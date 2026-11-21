using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;

namespace AwesomeForTheXR.Patcher.Core
{
    public sealed class PatcherService
    {
        private const string AssemblyOnlyFormat = "AwesomeForTheXR/assembly-install/1";
        private const string NativePluginFormat = "AwesomeForTheXR/marker-install/2";
        private const string LauncherFormat = "AwesomeForTheXR/launcher-install/3";
        private const string OpenXRFormat = "AwesomeForTheXR/openxr-install/4";
        private const string ArtworkFormat = "AwesomeForTheXR/openxr-install/5";
        private readonly bool hasLaunchers;
        private readonly IArtworkCatalog artworkCatalog;
        private static LauncherFile Player(InstallPaths paths) => new LauncherFile(paths, PlayerRecipe.Player, PlayerRecipe.PlayerOriginal);
        private static LauncherFile Forwarder(InstallPaths paths) => new LauncherFile(paths, PlayerRecipe.Helper, PlayerRecipe.HelperOriginal);
        private readonly string baseline, desired, nativePackageDirectory;
        private readonly Func<byte[], byte[]> rewrite;

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

        internal PatcherService(string packageDirectory, IArtworkCatalog artworkCatalog) : this(
            GameAssemblyRecipe.OriginalHash, GameAssemblyRecipe.PatchedHash, GameAssemblyRecipe.Rewrite,
            packageDirectory ?? throw new ArgumentNullException(nameof(packageDirectory)))
        {
            hasLaunchers = true;
            this.artworkCatalog = artworkCatalog ?? throw new ArgumentNullException(nameof(artworkCatalog));
        }

        public static PatcherService ForGame() => ForGame(Path.Combine(AppContext.BaseDirectory, "patch"));

