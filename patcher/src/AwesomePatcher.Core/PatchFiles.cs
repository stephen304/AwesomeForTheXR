using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace AwesomePatcher.Core
{
    internal static class PatchFiles
    {
        internal static string Digest(Stream stream)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
        }

        internal static string Digest(byte[] bytes)
        {
            using (var data = new MemoryStream(bytes, false)) return Digest(data);
        }

        internal static string Digest(string path, bool optional = false)
        {
            using (var data = ReadFile(path, File.OpenRead, optional))
                return data == null ? null : Digest(data);
        }

        internal static string OptionalDigest(string path) => Digest(path, optional: true);

        internal static T ReadFile<T>(string path, Func<string, T> read, bool optional = false) where T : class
        {
            try { return read(path); }
            catch (IOException ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                if (optional) return null;
                throw new FileNotFoundException("Missing file: " + path, path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Windows also reports access denied when asked to open a directory as a file.
                if (Directory.Exists(path)) throw new IOException("Expected a file at " + path, ex);
                throw ReadDenied(path, ex);
            }
        }

        internal static FileAttributes? AttributesIfPresent(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (UnauthorizedAccessException ex) { throw ReadDenied(path, ex); }
        }

        private static UnauthorizedAccessException ReadDenied(string path, UnauthorizedAccessException reason) =>
            new UnauthorizedAccessException("Read access denied. Allow your account to read: " + path, reason);

        // Each replace is local to one volume. The transaction journal makes
        // each owned game file recoverable across replacements.
        internal static void Transition(string path, string before, string after, byte[] contents)
        {
            if (OptionalDigest(path) != before)
                throw new IOException("File changed during the transaction: " + Path.GetFileName(path));
            if (before == after) return;
            if (after == null) File.Delete(path);
            else Publish(path, contents, before != null);
            if (OptionalDigest(path) != after)
                throw new IOException("Failed to verify " + Path.GetFileName(path));
        }

        internal static bool IsHash(string hash)
        {
            return hash != null && hash.Length == 64 && Array.TrueForAll(hash.ToCharArray(), Uri.IsHexDigit);
        }

        internal static byte[] Serialize<T>(T value)
        {
            using (var data = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(data, value);
                return data.ToArray();
            }
        }

        internal static T Deserialize<T>(byte[] bytes)
        {
            using (var data = new MemoryStream(bytes, false))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(data);
        }

        // Keep the temporary file on the destination volume. Flush the new bytes
        // before the rename so the previous file stays usable if writing fails.
        internal static void Publish(string path, byte[] bytes, bool replace)
        {
            var temp = Path.Combine(Path.GetDirectoryName(path), Path.GetRandomFileName());
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                try { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                catch { file.Close(); File.Delete(temp); throw; }
            }
            try
            {
                if (replace) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (UnauthorizedAccessException ex)
            {
                // File.Replace can omit the failing path from its exception.
                throw new UnauthorizedAccessException("Access denied publishing " + path + ".", ex);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    [DataContract]
    internal sealed class InstallRecord
    {
        [DataMember(IsRequired = true)] public string Format;
        [DataMember(IsRequired = true)] public string OriginalSha256;
        [DataMember(IsRequired = true)] public string InstalledSha256;
        [DataMember(IsRequired = true)] public string Backup;
        [DataMember(EmitDefaultValue = false)] public string NativeSha256;
        [DataMember(EmitDefaultValue = false)] public string PlayerSha256;
        [DataMember(EmitDefaultValue = false)] public string ForwarderSha256;
        [DataMember(EmitDefaultValue = false)] public string StartupSha256;
        [DataMember(EmitDefaultValue = false)] public string LoaderSha256;
        [DataMember(EmitDefaultValue = false)] public string ArtworkSha256;
    }

    // This snapshot is deliberately separate from the immutable original.
    [DataContract]
    internal sealed class PendingWrite
    {
        [DataMember(IsRequired = true)] public string BeforeHash;
        [DataMember(IsRequired = true)] public string AfterHash;
        [DataMember(IsRequired = true)] public byte[] PreviousAssembly;
        [DataMember] public byte[] PreviousManifest;
        [DataMember(EmitDefaultValue = false)] public ExecutableChange Player;
        [DataMember(EmitDefaultValue = false)] public ExecutableChange Forwarder;
        [DataMember(EmitDefaultValue = false)] public string BeforeNativeHash;
        [DataMember(EmitDefaultValue = false)] public string AfterNativeHash;
        [DataMember(EmitDefaultValue = false)] public byte[] PreviousNative;
        [DataMember(EmitDefaultValue = false)] public string BeforeLoaderHash;
        [DataMember(EmitDefaultValue = false)] public string AfterLoaderHash;
        [DataMember(EmitDefaultValue = false)] public byte[] PreviousLoader;
        [DataMember(EmitDefaultValue = false)] public SteamVrArtwork Artwork;
    }

    internal sealed class InstallPaths
    {
        internal const string Assembly = "Awesome_Data/Managed/Assembly-CSharp.dll";
        internal const string Manifest = "AwesomeForTheXR.manifest.json";
        internal const string NativeName = "AwesomeForTheXR32.dll";
        internal const string LoaderName = "openxr_loader.dll";
        internal const string NativeFile = "Awesome_Data/Plugins/" + NativeName;
        internal readonly string Root, Live, Native, Record, Original, OriginalRelative, DirectoryPath, Journal, Lock;

        internal InstallPaths(string folder, string baseline)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new DirectoryNotFoundException("Select the Awesome game folder.");
            Root = Path.GetFullPath(folder);
            var attributes = PatchFiles.AttributesIfPresent(Root);
            if (!attributes.HasValue) throw new DirectoryNotFoundException("Missing game folder: " + Root);
            if ((attributes.Value & FileAttributes.Directory) == 0) throw new IOException("Expected a game folder at " + Root);
            Live = Resolve(Assembly);
            Native = Resolve(NativeFile);
            Record = Resolve(Manifest);
            // Keep enough path budget for libraries under long Wine/Windows paths.
            // Identity and integrity checks still use the entire SHA-256 digest.
            var backupDirectory = "backups/AwesomeForTheXR/" + baseline.Substring(0, 16);
            OriginalRelative = backupDirectory + "/Assembly-CSharp.dll";
            Original = Resolve(OriginalRelative);
            DirectoryPath = Path.GetDirectoryName(Original);
            Journal = Resolve(backupDirectory + "/pending.json");
            Lock = Resolve(backupDirectory + "/transaction.lock");
        }

        // All relative paths here are fixed by the patcher. Never follow game-side
        // junctions or links when selecting an owned file for replacement.
        internal string Resolve(string relative)
        {
            string path = Root;
            foreach (var part in relative.Split('/'))
            {
                path = Path.Combine(path, part);
                if ((PatchFiles.AttributesIfPresent(path).GetValueOrDefault() & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked file or directory requires review: " + relative);
            }
            return path;
        }
    }
}
