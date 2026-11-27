using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace AwesomePatcher.Core
{
    internal interface IArtworkCatalog
    {
        ArtworkRegistration Inspect(string manifest, bool includeOtherProviders = true);
        bool Reconcile(string manifest, bool installed);
    }

    [Flags]
    internal enum ArtworkRegistration { None = 0, Available = 1, OwnedPath = 2, OtherProvider = 4, CatalogRead = 8 }

    internal sealed class SteamVrCatalog : IArtworkCatalog
    {
        [DataContract] private sealed class Paths
        {
            [DataMember] public string[] runtime { get; set; }
            [DataMember] public string[] config { get; set; }
        }

        [DataContract] private sealed class Catalog
        {
            [DataMember] public string[] manifest_paths { get; set; }
        }

        [DataContract] private sealed class Manifest
        {
            [DataMember] public Entry[] applications { get; set; }
        }

        [DataContract] private sealed class Entry
        {
            [DataMember] public string app_key { get; set; }
        }

        private readonly string pathRegistry;
        private readonly Action<string, string, bool> updateManifest;

        internal SteamVrCatalog() : this(Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath")) { }

        internal SteamVrCatalog(string pathRegistry, Action<string, string, bool> updateManifest = null)
        {
            this.pathRegistry = pathRegistry;
            this.updateManifest = updateManifest ?? OpenVrManifest.SetRegistered;
        }

        private static T Read<T>(string path) where T : class
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, true))
            {
                // Valve and third-party manifests can include a UTF-8 BOM.
                return PatchFiles.Deserialize<T>(System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd()));
            }
        }

        private static T ReadOptional<T>(string path) where T : class
        {
            try { return Read<T>(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private Paths GetPaths() => ReadOptional<Paths>(pathRegistry);

        private static string Library(Paths paths)
        {
            var root = Environment.GetEnvironmentVariable("VR_OVERRIDE") ?? paths?.runtime?.FirstOrDefault();
            return string.IsNullOrEmpty(root) || !Path.IsPathRooted(root) ? null :
                Path.Combine(root, "bin", IntPtr.Size == 8 ? "win64" : "win32", "openvr_api.dll");
        }

        public ArtworkRegistration Inspect(string manifest, bool includeOtherProviders = true) => Inspect(manifest, includeOtherProviders, null);

        private ArtworkRegistration Inspect(string manifest, bool includeOtherProviders, HashSet<string> registeredPaths)
        {
            var paths = GetPaths();
            var config = Environment.GetEnvironmentVariable("VR_CONFIG_PATH") ?? paths?.config?.FirstOrDefault();
            if (string.IsNullOrEmpty(config) || !Path.IsPathRooted(config)) return ArtworkRegistration.None;
            var result = File.Exists(Library(paths)) ? ArtworkRegistration.Available : ArtworkRegistration.None;
            var catalog = Path.Combine(config, "appconfig.json");
            var entries = ReadOptional<Catalog>(catalog);
            if (entries == null) return result;
            result |= ArtworkRegistration.CatalogRead;
            foreach (var path in entries.manifest_paths ?? Array.Empty<string>())
            {
                string fullPath;
                try
                {
                    if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) continue;
                    fullPath = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    continue; // Only this catalog entry is unusable.
                }
                if (string.Equals(fullPath, manifest, StringComparison.OrdinalIgnoreCase))
                {
                    result |= ArtworkRegistration.OwnedPath;
                    registeredPaths?.Add(path);
                }
                else if (includeOtherProviders)
                {
                    // An unrelated malformed manifest cannot provide a usable application entry.
                    try
                    {
                        if (ReadOptional<Manifest>(fullPath)?.applications?.Any(app => app?.app_key == "steam.app.15560") == true)
                            result |= ArtworkRegistration.OtherProvider;
                    }
                    catch (SerializationException) { }
                }
            }
            return result;
        }

        private static bool Confirmed(ArtworkRegistration state, bool registered) =>
            state.HasFlag(ArtworkRegistration.CatalogRead) && state.HasFlag(ArtworkRegistration.OwnedPath) == registered;

        // False means that SteamVR's catalog could not be discovered to confirm cleanup.
        public bool Reconcile(string manifest, bool installed)
        {
            var registeredPaths = new HashSet<string>(StringComparer.Ordinal);
            var state = Inspect(manifest, installed, registeredPaths);
            bool register = installed && !state.HasFlag(ArtworkRegistration.OtherProvider);
            if (Confirmed(state, register)) return true;
            if (installed && !state.HasFlag(ArtworkRegistration.Available)) return false;
            if (!register && !state.HasFlag(ArtworkRegistration.OwnedPath)) return false;

            var library = Library(GetPaths());
            if (!File.Exists(library))
                throw new IOException("SteamVR is unavailable to change its artwork registration.");
            try
            {
                if (register) updateManifest(library, manifest, true);
                else
                    // Removal uses the exact spellings in the catalog even after the file is gone.
                    foreach (var path in registeredPaths) updateManifest(library, path, false);
            }
            catch (IOException) when (Confirmed(Inspect(manifest, false), register)) { return true; }

            if (!Confirmed(Inspect(manifest, false), register))
                throw new IOException("Could not confirm the artwork change in the SteamVR catalog.");
            return true;
        }
    }
}
