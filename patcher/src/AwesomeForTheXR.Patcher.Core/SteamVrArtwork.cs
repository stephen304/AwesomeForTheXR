using System;
using System.IO;
using System.Runtime.Serialization;

namespace AwesomeForTheXR.Patcher.Core
{
    [DataContract]
    internal sealed class SteamVrArtwork
    {
        internal const string Name = "AwesomeForTheXR.vrmanifest";
        private static readonly byte[] Contents = Load();
        private static readonly string Hash = PatchFiles.Digest(Contents);

        [DataMember] public byte[] Previous;
        [DataMember] public string After;
        [DataMember] public bool PreviouslyRegistered;
        [DataMember] public bool Register;
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

        internal static PatchStatus InstalledStatus(InstallPaths paths, InstallRecord record, IArtworkCatalog catalog)
        {
            var path = paths.Resolve(Name);
            if (record.ArtworkSha256 != Hash || PatchFiles.OptionalDigest(path) != Hash)
                return new PatchStatus("Game files match. Patch to install the SteamVR artwork manifest.", true, true);
            var state = catalog.Inspect(path);
            bool ours = state.HasFlag(ArtworkRegistration.OwnedPath);
            bool other = state.HasFlag(ArtworkRegistration.OtherProvider);
            if (state.HasFlag(ArtworkRegistration.Available) && ((ours && other) || (!ours && !other)))
                return new PatchStatus("Game files match. Patch to update SteamVR artwork registration.", true, true);
            string message = other ? "SteamVR already has another entry for this game." :
                ours ? "SteamVR artwork is registered." : "SteamVR was not found. Run Patch after installing SteamVR to register artwork.";
            return new PatchStatus("Files already match this patch. " + message, canRevert: true, isCurrent: true);
        }

        internal static SteamVrArtwork Prepare(InstallPaths paths, InstallRecord record, bool revert, IArtworkCatalog catalog)
        {
            var path = paths.Resolve(Name);
            var digest = VerifyFile(paths, record);
            if (revert && record?.ArtworkSha256 == null) return null;
            var state = catalog.Inspect(path);
            bool owned = state.HasFlag(ArtworkRegistration.OwnedPath);
            var previous = digest == null ? null : File.ReadAllBytes(path);
            var change = new SteamVrArtwork {
                Previous = previous, After = revert ? null : Hash, PreviouslyRegistered = owned,
                Register = !revert && (state.HasFlag(ArtworkRegistration.Available)
                    ? !state.HasFlag(ArtworkRegistration.OtherProvider) : owned)
            };
            if (change.Before != digest) throw new IOException("The SteamVR artwork file changed during verification.");
            return change;
        }

        internal void Verify(InstallPaths paths, InstallRecord original)
        {
            var live = PatchFiles.OptionalDigest(paths.Resolve(Name));
            if ((Before != null && Before != original?.ArtworkSha256) || (After != null && !PatchFiles.IsHash(After)) ||
                (Register && After == null) || (live != Before && live != After))
                throw new InvalidDataException("Interrupted update conflicts with the SteamVR artwork manifest.");
        }

        internal void Apply(InstallPaths paths, IArtworkCatalog catalog)
        {
            var path = paths.Resolve(Name);
            if (!Register && PreviouslyRegistered) catalog.SetRegistered(path, false);
            PatchFiles.Transition(path, Before, After, After == null ? null : Contents);
            if (Register && !PreviouslyRegistered) catalog.SetRegistered(path, true);
        }

        internal void Undo(InstallPaths paths, IArtworkCatalog catalog)
        {
            var path = paths.Resolve(Name);
            if (Register != PreviouslyRegistered && !PreviouslyRegistered) catalog.SetRegistered(path, false);
            var live = PatchFiles.OptionalDigest(path);
            if (live != Before && live != After) throw new IOException("Artwork file changed during rollback.");
            PatchFiles.Transition(path, live, Before, Previous);
            if (Register != PreviouslyRegistered && PreviouslyRegistered) catalog.SetRegistered(path, true);
        }
    }
}
