using System;
using System.IO;
using System.Runtime.Serialization;

namespace AwesomePatcher.Core
{
    [DataContract]
    internal sealed class SteamVrArtwork
    {
        internal const string Name = "AwesomeForTheXR.vrmanifest";
        private static readonly byte[] Contents = Load();
        private static readonly string Hash = PatchFiles.Digest(Contents);

        [DataMember] public byte[] Previous;
        [DataMember] public string After;
        private string Before => Previous == null ? null : PatchFiles.Digest(Previous);

        private static byte[] Load()
        {
            using (var resource = typeof(SteamVrArtwork).Assembly.GetManifestResourceStream(Name))
            using (var stream = new MemoryStream())
            {
                resource.CopyTo(stream);
                return stream.ToArray();
            }
        }

        internal static string VerifyFile(InstallPaths paths, InstallRecord record)
        {
            var digest = PatchFiles.OptionalDigest(paths.Resolve(Name));
            if (digest != null && digest != record?.ArtworkSha256)
                throw new InvalidDataException("Unrecognized or modified SteamVR artwork manifest: " + Name);
            return digest;
        }

        internal static bool IsCurrent(InstallPaths paths, InstallRecord record) =>
            record.ArtworkSha256 == Hash && PatchFiles.OptionalDigest(paths.Resolve(Name)) == Hash;

        internal static SteamVrArtwork Prepare(InstallPaths paths, InstallRecord record, bool revert)
        {
            var path = paths.Resolve(Name);
            var digest = VerifyFile(paths, record);
            if (revert && record?.ArtworkSha256 == null) return null;
            var change = new SteamVrArtwork {
                Previous = digest == null ? null : File.ReadAllBytes(path),
                After = revert ? null : Hash
            };
            if (change.Before != digest) throw new IOException("The SteamVR artwork file changed during verification.");
            return change;
        }

        internal void Verify(InstallPaths paths, InstallRecord original)
        {
            var live = PatchFiles.OptionalDigest(paths.Resolve(Name));
            if ((Before != null && Before != original?.ArtworkSha256) || (After != null && !PatchFiles.IsHash(After)) ||
                (live != Before && live != After))
                throw new InvalidDataException("Interrupted update conflicts with the SteamVR artwork manifest.");
        }

        internal void Apply(InstallPaths paths)
        {
            PatchFiles.Transition(paths.Resolve(Name), Before, After, After == null ? null : Contents);
        }

        internal void Undo(InstallPaths paths)
        {
            var path = paths.Resolve(Name);
            var live = PatchFiles.OptionalDigest(path);
            if (live != Before && live != After) throw new IOException("Artwork file changed during rollback.");
            PatchFiles.Transition(path, live, Before, Previous);
        }
    }
}
