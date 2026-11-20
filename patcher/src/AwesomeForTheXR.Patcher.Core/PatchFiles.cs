using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace AwesomeForTheXR.Patcher.Core
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

        internal static string Digest(string path)
        {
            using (var data = File.OpenRead(path)) return Digest(data);
        }

        internal static string OptionalDigest(string path)
        {
            if (Directory.Exists(path)) throw new IOException("Expected a file at " + path);
            return File.Exists(path) ? Digest(path) : null;
        }

        // Each replace is local to one volume. The transaction journal makes
        // the pair (assembly + native plugin) recoverable across replacements.
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
    }

    // This snapshot is deliberately separate from the immutable original.
    [DataContract]
    internal sealed class PendingWrite
    {
        [DataMember(IsRequired = true)] public string BeforeHash;
        [DataMember(IsRequired = true)] public string AfterHash;
        [DataMember(IsRequired = true)] public byte[] PreviousAssembly;
        [DataMember] public byte[] PreviousManifest;
        [DataMember(EmitDefaultValue = false)] public string BeforeNativeHash;
        [DataMember(EmitDefaultValue = false)] public string AfterNativeHash;
        [DataMember(EmitDefaultValue = false)] public byte[] PreviousNative;
    }

    internal sealed class InstallPaths
    {
        internal const string Assembly = "Awesome_Data/Managed/Assembly-CSharp.dll";
        internal const string Manifest = "AwesomeForTheXR.manifest.json";
        internal const string NativeName = "AwesomeForTheXR32.dll";
        internal const string NativeFile = "Awesome_Data/Plugins/" + NativeName;
        internal readonly string Root, Live, Native, Record, Original, OriginalRelative, DirectoryPath, Journal, Lock;

        internal InstallPaths(string folder, string baseline)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException("Select the Awesome game folder.");
            Root = Path.GetFullPath(folder);
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
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked file or directory requires review: " + relative);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return path;
        }
    }
}