        public static PatcherService ForGame(string nativePackageDirectory) => new PatcherService(nativePackageDirectory, new SteamVrCatalog());

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
            if (hasLaunchers)
            {
                const string oculus = "Awesome_Data/Plugins/OculusPlugin.dll";
                if (PatchFiles.OptionalDigest(paths.Resolve(oculus)) != "0C50EF33ACB01C8B1D3CF526CEE1A3FA64182EFE59F48CE3F3E3445AE883A9EF")
                    throw new InvalidDataException("File does not match the supported build: " + oculus);
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
            string player = hasLaunchers ? Player(paths).Inspect(record?.PlayerSha256, interrupted) : null;
            string helper = hasLaunchers ? Forwarder(paths).Inspect(record?.ForwarderSha256, interrupted) : null;
            string loader = hasLaunchers ? PatchFiles.OptionalDigest(paths.Resolve(InstallPaths.LoaderName)) : null;
            if (!interrupted && loader != record?.LoaderSha256)
                throw new InvalidDataException("Unrecognized, missing or modified OpenXR loader: " + InstallPaths.LoaderName);
            if (hasLaunchers && !interrupted) SteamVrArtwork.VerifyFile(paths, record);
            if (interrupted) ReadJournal(paths, live, native, record);
            else
            {
                if (live != baseline && live != record?.InstalledSha256)
                    throw new InvalidDataException("Unexpected file contents: " + InstallPaths.Assembly);
                if (native != record?.NativeSha256)
                    throw new InvalidDataException("Unrecognized, missing or modified plugin: " + InstallPaths.NativeName);
            }

            if (hasLaunchers && GameIsRunning(paths.Root))
                return new PatchStatus("Close Awesome to patch or revert.");

            if (interrupted)
                return new PatchStatus("Interrupted update. Revert will recover the original files.", false, true);
            if (rewrite == null) return new PatchStatus("A compatible game patch is required.", false, record != null);
            string desiredNative;
            bool launchersCurrent = true;
            try
            {
                ReadPackage(InstallPaths.NativeName, out desiredNative);
                if (hasLaunchers)
                {
                    ReadPackage(PlayerRecipe.Helper, out var forwarderHash);
                    ReadPackage(PlayerRecipe.Startup, out var startupHash);
                    ReadPackage(InstallPaths.LoaderName, out var loaderHash);
                    launchersCurrent = loader == loaderHash && record?.StartupSha256 == startupHash && helper == forwarderHash && player == record.PlayerSha256;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return new PatchStatus("Patch package unavailable. " + ex.Message, false, record != null);
            }
            if (record != null && live == desired && native == desiredNative && launchersCurrent)
            {
                if (hasLaunchers) return SteamVrArtwork.InstalledStatus(paths, record, artworkCatalog);
                return new PatchStatus("Files already match this patch.", canRevert: true, isCurrent: true);
            }
            if (record != null && live != baseline)
                return new PatchStatus("Files match the previous patch. Update available.", true, true);
            return new PatchStatus("Files match the supported game build.", true, record != null);
        }

        private static bool GameIsRunning(string root)
        {
            return ExecutableIsRunning(root, PlayerRecipe.Player) || ExecutableIsRunning(root, PlayerRecipe.Helper);
        }

        private static bool ExecutableIsRunning(string root, string name)
        {
            var target = Path.Combine(root, name);
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
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

        private byte[] ReadPackage(string name, out string digest)
        {
            digest = null;
            if (nativePackageDirectory == null) return null;
            string file = Path.Combine(nativePackageDirectory, name);
            RequireFile(file, name);
            RequireFile(file + ".sha256", name + ".sha256");
            digest = File.ReadAllText(file + ".sha256").Trim().ToUpperInvariant();
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0 || !PatchFiles.IsHash(digest) || PatchFiles.Digest(bytes) != digest)
                throw new InvalidDataException("Packaged file failed its SHA-256 check: " + name);
            return bytes;
        }

        private InstallRecord ParseRecord(InstallPaths paths, byte[] bytes)
        {
            var r = PatchFiles.Deserialize<InstallRecord>(bytes);
            if (r == null || r.OriginalSha256 != baseline ||
                r.Backup != paths.OriginalRelative || !PatchFiles.IsHash(r.InstalledSha256))
                throw new InvalidDataException("Unrecognized installation manifest.");
            bool legacy = r.LoaderSha256 == null && r.PlayerSha256 == null && r.ForwarderSha256 == null && r.StartupSha256 == null &&
                ((r.Format == AssemblyOnlyFormat && r.NativeSha256 == null) ||
                 (r.Format == NativePluginFormat && PatchFiles.IsHash(r.NativeSha256)));
            bool launcherVersion = (r.Format == LauncherFormat && r.LoaderSha256 == null) ||
                ((r.Format == OpenXRFormat || r.Format == ArtworkFormat) && PatchFiles.IsHash(r.LoaderSha256));
            bool launchers = hasLaunchers && launcherVersion && PatchFiles.IsHash(r.NativeSha256) &&
                PatchFiles.IsHash(r.PlayerSha256) && PatchFiles.IsHash(r.ForwarderSha256) && PatchFiles.IsHash(r.StartupSha256);
            bool validArtwork = r.Format == ArtworkFormat ? PatchFiles.IsHash(r.ArtworkSha256) : r.ArtworkSha256 == null;
            if ((!legacy && !launchers) || !validArtwork)
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
            if (hasLaunchers)
            {
                j.Artwork?.Verify(paths, old);
                string loader = PatchFiles.OptionalDigest(paths.Resolve(InstallPaths.LoaderName));
                bool savedLoader = j.BeforeLoaderHash != j.AfterLoaderHash && j.BeforeLoaderHash != null;
                if (j.BeforeLoaderHash != old?.LoaderSha256 ||
                    (j.AfterLoaderHash != null && !PatchFiles.IsHash(j.AfterLoaderHash)) ||
                    (loader != j.BeforeLoaderHash && loader != j.AfterLoaderHash) ||
                    (savedLoader ? j.PreviousLoader == null || PatchFiles.Digest(j.PreviousLoader) != j.BeforeLoaderHash
                                 : j.PreviousLoader != null))
                    throw new InvalidDataException("Interrupted update conflicts with the OpenXR loader.");
            }
            bool currentIsOld = current?.InstalledSha256 == old?.InstalledSha256 && current?.NativeSha256 == old?.NativeSha256 &&
                current?.PlayerSha256 == old?.PlayerSha256 && current?.ForwarderSha256 == old?.ForwarderSha256 &&
                current?.StartupSha256 == old?.StartupSha256 && current?.LoaderSha256 == old?.LoaderSha256 &&
                current?.ArtworkSha256 == old?.ArtworkSha256;
            bool currentIsNew = (current?.InstalledSha256 ?? baseline) == j.AfterHash && current?.NativeSha256 == j.AfterNativeHash &&
                current?.LoaderSha256 == j.AfterLoaderHash && current?.ArtworkSha256 == j.Artwork?.After;
            if ((j.BeforeHash != baseline && j.BeforeHash != old?.InstalledSha256) || (!currentIsOld && !currentIsNew))
                throw new InvalidDataException("Interrupted update conflicts with the installation manifest.");
            if (hasLaunchers)
            {
                if ((j.Player == null) != (j.Forwarder == null))
                    throw new InvalidDataException("Incomplete launcher transaction.");
                Player(paths).CheckJournal(j.Player, old?.PlayerSha256, current?.PlayerSha256, !currentIsOld);
                Forwarder(paths).CheckJournal(j.Forwarder, old?.ForwarderSha256, current?.ForwarderSha256, !currentIsOld);
            }
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
                byte[] afterNative = revert ? null : ReadPackage(InstallPaths.NativeName, out afterNativeHash);
                string loaderPath = hasLaunchers ? paths.Resolve(InstallPaths.LoaderName) : null;
                string beforeLoaderHash = current?.LoaderSha256, afterLoaderHash = null;
                byte[] afterLoader = null, previousLoader = null;
                if (hasLaunchers)
                {
                    if (PatchFiles.OptionalDigest(loaderPath) != beforeLoaderHash)
                        throw new IOException("The installed OpenXR loader changed during verification.");
                    if (!revert) afterLoader = ReadPackage(InstallPaths.LoaderName, out afterLoaderHash);
                    if (beforeLoaderHash != null && beforeLoaderHash != afterLoaderHash)
                    {
                        previousLoader = File.ReadAllBytes(loaderPath);
                        if (PatchFiles.Digest(previousLoader) != beforeLoaderHash)
                            throw new IOException("The OpenXR loader rollback snapshot failed verification.");
                    }
                }
                string startupHash = null;
                byte[] playerBytes = null, forwarderBytes = null;
                ExecutableChange playerChange = null, forwarderChange = null;
                if (hasLaunchers)
                {
                    playerBytes = Player(paths).Original();
                    forwarderBytes = Forwarder(paths).Original();
                    if (!revert)
                    {
                        playerBytes = PlayerRecipe.Rewrite(playerBytes, ReadPackage(PlayerRecipe.Startup, out startupHash));
                        forwarderBytes = ReadPackage(PlayerRecipe.Helper, out _);
                    }
                    playerChange = Player(paths).Prepare(current?.PlayerSha256, playerBytes);
                    forwarderChange = Forwarder(paths).Prepare(current?.ForwarderSha256, forwarderBytes);
                }

                var artwork = hasLaunchers ? SteamVrArtwork.Prepare(paths, current, revert, artworkCatalog) : null;
                var transaction = new PendingWrite
                {
                    Artwork = artwork, Player = playerChange, Forwarder = forwarderChange,
                    BeforeHash = beforeHash,
                    AfterHash = afterHash,
                    PreviousLoader = previousLoader, BeforeLoaderHash = beforeLoaderHash, AfterLoaderHash = afterLoaderHash,
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
                    if (!revert && hasLaunchers) PatchFiles.Transition(loaderPath, beforeLoaderHash, afterLoaderHash, afterLoader);
                    if (!revert) PatchFiles.Transition(paths.Native, beforeNativeHash, afterNativeHash, afterNative);
                    PatchFiles.Transition(paths.Live, beforeHash, afterHash, candidate);
                    if (hasLaunchers)
                    {
                        Player(paths).Apply(playerChange, playerBytes);
                        Forwarder(paths).Apply(forwarderChange, forwarderBytes);
                    }
                    if (revert) PatchFiles.Transition(paths.Native, beforeNativeHash, null, null);
                    if (revert && hasLaunchers) PatchFiles.Transition(loaderPath, beforeLoaderHash, null, null);
                    artwork?.Apply(paths, artworkCatalog);
                    if (revert) File.Delete(paths.Record);
                    else
                    {
                        var record = new InstallRecord { ArtworkSha256 = artwork?.After, LoaderSha256 = afterLoaderHash, Format = hasLaunchers ? ArtworkFormat : afterNativeHash == null ? AssemblyOnlyFormat : NativePluginFormat,
                            PlayerSha256 = playerChange?.After, ForwarderSha256 = forwarderChange?.After, StartupSha256 = startupHash,
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
                if (hasLaunchers)
                {
                    Player(paths).DiscardSnapshot(playerChange);
                    Forwarder(paths).DiscardSnapshot(forwarderChange);
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
            if (hasLaunchers)
            {
                j.Artwork?.Undo(paths, artworkCatalog);
                var loaderPath = paths.Resolve(InstallPaths.LoaderName);
                string loader = PatchFiles.OptionalDigest(loaderPath);
                if (loader != j.BeforeLoaderHash)
                    PatchFiles.Transition(loaderPath, loader, j.BeforeLoaderHash, j.PreviousLoader);
                Forwarder(paths).RollBack(j.Forwarder);
                Player(paths).RollBack(j.Player);
            }
            PatchFiles.Transition(paths.Native, native, j.BeforeNativeHash, j.PreviousNative);
            PatchFiles.Transition(paths.Live, live, j.BeforeHash, j.PreviousAssembly);
            if (j.PreviousManifest == null) File.Delete(paths.Record);
            else PatchFiles.Publish(paths.Record, j.PreviousManifest, File.Exists(paths.Record));
            if (PatchFiles.Digest(paths.Live) != j.BeforeHash) throw new IOException("Rollback verification failed.");
            File.Delete(paths.Journal);
            if (hasLaunchers)
            {
                Player(paths).DiscardSnapshot(j.Player);
                Forwarder(paths).DiscardSnapshot(j.Forwarder);
            }
        }
    }
}
