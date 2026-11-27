using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AwesomePatcher.Core
{
    // Windows-only guard for an administrator file worker. Ordinary Patch/Revert never needs these handles.
    internal sealed class ElevatedDirectories : IDisposable
    {
        internal const int CannotSecure = unchecked((int)0x80040201);
        internal const string Refusal = "Administrator access requires a local NTFS game path whose directories can be locked and contain no links.";
        private readonly Dictionary<string, SafeFileHandle> directories =
            new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SafeFileHandle> anchors = new List<SafeFileHandle>();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string module);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        internal static bool NativeWindows => Environment.OSVersion.Platform == PlatformID.Win32NT &&
            GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") == IntPtr.Zero;

        private enum Disposition : uint { Open = 1, Create = 2, OpenIf = 3 }
        private const uint DirectoryFile = 1, NonDirectoryFile = 0x40, DeleteOnClose = 0x1000, DeleteAccess = 0x10000;
        private const uint DirectoryAccess = 0x0001 | 0x0080; // list directory and read attributes
        private const uint OpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { internal FileAttributes Attributes; internal uint ReparseTag; }
        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeName { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            internal int Length;
            internal IntPtr Root, Name;
            internal uint Flags;
            internal IntPtr Security, SecurityQuality;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatus { internal IntPtr Status, Information; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
            FileMode mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
            out AttributeTag info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
            out IoStatus status, IntPtr allocation, uint fileAttributes, FileShare share, Disposition disposition,
            uint options, IntPtr extendedAttributes, uint extendedLength);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int RtlNtStatusToDosError(int status);

        private static IOException Refuse(string path, int error = 0) =>
            new IOException(Refusal + " " + path + (error == 0 ? "" : ". " + new Win32Exception(error).Message), CannotSecure);

        // Resolve one child relative to an already opened directory, including when creating the backup tree.
        // No string-based create runs through an unverified ancestor.
        private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, uint access, FileShare share,
            Disposition disposition, uint options)
        {
            var bytes = checked((ushort)(name.Length * 2));
            var chars = Marshal.StringToHGlobalUni(name);
            try
            {
                var unicode = new UnicodeName { Length = bytes, MaximumLength = bytes, Buffer = chars };
                var pinned = GCHandle.Alloc(unicode, GCHandleType.Pinned);
                try
                {
                    var attributes = new ObjectAttributes {
                        Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(),
                        Name = pinned.AddrOfPinnedObject(), Flags = 0x40 | 0x1000 // CASE_INSENSITIVE | DONT_REPARSE
                    };
                    int result = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0,
                        share, disposition, options | OpenReparsePoint, IntPtr.Zero, 0);
                    if (result == 0 && handle != null && !handle.IsInvalid) return handle;
                    handle?.Dispose();
                    throw Refuse(name, RtlNtStatusToDosError(result));
                }
                finally { pinned.Free(); }
            }
            finally { Marshal.FreeHGlobal(chars); }
        }

        private static void CheckDirectory(string path, SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8))
                throw Refuse(path, Marshal.GetLastWin32Error());
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw Refuse(path);
        }

        private SafeFileHandle Hold(string path, bool create)
        {
            if (directories.TryGetValue(path, out var existing)) return existing;
            string parent = Path.GetDirectoryName(path);
            SafeFileHandle handle;
            if (parent == null)
            {
                if (path.Length != 3) throw Refuse(path);
                var drive = new DriveInfo(path);
                if (drive.DriveType == DriveType.Network || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw Refuse(path);
                handle = CreateFileW(path, DirectoryAccess, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
                    0x02000000 /* BACKUP_SEMANTICS */ | OpenReparsePoint, IntPtr.Zero);
            }
            else handle = OpenChild(Hold(parent, create), Path.GetFileName(path), DirectoryAccess,
                FileShare.ReadWrite, create ? Disposition.OpenIf : Disposition.Open, DirectoryFile);
            try
            {
                if (handle.IsInvalid) throw Refuse(path, Marshal.GetLastWin32Error());
                CheckDirectory(path, handle);
                if (parent == null)
                {
                    var actual = new StringBuilder(260);
                    if (GetFinalPathNameByHandleW(handle, actual, (uint)actual.Capacity, 0) == 0 ||
                        !string.Equals(actual.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) throw Refuse(path);
                }
                directories.Add(path, handle);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        internal void AcquireForTransaction(InstallPaths paths)
        {
            Anchor(Path.GetDirectoryName(paths.Live));
            Anchor(Path.GetDirectoryName(paths.Native));
            Anchor(paths.DirectoryPath, create: true);
            foreach (var pair in directories) CheckDirectory(pair.Key, pair.Value);
        }

        private void Anchor(string directory, bool create = false)
        {
            var parent = Hold(directory, create);
            // Denying delete sharing prevents replacement. An open child keeps the leaf nonempty,
            // which also prevents NTFS converting it into a junction in place. Ancestors have a pinned child directory.
            anchors.Add(OpenChild(parent, Path.GetRandomFileName(), DeleteAccess, FileShare.None,
                Disposition.Create, NonDirectoryFile | DeleteOnClose));
        }

        public void Dispose()
        {
            foreach (var anchor in anchors) anchor.Dispose();
            foreach (var directory in directories.Values) directory.Dispose();
        }
    }
}
