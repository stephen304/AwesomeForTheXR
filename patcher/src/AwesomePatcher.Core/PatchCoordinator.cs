using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AwesomePatcher.Core
{
    // Runs in the player's process. Only the file operation crosses the UAC boundary.
    internal sealed class PatchCoordinator
    {
        private readonly PatcherService files;
        private readonly IArtworkCatalog catalog;
        private readonly Action<string, bool, bool> runWorker;
        private readonly Func<bool> canElevate;

        internal PatchCoordinator(PatcherService files, IArtworkCatalog catalog, Action<string, bool, bool> runWorker, Func<bool> canElevate)
        {
            this.files = files;
            this.catalog = catalog;
            this.runWorker = runWorker;
            this.canElevate = canElevate;
        }

        private static string Manifest(string folder) =>
            Path.Combine(Path.GetFullPath(folder), SteamVrArtwork.Name);

        internal const string UnverifiedCleanup = "SteamVR catalog unavailable. Artwork cleanup could not be verified.";

        internal PatchStatus Inspect(string folder)
        {
            var state = files.Inspect(folder);
            if (!state.IsCurrent && !state.FilesOriginal) return state;
            try
            {
                var registration = catalog.Inspect(Manifest(folder), includeOtherProviders: state.IsCurrent);
                bool ours = registration.HasFlag(ArtworkRegistration.OwnedPath);
                if (state.FilesOriginal)
                {
                    return ours ? new PatchStatus("Original game files verified. Revert to remove the stale SteamVR artwork registration.",
                        state.CanPatch, canRevert: true, filesOriginal: true) : state;
                }
                bool other = registration.HasFlag(ArtworkRegistration.OtherProvider);
                if (registration.HasFlag(ArtworkRegistration.Available) && ours == other)
                    return new PatchStatus("Game files match. Patch to update SteamVR artwork registration.", true, true);
                string message = other ? "SteamVR already has another entry for this game." :
                    ours ? "SteamVR artwork is registered." : "SteamVR was not found. Run Patch after installing SteamVR to register artwork.";
                return new PatchStatus(state.Message + " " + message, canRevert: true, isCurrent: true);
            }
            catch (Exception ex) when (IsArtworkError(ex))
            {
                if (state.FilesOriginal)
                    return new PatchStatus(state.Message + " Could not check SteamVR artwork. " + ex.Message,
                        state.CanPatch, canRevert: true, filesOriginal: true);
                return new PatchStatus("Game files match. Could not check SteamVR artwork. " + ex.Message, true, true);
            }
        }

        private static bool IsArtworkError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is System.Runtime.Serialization.SerializationException || ex is System.Security.SecurityException ||
            ex is ArgumentException || ex is NotSupportedException;

        internal void Patch(string folder) => Change(folder, false);
        internal bool Revert(string folder) => Change(folder, true);

        private bool Change(string folder, bool revert)
        {
            string path = Manifest(folder);
            string key = PatchFiles.Digest(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            // Serialize both phases without writing a lock file during an artwork-only repair.
            using (var operation = new Mutex(false, @"Local\AwesomePatcher-" + key))
            {
                try
                {
                    if (!operation.WaitOne(0)) throw new IOException("Another patcher is changing this installation.");
                }
                catch (AbandonedMutexException) { /* The previous coordinator exited. Reinspect the files. */ }
                try
                {
                    var state = files.Inspect(folder);
                    if (revert)
                    {
                        if (state.CanRevert) ChangeFiles(folder, true);
                        else if (!state.FilesOriginal) throw new InvalidOperationException(state.Message);
                    }
                    else if (!state.IsCurrent)
                    {
                        if (!state.CanPatch) throw new InvalidOperationException(state.Message);
                        ChangeFiles(folder, false);
                    }

                    try
                    {
                        return catalog.Reconcile(path, installed: !revert);
                    }
                    catch (Exception ex) when (IsArtworkError(ex))
                    {
                        string message = revert ? "Game restored. Could not confirm SteamVR artwork removal. Revert to retry cleanup. " :
                            "Game patched. SteamVR artwork registration failed. ";
                        throw new IOException(message + ex.Message, ex);
                    }
                }
                finally { operation.ReleaseMutex(); }
            }
        }

        private void ChangeFiles(string folder, bool revert)
        {
            try { runWorker(folder, revert, false); }
            catch (UnauthorizedAccessException denied) when (canElevate())
            {
                try { runWorker(folder, revert, true); }
                catch (UnauthorizedAccessException elevated)
                {
                    throw new UnauthorizedAccessException(denied.Message + " " + elevated.Message, elevated);
                }
            }
        }
    }
}
