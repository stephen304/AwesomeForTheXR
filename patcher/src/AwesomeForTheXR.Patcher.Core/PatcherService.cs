using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;

namespace AwesomeForTheXR.Patcher.Core
{
    public sealed class PatcherService
    {
        private const string AssemblyOnlyFormat = "AwesomeForTheXR/assembly-install/1";
        private const string NativePluginFormat = "AwesomeForTheXR/marker-install/2";
        private readonly string baseline, desired, nativePackageDirectory;
        private readonly Func<byte[], byte[]> rewrite;
        private readonly IDictionary<string, string> companionFiles;

        // A recipe is compiled code with fixed source and output digests. The
        // scaffold's synthetic recipes use this seam without shipping game data.
        public PatcherService(string originalAssemblySha256, string expectedPatchedAssemblySha256,
            Func<byte[], byte[]> rewriteOriginal)
            : this(originalAssemblySha256, expectedPatchedAssemblySha256, rewriteOriginal, null) { }

        public PatcherService(string originalAssemblySha256, string expectedPatchedAssemblySha256,
            Func<byte[], byte[]> rewriteOriginal, string nativePackageDirectory)
        {
            if (!PatchFiles.IsHash(originalAssemblySha256))
                throw new ArgumentException("The original SHA-256 is invalid.", nameof(originalAssemblySha256));
            if ((expectedPatchedAssemblySha256 == null) != (rewriteOriginal == null) ||
                (expectedPatchedAssemblySha256 != null && !PatchFiles.IsHash(expectedPatchedAssemblySha256)))
                throw new ArgumentException("The recipe needs a verified output SHA-256.");
            baseline = originalAssemblySha256.ToUpperInvariant();
            desired = expectedPatchedAssemblySha256?.ToUpperInvariant();
            rewrite = rewriteOriginal;
            this.nativePackageDirectory = nativePackageDirectory;
        }

        private PatcherService(string packageDirectory) : this(
            TestMarkerRecipe.OriginalHash, TestMarkerRecipe.PatchedHash, TestMarkerRecipe.Rewrite,
            packageDirectory ?? throw new ArgumentNullException(nameof(packageDirectory)))
        {
            companionFiles = new Dictionary<string, string>
            {
                ["Awesome.exe"] = "E913CB114A94EC190E45266AD44721A8EC3636D364CEA9CB0AECEC7717006CE2",
                ["Awesome_Data/Plugins/OculusPlugin.dll"] = "0C50EF33ACB01C8B1D3CF526CEE1A3FA64182EFE59F48CE3F3E3445AE883A9EF",
                ["Awesome_DirectToRift.exe"] = "47AD5AD4962138DF0A9AD26315E297AEA36343F150E292B805106A99E9BAB396"
            };
        }

        public static PatcherService ForGame() => ForGame(Path.Combine(AppContext.BaseDirectory, "patch"));

        public static PatcherService ForGame(string nativePackageDirectory) => new PatcherService(nativePackageDirectory);

        public PatchStatus Inspect(string folder)
        {
            try { return Check(new InstallPaths(folder, baseline)); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException || ex is SerializationException ||
                ex is System.Security.SecurityException)
            {
                return new PatchStatus(ex.Message);
            }
        }

        public void Patch(string folder) => Change(folder, false);
        public void Revert(string folder) => Change(folder, true);

        private PatchStatus Check(InstallPaths paths)
        {
            if (companionFiles != null)
                foreach (var pair in companionFiles)
                {
                    var path = paths.Resolve(pair.Key);
                    RequireFile(path, pair.Key);
                    if (PatchFiles.Digest(path) != pair.Value)
                        throw new InvalidDataException("File does not match the supported build: " + pair.Key);
                }

            RequireFile(paths.Live, InstallPaths.Assembly);
            string live = PatchFiles.Digest(paths.Live);
            var record = ReadRecord(paths);
            bool interrupted = File.Exists(paths.Journal);
            if (record != null || interrupted || File.Exists(paths.Original))
            {
                if (!File.Exists(paths.Original)) throw new InvalidDataException("Original backup is missing.");
                if (PatchFiles.Digest(paths.Original) != baseline)
                    throw new InvalidDataException("Original backup does not match the supported build.");
            }

            string native = PatchFiles.OptionalDigest(paths.Native);
            if (interrupted) ReadJournal(paths, live, native, record);
            else
            {
                if (live != baseline && live != record?.InstalledSha256)
                    throw new InvalidDataException("Unexpected file contents: " + InstallPaths.Assembly);
                if (native != record?.NativeSha256)
                    throw new InvalidDataException("Unrecognized, missing or modified plugin: " + InstallPaths.NativeName);
            }

            if (companionFiles != null && GameIsRunning(paths.Root))
                return new PatchStatus("Close Awesome to patch or revert.");

            if (interrupted)
                return new PatchStatus("Interrupted update. Revert will recover the original files.", false, true);
            if (rewrite == null) return new PatchStatus("A compatible game patch is required.", false, record != null);
            string desiredNative;
            try { ReadPackage(out desiredNative); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return new PatchStatus("Patch package unavailable. " + ex.Message, false, record != null);
            }
            if (record != null && live == desired && native == desiredNative)
                return new PatchStatus("Files already match this patch.", false, true);
            if (record != null && live != baseline)
                return new PatchStatus("Files match the previous patch. Update available.", true, true);
            return new PatchStatus("Files match the supported game build.", true, record != null);
        }

