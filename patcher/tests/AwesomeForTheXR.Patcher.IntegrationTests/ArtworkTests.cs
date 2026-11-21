using System;
using System.IO;
using System.Linq;
using AwesomeForTheXR.Patcher.Core;

namespace AwesomeForTheXR.Patcher.IntegrationTests
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
            var catalog = new SteamVrCatalog(registry);
            if (catalog.Inspect(manifest) != ArtworkRegistration.None)
                throw new InvalidOperationException("Unexpected SteamVR installation.");
            string escaped = root.Replace("\\", "\\\\");
            File.WriteAllText(registry, "{\"config\":[\"" + escaped + "\"],\"runtime\":[\"" + escaped + "\"]}");
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin", IntPtr.Size == 8 ? "win64" : "win32"));
            File.WriteAllBytes(Path.Combine(bin.FullName, "openvr_api.dll"), Array.Empty<byte>());
            using (var resource = typeof(PatcherService).Assembly.GetManifestResourceStream(SteamVrArtwork.Name))
            using (var output = File.Create(Path.Combine(root, "other.vrmanifest"))) resource.CopyTo(output);
            File.WriteAllText(Path.Combine(root, "appconfig.json"), "{\"manifest_paths\":[\"" +
                escaped + "/other.vrmanifest\",\"" + escaped + "/" + SteamVrArtwork.Name + "\"]}");
            var expected = ArtworkRegistration.Available | ArtworkRegistration.OwnedPath | ArtworkRegistration.OtherProvider;
            if (catalog.Inspect(manifest) != expected) throw new InvalidOperationException("Manifest ownership detection failed.");
            var art = File.ReadAllText(Path.Combine(root, "other.vrmanifest"));
            if (!new[] { "AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome", "/15560/header.jpg", "/15560/library_600x900.jpg" }.All(art.Contains))
                throw new InvalidOperationException("Packaged artwork does not describe the Steam game.");
            Console.WriteLine("PASS: packaged cover and SteamVR catalog discovery.");
        }
    }
}
