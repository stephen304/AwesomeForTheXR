using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace AwesomePatcher.Core
{
    internal interface IArtworkCatalog
    {
        ArtworkRegistration Inspect(string manifest);
        void SetRegistered(string manifest, bool registered);
    }

    [Flags]
    internal enum ArtworkRegistration { None = 0, Available = 1, OwnedPath = 2, OtherProvider = 4 }

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
        internal SteamVrCatalog() : this(Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath")) { }

        internal SteamVrCatalog(string pathRegistry) { this.pathRegistry = pathRegistry; }

        private static T Read<T>(string path) where T : class
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, true))
            {
                // Valve and third-party manifests can include a UTF-8 BOM.
                return PatchFiles.Deserialize<T>(System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd()));
            }
        }

        private Paths GetPaths() => File.Exists(pathRegistry) ? Read<Paths>(pathRegistry) : null;

        private static string Library(Paths paths)
        {
            var root = Environment.GetEnvironmentVariable("VR_OVERRIDE") ?? paths?.runtime?.FirstOrDefault();
            return string.IsNullOrEmpty(root) || !Path.IsPathRooted(root) ? null :
                Path.Combine(root, "bin", IntPtr.Size == 8 ? "win64" : "win32", "openvr_api.dll");
        }

        public ArtworkRegistration Inspect(string manifest)
        {
            var paths = GetPaths();
            var config = Environment.GetEnvironmentVariable("VR_CONFIG_PATH") ?? paths?.config?.FirstOrDefault();
            if (string.IsNullOrEmpty(config) || !Path.IsPathRooted(config)) return ArtworkRegistration.None;
            var result = File.Exists(Library(paths)) ? ArtworkRegistration.Available : ArtworkRegistration.None;
            var catalog = Path.Combine(config, "appconfig.json");
            if (!File.Exists(catalog)) return result;
            foreach (var path in Read<Catalog>(catalog)?.manifest_paths ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) continue;
                if (string.Equals(Path.GetFullPath(path), manifest, StringComparison.OrdinalIgnoreCase))
                    result |= ArtworkRegistration.OwnedPath;
                else if (File.Exists(path))
                {
                    // An unrelated malformed manifest cannot provide a usable application entry.
                    try
                    {
                        if (Read<Manifest>(path)?.applications?.Any(app => app?.app_key == "steam.app.15560") == true)
                            result |= ArtworkRegistration.OtherProvider;
                    }
                    catch (SerializationException) { }
                }
            }
            return result;
        }

        public void SetRegistered(string manifest, bool registered)
        {
            var state = Inspect(manifest);
            if (state.HasFlag(ArtworkRegistration.OwnedPath) == registered) return;
            if (registered && state.HasFlag(ArtworkRegistration.OtherProvider)) return;
            var library = Library(GetPaths());
            if (!File.Exists(library))
                throw new IOException("SteamVR is unavailable to change its artwork registration.");
            OpenVrManifest.SetRegistered(library, manifest, registered);
            if (Inspect(manifest).HasFlag(ArtworkRegistration.OwnedPath) != registered)
                throw new IOException("SteamVR did not save the artwork registration.");
        }
    }
}
