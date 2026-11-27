using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomePatcher.Core;

namespace AwesomePatcher.IntegrationTests
{
    internal static partial class Program
    {
        private sealed class TestArtworkCatalog : IArtworkCatalog
        {
            public bool Available = true, Readable = true, OtherProvider, Registered, FailAdd, FailRemoval;
            public int RemovalCalls;
            public bool LastRemovalHadFile;
            public ArtworkRegistration Inspect(string path, bool includeOtherProviders = true) =>
                (Available ? ArtworkRegistration.Available : 0) | (Readable ? ArtworkRegistration.CatalogRead : 0) |
                (includeOtherProviders && OtherProvider ? ArtworkRegistration.OtherProvider : 0) |
                (Readable && Registered ? ArtworkRegistration.OwnedPath : 0);

            public bool Reconcile(string path, bool installed)
            {
                if (!Readable || (installed && !Available)) return false;
                bool registered = installed && !OtherProvider;
                if (registered == Registered) return true;
                if (!registered)
                {
                    RemovalCalls++;
                    LastRemovalHadFile = File.Exists(path);
                    if (FailRemoval) throw new IOException("Simulated removal failure.");
                }
                if (!Available || (registered && (!File.Exists(path) || OtherProvider)))
                    throw new InvalidOperationException("Unexpected artwork operation.");
                if (registered && FailAdd) throw new IOException("Simulated registration failure.");
                Registered = registered;
                return true;
            }
        }

        private static void TestCoordinator(string temp)
        {
            string root = Directory.CreateDirectory(Path.Combine(temp, "coordinator")).FullName;
            string managed = Directory.CreateDirectory(Path.Combine(root, "Awesome_Data", "Managed")).FullName;
            File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), Original);
            // The synthetic recipe only owns the assembly. Supply the manifest to the fake SteamVR runtime.
            File.WriteAllText(Path.Combine(root, SteamVrArtwork.Name), "{}");
            var files = Recipe(FirstBuild);
            var catalog = new TestArtworkCatalog();
            var attempts = new List<bool>();
            bool allowed = false, fileInUse = false, cancel = false;
            var coordinator = new PatchCoordinator(files, catalog, (folder, revert, elevated) =>
            {
                attempts.Add(elevated);
                if (!elevated && fileInUse) throw new IOException("File in use.");
                if (!elevated) throw new UnauthorizedAccessException("Protected game folder.");
                if (cancel) throw new OperationCanceledException();
                files.Change(folder, revert, elevated);
                if (revert) File.Delete(Path.Combine(folder, SteamVrArtwork.Name));
            }, () => allowed);

            try { coordinator.Patch(root); throw new InvalidOperationException("Expected access denial."); }
            catch (UnauthorizedAccessException) { }
            if (!attempts.SequenceEqual(new[] { false })) throw new InvalidOperationException("Unexpected elevation when unavailable.");

            allowed = true;
            fileInUse = true;
            attempts.Clear();
            try { coordinator.Patch(root); throw new InvalidOperationException("Expected file-in-use failure."); }
            catch (IOException) { }
            if (!attempts.SequenceEqual(new[] { false })) throw new InvalidOperationException("Retried a non-permission error.");

            fileInUse = false;
            cancel = true;
            attempts.Clear();
            try { coordinator.Patch(root); throw new InvalidOperationException("Expected UAC cancellation."); }
            catch (OperationCanceledException) { }
            if (!attempts.SequenceEqual(new[] { false, true }) || catalog.Registered)
                throw new InvalidOperationException("Cancellation changed the retry sequence or registration.");

            cancel = false;
            attempts.Clear();
            coordinator.Patch(root);
            if (!attempts.SequenceEqual(new[] { false, true }) || !catalog.Registered)
                throw new InvalidOperationException("Expected one elevated retry followed by normal registration.");
            attempts.Clear();
            catalog.Registered = false;
            coordinator.Patch(root);
            if (attempts.Count != 0 || !catalog.Registered) throw new InvalidOperationException("Artwork-only repair started a worker.");
            cancel = true;
            try { coordinator.Revert(root); throw new InvalidOperationException("Expected UAC cancellation on Revert."); }
            catch (OperationCanceledException) { }
            if (!attempts.SequenceEqual(new[] { false, true }) || !catalog.Registered || catalog.RemovalCalls != 0)
                throw new InvalidOperationException("Cancelled Revert touched the artwork registration.");

            cancel = false;
            catalog.FailRemoval = true;
            attempts.Clear();
            try { coordinator.Revert(root); throw new InvalidOperationException("Expected artwork removal failure."); }
            catch (IOException) { }
            if (!attempts.SequenceEqual(new[] { false, true }) || catalog.LastRemovalHadFile ||
                !catalog.Registered || !files.Inspect(root).FilesOriginal || !coordinator.Inspect(root).CanRevert)
                throw new InvalidOperationException("Failed removal did not leave only registration to retry.");

