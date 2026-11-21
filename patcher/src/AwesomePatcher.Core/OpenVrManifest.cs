using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AwesomePatcher.Core
{
    internal static class OpenVrManifest
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32", ExactSpelling = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint Init(ref int error, int type, IntPtr startupInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Shutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetInterface([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
        // The first two slots of Valve's IVRApplications_008 C function table.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Add(byte[] utf8Path, [MarshalAs(UnmanagedType.I1)] bool temporary);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Remove(byte[] utf8Path);

        private static T Export<T>(IntPtr module, string name) where T : class
        {
            var address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new IOException("SteamVR does not export " + name + ".");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        internal static void SetRegistered(string library, string manifest, bool register)
        {
            // Limit transitive DLL lookup to the SteamVR DLL directory and System32.
            var module = LoadLibraryExW(library, IntPtr.Zero, 0x100 | 0x800);
            if (module == IntPtr.Zero) throw new IOException("Could not load the SteamVR application API.", new Win32Exception());
            try
            {
                var shutdown = Export<Shutdown>(module, "VR_ShutdownInternal");
                int error = 0;
                Export<Init>(module, "VR_InitInternal2")(ref error, 4 /* VRApplication_Utility */, IntPtr.Zero);
                if (error != 0) throw new IOException("SteamVR utility initialization failed (" + error + ").");
                try
                {
                    var table = Export<GetInterface>(module, "VR_GetGenericInterface")("FnTable:IVRApplications_008", ref error);
                    if (table == IntPtr.Zero || error != 0) throw new IOException("SteamVR applications interface unavailable (" + error + ").");
                    var path = Encoding.UTF8.GetBytes(manifest + "\0");
                    int result = register
                        ? Marshal.GetDelegateForFunctionPointer<Add>(Marshal.ReadIntPtr(table))(path, false)
                        : Marshal.GetDelegateForFunctionPointer<Remove>(Marshal.ReadIntPtr(table, IntPtr.Size))(path);
                    if (result != 0) throw new IOException("SteamVR artwork " + (register ? "registration" : "removal") + " failed (" + result + ").");
                }
                finally { shutdown(); }
            }
            finally { FreeLibrary(module); }
        }
    }
}
