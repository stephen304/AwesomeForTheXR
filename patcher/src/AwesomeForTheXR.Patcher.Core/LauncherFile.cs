using System;
using System.IO;
using System.Runtime.Serialization;

namespace AwesomeForTheXR.Patcher.Core
{
    [DataContract]
    internal sealed class ExecutableChange
    {
        [DataMember(IsRequired = true)] public string Before;
        [DataMember(IsRequired = true)] public string After;
    }

    // Fixed game-owned targets. Neither the package nor the game manifest can
    // supply paths. Journal snapshots are needed only for replacing an old patch.
    internal sealed class LauncherFile
    {
        private readonly InstallPaths paths;
        private readonly string name, originalHash, backupRelative, live;
        internal LauncherFile(InstallPaths paths, string name, string originalHash)
        {
            this.paths = paths;
            this.name = name;
            this.originalHash = originalHash;
            live = paths.Resolve(name);
            backupRelative = paths.OriginalRelative.Substring(0, paths.OriginalRelative.LastIndexOf('/') + 1) + name;
        }

        internal string Inspect(string installed, bool interrupted)
        {
            string hash = PatchFiles.OptionalDigest(live);
            if (hash == null || (!interrupted && hash != originalHash && hash != installed))
                throw new InvalidDataException("Unexpected or missing executable: " + name);
            var backup = PatchFiles.OptionalDigest(paths.Resolve(backupRelative));
            if ((backup != null || installed != null) && backup != originalHash)
                throw new InvalidDataException("Missing or changed original backup: " + name);
            return hash;
        }

        internal byte[] Original()
        {
            var backup = paths.Resolve(backupRelative);
            if (!File.Exists(backup))
            {
                byte[] data = File.ReadAllBytes(live);
                if (PatchFiles.Digest(data) != originalHash) throw new IOException("Verified original required: " + name);
                PatchFiles.Publish(backup, data, false);
            }
            return Verified(backup, originalHash);
        }

        internal ExecutableChange Prepare(string installed, byte[] candidate)
        {
            var change = new ExecutableChange { Before = Inspect(installed, false), After = PatchFiles.Digest(candidate) };
            if (change.Before != originalHash && change.Before != change.After)
            {
                var snapshot = Snapshot(change.Before);
                if (!File.Exists(snapshot)) PatchFiles.Publish(snapshot, Verified(live, change.Before), false);
                if (PatchFiles.Digest(snapshot) != change.Before) throw new IOException("Invalid rollback snapshot: " + name);
            }
            return change;
        }

        internal void CheckJournal(ExecutableChange change, string oldInstalled, string currentInstalled, bool newManifest)
        {
            if (change == null)
            {
                if (oldInstalled != null || currentInstalled != null || PatchFiles.OptionalDigest(live) != originalHash)
                    throw new InvalidDataException("Missing executable transaction: " + name);
                return; // Pre-launcher journal.
            }
            string hash = PatchFiles.OptionalDigest(live);
            if (!PatchFiles.IsHash(change.Before) || !PatchFiles.IsHash(change.After) ||
                (change.Before != originalHash && change.Before != oldInstalled) ||
                (hash != change.Before && hash != change.After) ||
                (newManifest && change.After != (currentInstalled ?? originalHash)) ||
                PatchFiles.OptionalDigest(paths.Resolve(backupRelative)) != originalHash)
                throw new InvalidDataException("Interrupted update conflicts with " + name);
            if (hash != change.Before && change.Before != originalHash &&
                PatchFiles.OptionalDigest(Snapshot(change.Before)) != change.Before)
                throw new InvalidDataException("Cannot verify rollback snapshot: " + name);
        }

        internal void Apply(ExecutableChange change, byte[] candidate) =>
            PatchFiles.Transition(live, change.Before, change.After, candidate);

        internal void RollBack(ExecutableChange change)
        {
            if (change == null || PatchFiles.Digest(live) == change.Before) return;
            string source = change.Before == originalHash ? paths.Resolve(backupRelative) : Snapshot(change.Before);
            PatchFiles.Transition(live, change.After, change.Before, Verified(source, change.Before));
        }

        internal void DiscardSnapshot(ExecutableChange change)
        {
            if (change == null || change.Before == originalHash || change.Before == change.After) return;
            var snapshot = Snapshot(change.Before);
            // Cleanup is optional once the journal has been retired.
            try { if (PatchFiles.OptionalDigest(snapshot) == change.Before) File.Delete(snapshot); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Keep temporary names no longer than the stock player filenames for long Steam library paths.
        // The snapshot contents are always checked against the full transaction hash.
        private string Snapshot(string hash) =>
            paths.Resolve(backupRelative.Substring(0, backupRelative.Length - name.Length) +
                hash.Substring(0, 12) + ".previous");

        private static byte[] Verified(string path, string hash)
        {
            var bytes = File.ReadAllBytes(path);
            if (PatchFiles.Digest(bytes) != hash) throw new IOException("File changed during verification: " + Path.GetFileName(path));
            return bytes;
        }
    }
}