        private static bool GameIsRunning(string root)
        {
            var target = Path.Combine(root, "Awesome.exe");
            foreach (var process in Process.GetProcessesByName("Awesome"))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (InvalidOperationException) { /* Process exited during inspection. */ }
                    catch (System.ComponentModel.Win32Exception) { return true; }
                    catch (NotSupportedException) { return true; }
                }
            }
            return false;
        }

        private static void RequireFile(string path, string description)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Missing file: " + description);
        }

        private byte[] ReadPackage(out string digest)
        {
            digest = null;
            if (nativePackageDirectory == null) return null;
            string file = Path.Combine(nativePackageDirectory, InstallPaths.NativeName);
            RequireFile(file, InstallPaths.NativeName);
            RequireFile(file + ".sha256", InstallPaths.NativeName + ".sha256");
            digest = File.ReadAllText(file + ".sha256").Trim().ToUpperInvariant();
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0 || !PatchFiles.IsHash(digest) || PatchFiles.Digest(bytes) != digest)
                throw new InvalidDataException("Packaged plugin failed its SHA-256 check.");
            return bytes;
        }

        private InstallRecord ParseRecord(InstallPaths paths, byte[] bytes)
        {
            var r = PatchFiles.Deserialize<InstallRecord>(bytes);
            if (r == null || r.OriginalSha256 != baseline ||
                r.Backup != paths.OriginalRelative || !PatchFiles.IsHash(r.InstalledSha256))
                throw new InvalidDataException("Unrecognized installation manifest.");
            if (!(r.Format == AssemblyOnlyFormat && r.NativeSha256 == null) &&
                !(r.Format == NativePluginFormat && PatchFiles.IsHash(r.NativeSha256)))
                throw new InvalidDataException("Unrecognized installation manifest version or payload hash.");
            return r;
        }

        private InstallRecord ReadRecord(InstallPaths paths)
        {
            return File.Exists(paths.Record) ? ParseRecord(paths, File.ReadAllBytes(paths.Record)) : null;
        }

        private PendingWrite ReadJournal(InstallPaths paths, string live, string native, InstallRecord current)
        {
            var j = PatchFiles.Deserialize<PendingWrite>(File.ReadAllBytes(paths.Journal));
            if (j == null || j.PreviousAssembly == null || PatchFiles.Digest(j.PreviousAssembly) != j.BeforeHash ||
                !PatchFiles.IsHash(j.AfterHash) || (live != j.BeforeHash && live != j.AfterHash))
                throw new InvalidDataException("Interrupted update conflicts with existing files.");
            var old = j.PreviousManifest == null ? null : ParseRecord(paths, j.PreviousManifest);
            if ((j.PreviousNative == null ? null : PatchFiles.Digest(j.PreviousNative)) != j.BeforeNativeHash ||
                (j.AfterNativeHash != null && !PatchFiles.IsHash(j.AfterNativeHash)) ||
                (native != j.BeforeNativeHash && native != j.AfterNativeHash) || j.BeforeNativeHash != old?.NativeSha256)
                throw new InvalidDataException("Interrupted update conflicts with the native plugin.");
            bool currentIsOld = current?.InstalledSha256 == old?.InstalledSha256 && current?.NativeSha256 == old?.NativeSha256;
            bool currentIsNew = (current?.InstalledSha256 ?? baseline) == j.AfterHash && current?.NativeSha256 == j.AfterNativeHash;
            if ((j.BeforeHash != baseline && j.BeforeHash != old?.InstalledSha256) || (!currentIsOld && !currentIsNew))
                throw new InvalidDataException("Interrupted update conflicts with the installation manifest.");
            return j;
        }

        private void Change(string folder, bool revert)
        {
            var paths = new InstallPaths(folder, baseline);
            CheckAllowed(Check(paths), revert);
            Directory.CreateDirectory(paths.DirectoryPath);
            using (new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CheckAllowed(Check(paths), revert);
                if (File.Exists(paths.Journal))
                {
                    RollBack(paths);
                    if (!File.Exists(paths.Record) && PatchFiles.Digest(paths.Live) == baseline) return;
                    CheckAllowed(Check(paths), revert);
                }
                var current = ReadRecord(paths);
                byte[] before = File.ReadAllBytes(paths.Live);
                string beforeHash = PatchFiles.Digest(before);
                if (beforeHash != baseline && beforeHash != current?.InstalledSha256)
                    throw new IOException("Game files changed during verification.");

                if (!File.Exists(paths.Original))
                {
                    if (beforeHash != baseline) throw new IOException("A verified original is required.");
                    PatchFiles.Publish(paths.Original, before, false);
                }
                byte[] original = File.ReadAllBytes(paths.Original);
                if (PatchFiles.Digest(original) != baseline) throw new IOException("Original backup failed verification.");
                byte[] candidate = revert ? original : beforeHash == desired ? before : rewrite(original);
                string afterHash = revert ? baseline : desired;
                if (candidate == null || PatchFiles.Digest(candidate) != afterHash)
                    throw new InvalidDataException("The patch recipe produced unexpected output.");

                byte[] beforeNative = File.Exists(paths.Native) ? File.ReadAllBytes(paths.Native) : null;
                string beforeNativeHash = beforeNative == null ? null : PatchFiles.Digest(beforeNative);
                if (beforeNativeHash != current?.NativeSha256)
                    throw new IOException("The installed plugin changed during verification.");
                string afterNativeHash = null;
                byte[] afterNative = revert ? null : ReadPackage(out afterNativeHash);

                var transaction = new PendingWrite
                {
                    BeforeHash = beforeHash,
                    AfterHash = afterHash,
                    PreviousNative = beforeNative,
                    BeforeNativeHash = beforeNativeHash,
                    AfterNativeHash = afterNativeHash,
                    PreviousAssembly = before,
                    PreviousManifest = current == null ? null : File.ReadAllBytes(paths.Record)
                };
                PatchFiles.Publish(paths.Journal, PatchFiles.Serialize(transaction), false);
                try
                {
                    // Put the plugin in place before enabling the managed call.
                    // On revert, disable that call before removing the plugin.
                    if (!revert) PatchFiles.Transition(paths.Native, beforeNativeHash, afterNativeHash, afterNative);
                    PatchFiles.Transition(paths.Live, beforeHash, afterHash, candidate);
                    if (revert) PatchFiles.Transition(paths.Native, beforeNativeHash, null, null);
                    if (revert) File.Delete(paths.Record);
                    else
                    {
                        var record = new InstallRecord { Format = afterNativeHash == null ? AssemblyOnlyFormat : NativePluginFormat,
                            NativeSha256 = afterNativeHash, OriginalSha256 = baseline,
                            InstalledSha256 = afterHash, Backup = paths.OriginalRelative };
                        PatchFiles.Publish(paths.Record, PatchFiles.Serialize(record), current != null);
                    }
                    File.Delete(paths.Journal);
                }
                catch
                {
                    // Keep the journal if recovery itself encounters a changed or locked file.
                    RollBack(paths);
                    throw;
                }
            }
        }

        private static void CheckAllowed(PatchStatus state, bool revert)
        {
            if (!(revert ? state.CanRevert : state.CanPatch))
                throw new InvalidOperationException(state.Message);
        }

        private void RollBack(InstallPaths paths)
        {
            string live = PatchFiles.Digest(paths.Live);
            string native = PatchFiles.OptionalDigest(paths.Native);
            var j = ReadJournal(paths, live, native, ReadRecord(paths));
            PatchFiles.Transition(paths.Native, native, j.BeforeNativeHash, j.PreviousNative);
            PatchFiles.Transition(paths.Live, live, j.BeforeHash, j.PreviousAssembly);
            if (j.PreviousManifest == null) File.Delete(paths.Record);
            else PatchFiles.Publish(paths.Record, j.PreviousManifest, File.Exists(paths.Record));
            if (PatchFiles.Digest(paths.Live) != j.BeforeHash) throw new IOException("Rollback verification failed.");
            File.Delete(paths.Journal);
        }
    }
}
