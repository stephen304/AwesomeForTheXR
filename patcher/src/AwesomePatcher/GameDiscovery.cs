using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AwesomePatcher
{
    internal static class GameDiscovery
    {
        // Steam writes one quoted key/value pair per line in its Windows VDF/ACF files.
        private static readonly Regex Pair = new Regex(
            "^\\s*\"(?<key>[^\"\\\\]+)\"\\s+\"(?<value>(?:\\\\.|[^\"\\\\])*)\"\\s*(?://.*)?$",
            RegexOptions.CultureInvariant);

        internal static string FindGameFolder()
        {
            var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRoot(steamRoots, RegistryPath(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"));
            AddRoot(steamRoots, RegistryPath(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"));
            AddRoot(steamRoots, RegistryPath(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            foreach (var special in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                var directory = Environment.GetFolderPath(special);
                if (!string.IsNullOrEmpty(directory)) AddRoot(steamRoots, Path.Combine(directory, "Steam"));
            }

            var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
            foreach (var steam in steamRoots)
            {
                foreach (var pair in ReadPairs(Path.Combine(steam, "steamapps", "libraryfolders.vdf")))
                {
                    // Older VDFs stored a path directly under the numeric library key.
                    if (pair.Key == "path" || int.TryParse(pair.Key, out _)) AddRoot(libraries, pair.Value);
                }
            }

            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                var apps = Path.Combine(library, "steamapps");
                var folder = ReadPairs(Path.Combine(apps, "appmanifest_15560.acf"))
                    .FirstOrDefault(p => p.Key == "installdir").Value ?? "Awesome";
                if (folder.Length == 0 || folder == "." || folder == ".." ||
                    folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                var candidate = Path.Combine(apps, "common", folder);
                if (File.Exists(Path.Combine(candidate, "Awesome.exe")))
                    matches.Add(candidate);
            }
            return matches.Count == 1 ? matches.Single() : null;
        }

        private static void AddRoot(HashSet<string> roots, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || !Directory.Exists(value)) return;
            roots.Add(Path.GetFullPath(value));
        }

        private static string RegistryPath(string key, string name)
        {
            try { return Registry.GetValue(key, name, null) as string; }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadPairs(string file)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { return Enumerable.Empty<KeyValuePair<string, string>>(); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<KeyValuePair<string, string>>(); }

            return lines.Select(line => Pair.Match(line)).Where(m => m.Success)
                .Select(m => new KeyValuePair<string, string>(m.Groups["key"].Value,
                    m.Groups["value"].Value.Replace(@"\\", @"\").Replace("\\\"", "\"")));
        }
    }
}
