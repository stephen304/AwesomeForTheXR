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
            public bool Available = true, OtherProvider, Registered, FailAfterAdd;
            public ArtworkRegistration Inspect(string path) =>
                (Available ? ArtworkRegistration.Available : 0) |
                (OtherProvider ? ArtworkRegistration.OtherProvider : 0) |
                (Registered ? ArtworkRegistration.OwnedPath : 0);

            public void SetRegistered(string path, bool registered)
            {
                if (registered == Registered) return;
                if (!Available || !File.Exists(path) || (registered && OtherProvider))
                    throw new InvalidOperationException("Unexpected artwork operation.");
                Registered = registered;
                if (registered && FailAfterAdd)
                {
                    FailAfterAdd = false;
                    throw new IOException("Simulated interrupted registration.");
                }
            }
        }

        private static void TestArtworkDiscovery(string temp)
        {
            var root = Directory.CreateDirectory(Path.Combine(temp, "SteamVR catalog")).FullName;
            var registry = Path.Combine(root, "openvrpaths.vrpath");
            var manifest = Path.Combine(root, SteamVrArtwork.Name);
            var storedPath = manifest.ToUpperInvariant().Replace('\\', '/');
            var otherPath = Path.Combine(root, "other.vrmanifest");
            var registrations = new List<string> { otherPath, storedPath, manifest };
            string config = Path.Combine(root, "appconfig.json");
            void SaveCatalog() => File.WriteAllText(config, "{\"manifest_paths\":" +
                System.Text.Encoding.UTF8.GetString(PatchFiles.Serialize(registrations.ToArray())) + "}");
            var catalog = new SteamVrCatalog(registry, (library, path, register) =>
            {
                if (register || !registrations.Remove(path)) throw new IOException("SteamVR artwork removal failed (107).");
                SaveCatalog();
            });
            if (catalog.Inspect(manifest) != ArtworkRegistration.None)
                throw new InvalidOperationException("Unexpected SteamVR installation.");
            string escaped = root.Replace("\\", "\\\\");
            File.WriteAllText(registry, "{\"config\":[\"" + escaped + "\"],\"runtime\":[\"" + escaped + "\"]}");
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin", IntPtr.Size == 8 ? "win64" : "win32"));
            File.WriteAllBytes(Path.Combine(bin.FullName, "openvr_api.dll"), Array.Empty<byte>());
            using (var resource = typeof(PatcherService).Assembly.GetManifestResourceStream(SteamVrArtwork.Name))
            using (var output = File.Create(otherPath)) resource.CopyTo(output);
            SaveCatalog();
            var expected = ArtworkRegistration.Available | ArtworkRegistration.OwnedPath | ArtworkRegistration.OtherProvider;
            if (catalog.Inspect(manifest) != expected) throw new InvalidOperationException("Manifest ownership detection failed.");
            string discoveredPath = manifest.ToLowerInvariant();
            catalog.SetRegistered(discoveredPath, false);
            catalog.SetRegistered(discoveredPath, false);
            if (!registrations.SequenceEqual(new[] { otherPath }) ||
                catalog.Inspect(manifest) != (ArtworkRegistration.Available | ArtworkRegistration.OtherProvider))
                throw new InvalidOperationException("Artwork removal failed to preserve the other provider.");
            var art = File.ReadAllText(otherPath);
            if (!new[] { "AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome", "/15560/header.jpg", "/15560/library_600x900.jpg" }.All(art.Contains))
                throw new InvalidOperationException("Packaged artwork does not describe the Steam game.");
            Console.WriteLine("PASS: packaged cover, SteamVR catalog discovery and exact-path artwork removal.");
        }
    }
}