            attempts.Clear();
            catalog.FailRemoval = false;
            coordinator.Revert(root);
            if (attempts.Count != 0 || catalog.Registered || coordinator.Inspect(root).CanRevert)
                throw new InvalidOperationException("Artwork-only Revert did not finish without a worker.");
            File.WriteAllText(Path.Combine(root, SteamVrArtwork.Name), "{}");
            coordinator.Patch(root);
            catalog.Readable = false;
            if (coordinator.Revert(root) || !catalog.Registered || !files.Inspect(root).FilesOriginal)
                throw new InvalidOperationException("Undiscoverable catalog reported confirmed cleanup.");
            var inspected = coordinator.Inspect(root);
            if (!inspected.CanPatch || inspected.CanRevert || inspected.Message != files.Inspect(root).Message)
                throw new InvalidOperationException("Folder verification included a cleanup result without a visible registration.");
            catalog.Readable = true;
            attempts.Clear();
            if (!coordinator.Inspect(root).CanRevert || !coordinator.Revert(root) || attempts.Count != 0 || catalog.Registered)
                throw new InvalidOperationException("Rediscovered stale artwork could not be removed without a worker.");
            Console.WriteLine("PASS: one-time UAC retry, cancellation and worker-free artwork install/removal retries.");
        }

        private static void TestPartialPatchRetry(string temp, bool decline)
        {
            var root = Directory.CreateDirectory(Path.Combine(temp, "partial patch " + decline)).FullName;
            Directory.CreateDirectory(Path.Combine(root, "Awesome_Data", "Managed"));
            var paths = new InstallPaths(root, Hash(Original));
            File.WriteAllBytes(paths.Live, Original);
            File.WriteAllText(Path.Combine(root, SteamVrArtwork.Name), "{}");
            var files = Recipe(FirstBuild);
            var catalog = new TestArtworkCatalog();
            var attempts = new List<bool>();
            var coordinator = new PatchCoordinator(files, catalog, (folder, revert, elevated) =>
            {
                attempts.Add(elevated);
                if (!elevated && !revert)
                {
                    // First install stopped after its assembly write and before its record was published.
                    Directory.CreateDirectory(paths.DirectoryPath);
                    File.WriteAllBytes(paths.Original, Original);
                    File.WriteAllBytes(paths.Journal, PatchFiles.Serialize(new PendingWrite {
                        BeforeHash = Hash(Original), AfterHash = Hash(FirstBuild), PreviousAssembly = Original
                    }));
                    File.WriteAllBytes(paths.Live, FirstBuild);
                    throw new UnauthorizedAccessException("Rollback needs administrator access.");
                }
                if (elevated && decline) throw new OperationCanceledException();
                files.Change(folder, revert, elevated);
            }, () => true);

            if (decline)
            {
                try { coordinator.Patch(root); throw new InvalidOperationException("Expected cancellation."); }
                catch (OperationCanceledException) { }
                if (!files.Inspect(root).Interrupted || !File.Exists(paths.Journal) || catalog.Registered)
                    throw new InvalidOperationException("A declined partial Patch lost its recovery state.");
                CheckBytes(paths.Live, FirstBuild, "partial write present before recovery");
                coordinator.Revert(root);
                CheckBytes(paths.Live, Original, "recovered original after cancellation");
                if (File.Exists(paths.Record) || catalog.Registered || !attempts.SequenceEqual(new[] { false, true, false }))
                    throw new InvalidOperationException("Revert after cancellation did not restore the original state.");
            }
            else
            {
                coordinator.Patch(root);
                if (!files.Inspect(root).IsCurrent || !catalog.Registered || !attempts.SequenceEqual(new[] { false, true }))
                    throw new InvalidOperationException("Elevated retry did not finish its interrupted Patch.");
            }
            if (File.Exists(paths.Journal)) throw new InvalidOperationException("Recovered Patch left its journal.");
        }

        private static void TestArtworkDiscovery(string temp)
        {
            var runtime = Environment.GetEnvironmentVariable("VR_OVERRIDE");
            var config = Environment.GetEnvironmentVariable("VR_CONFIG_PATH");
            try
            {
                Environment.SetEnvironmentVariable("VR_OVERRIDE", null);
                Environment.SetEnvironmentVariable("VR_CONFIG_PATH", null);
                CheckArtworkDiscovery(temp);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VR_OVERRIDE", runtime);
                Environment.SetEnvironmentVariable("VR_CONFIG_PATH", config);
            }
        }

        private static void CheckArtworkDiscovery(string temp)
        {
            var root = Directory.CreateDirectory(Path.Combine(temp, "SteamVR catalog")).FullName;
            var registry = Path.Combine(root, "openvrpaths.vrpath");
            var manifest = Path.Combine(root, SteamVrArtwork.Name);
            var storedPath = manifest.ToUpperInvariant().Replace('\\', '/');
            var otherPath = Path.Combine(root, "other.vrmanifest");
            string malformed = root + "\\bad\u0000path";
            var registrations = new List<string> { malformed, otherPath, storedPath, manifest };
            string config = Path.Combine(root, "appconfig.json");
            void SaveCatalog() => File.WriteAllText(config, "{\"manifest_paths\":" +
                System.Text.Encoding.UTF8.GetString(PatchFiles.Serialize(registrations.ToArray())) + "}");
            bool reportError = false, saveUpdate = true, loseCatalog = false;
            var catalog = new SteamVrCatalog(registry, (library, path, register) =>
            {
                if (saveUpdate)
                {
                    if (register) registrations.Add(path);
                    else if (!registrations.Remove(path)) throw new IOException("SteamVR artwork removal failed (107).");
                    SaveCatalog();
                }
                if (loseCatalog) File.Move(config, config + ".saved");
                if (reportError) throw new IOException("SteamVR reported an update error.");
            });
            if (catalog.Inspect(manifest) != ArtworkRegistration.None || catalog.Reconcile(manifest, false))
                throw new InvalidOperationException("Unexpected SteamVR installation.");
            string escaped = root.Replace("\\", "\\\\");
            File.WriteAllText(registry, "{\"config\":[\"" + escaped + "\"],\"runtime\":[\"" + escaped + "\"]}");
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin", IntPtr.Size == 8 ? "win64" : "win32"));
            File.WriteAllBytes(Path.Combine(bin.FullName, "openvr_api.dll"), Array.Empty<byte>());
            using (var resource = typeof(PatcherService).Assembly.GetManifestResourceStream(SteamVrArtwork.Name))
            using (var output = File.Create(otherPath)) resource.CopyTo(output);
            SaveCatalog();
            var expected = ArtworkRegistration.CatalogRead | ArtworkRegistration.Available | ArtworkRegistration.OwnedPath | ArtworkRegistration.OtherProvider;
            if (catalog.Inspect(manifest) != expected) throw new InvalidOperationException("Manifest ownership detection failed.");
            string discoveredPath = manifest.ToLowerInvariant();
            catalog.Reconcile(discoveredPath, false);
            catalog.Reconcile(discoveredPath, false);
            if (!registrations.SequenceEqual(new[] { malformed, otherPath }) ||
                catalog.Inspect(manifest) != (ArtworkRegistration.CatalogRead | ArtworkRegistration.Available | ArtworkRegistration.OtherProvider))
                throw new InvalidOperationException("Artwork removal failed to preserve the other provider.");
            registrations.Add(storedPath);
            SaveCatalog();
            reportError = true;
            catalog.Reconcile(discoveredPath, false);
            if (File.Exists(manifest) || !registrations.SequenceEqual(new[] { malformed, otherPath }))
                throw new InvalidOperationException("Post-removal error was not reconciled against the catalog.");

            registrations.Remove(otherPath);
            SaveCatalog();
            if (!catalog.Reconcile(discoveredPath, true) || !registrations.Contains(discoveredPath))
                throw new InvalidOperationException("A committed registration was reported as failed.");
            catalog.Reconcile(discoveredPath, false);
            saveUpdate = false;
            try { catalog.Reconcile(discoveredPath, true); throw new InvalidOperationException("Uncommitted add reported success."); }
            catch (IOException) { }

            File.Move(registry, registry + ".saved");
            registrations.Add(storedPath);
            SaveCatalog();
            if (catalog.Reconcile(discoveredPath, false) || !registrations.Contains(storedPath))
                throw new InvalidOperationException("Missing path registry reported verified removal.");
            File.Move(registry + ".saved", registry);
            loseCatalog = true;
            saveUpdate = false;
            try { catalog.Reconcile(discoveredPath, false); throw new InvalidOperationException("Lost catalog confirmed a failed removal."); }
            catch (IOException ex) when (ex.Message == "SteamVR reported an update error.") { }
            File.Move(config + ".saved", config);
            loseCatalog = false;
            saveUpdate = true;
            if (!catalog.Reconcile(discoveredPath, false) || registrations.Contains(storedPath))
                throw new InvalidOperationException("Could not retry removal after rediscovery.");

            using (File.Open(config, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { catalog.Reconcile(discoveredPath, false); throw new InvalidOperationException("Unreadable catalog reported success."); }
                catch (IOException) { }
            }
            var art = File.ReadAllText(otherPath);
            if (!new[] { "AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome", "/15560/header.jpg", "/15560/library_600x900.jpg" }.All(art.Contains))
                throw new InvalidOperationException("Packaged artwork does not describe the Steam game.");
            Console.WriteLine("PASS: packaged cover, SteamVR catalog discovery and exact-path artwork removal.");
        }
    }
}
