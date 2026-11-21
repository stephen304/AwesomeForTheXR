using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AwesomeForTheXR.Patcher.Core;

namespace AwesomeForTheXR.Patcher.IntegrationTests
{
    internal static partial class Program
    {
        private static readonly byte[] Original = Encoding.ASCII.GetBytes("synthetic assembly v1");
        private static readonly byte[] FirstBuild = Encoding.ASCII.GetBytes("synthetic patched build 1");
        private static readonly byte[] SecondBuild = Encoding.ASCII.GetBytes("synthetic patched build 2");

        private static int Main(string[] args)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                "AwesomeForTheXR-smoke-" + Guid.NewGuid().ToString("N")));
            try
            {
                if (args.Length != 0 && args.Length != 2)
                    throw new ArgumentException("Optional arguments: <supported game folder> <built patch package folder>");
                if (args.Length == 2) TestLaunchers(folder.FullName, args[0], args[1]);
                TestArtworkDiscovery(folder.FullName);
                TestPatchCycle(folder.FullName);
                TestNativePlugin(folder.FullName);
                Console.WriteLine("PASS: managed/native install, update, interrupted transaction, verified revert and tamper detection.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                folder.Delete(true);
            }
        }

        private static void TestPatchCycle(string folder)
        {
            var managed = Directory.CreateDirectory(Path.Combine(folder, "Awesome_Data", "Managed"));
            string target = Path.Combine(managed.FullName, "Assembly-CSharp.dll");
            var first = Recipe(FirstBuild);
            var second = Recipe(SecondBuild);

            CheckStatus(first, folder, false, false, "missing target");
            File.WriteAllBytes(target, Original);
            CheckStatus(PatcherService.ForGame(), folder, false, false, "synthetic game must be unsupported");
            CheckStatus(first, folder, true, false, "clean fixture");

            first.Patch(folder);
            CheckBytes(target, FirstBuild, "first patch");
            CheckStatus(first, folder, false, true, "current recipe");
            CheckStatus(second, folder, true, true, "updated recipe");

            second.Patch(folder);
            CheckBytes(target, SecondBuild, "second patch");
            CheckStatus(second, folder, false, true, "current after reapply");

            var foreign = Encoding.ASCII.GetBytes("foreign edit to live assembly");
            File.WriteAllBytes(target, foreign);
            CheckStatus(second, folder, false, false, "modified live file");
            ExpectRefusal(() => second.Patch(folder), "patch modified live file");
            ExpectRefusal(() => second.Revert(folder), "revert modified live file");
            CheckBytes(target, foreign, "foreign file survived refused operations");
            File.WriteAllBytes(target, SecondBuild);

            var backup = Directory.GetFiles(Path.Combine(folder, "backups"), "*", SearchOption.AllDirectories)
                .Single(path => File.ReadAllBytes(path).SequenceEqual(Original));
            File.WriteAllBytes(backup, foreign);
            CheckStatus(second, folder, false, false, "invalid pristine backup");
            ExpectRefusal(() => second.Revert(folder), "revert with invalid pristine backup");
            CheckBytes(target, SecondBuild, "installed file survived invalid backup");
            File.WriteAllBytes(backup, Original);

            second.Revert(folder);
            CheckBytes(target, Original, "reverted assembly");
            CheckBytes(backup, Original, "retained pristine backup");
            CheckStatus(second, folder, true, false, "clean after revert");
        }

        private static void TestNativePlugin(string folder)
        {
            // Start in the retained baseline from TestPatchCycle. Production places
            // this package beside the application, outside the player's game files.
            string package = Directory.CreateDirectory(Path.Combine(folder, "fixture-package")).FullName;
            string plugins = Directory.CreateDirectory(Path.Combine(folder, "Awesome_Data", "Plugins")).FullName;
            const string name = "AwesomeForTheXR32.dll";
            string source = Path.Combine(package, name), installed = Path.Combine(plugins, name);
            string target = Path.Combine(folder, "Awesome_Data", "Managed", "Assembly-CSharp.dll");
            string manifest = Path.Combine(folder, "AwesomeForTheXR.manifest.json");
            var payload1 = Encoding.UTF8.GetBytes("synthetic native build 1");
            var payload2 = Encoding.UTF8.GetBytes("synthetic native build 2");
            var foreign = Encoding.UTF8.GetBytes("third-party bytes");
            var service = Recipe(FirstBuild, package);

            CheckStatus(service, folder, false, false, "package absent");
            WritePackage(source, payload1);
            File.WriteAllBytes(installed, foreign);
            CheckStatus(service, folder, false, false, "foreign plugin name collision");
            ExpectRefusal(() => service.Patch(folder), "replace pre-existing plugin");
            CheckBytes(installed, foreign, "unowned plugin survived refusal");
            File.Delete(installed);

            service.Patch(folder);
            CheckBytes(installed, payload1, "native install");
            CheckStatus(service, folder, false, true, "matched installed package");
            byte[] previousManifest = File.ReadAllBytes(manifest);
            WritePackage(source, payload2);
            CheckStatus(service, folder, true, true, "DLL-only update");
            var sameAssembly = new PatcherService(Hash(Original), Hash(FirstBuild),
                _ => throw new InvalidOperationException("DLL-only update invoked the assembly rewriter."), package);
            sameAssembly.Patch(folder);
            CheckBytes(target, FirstBuild, "existing managed patch");
            CheckBytes(installed, payload2, "updated native plugin");

            File.WriteAllBytes(installed, foreign);
            CheckStatus(service, folder, false, false, "installed plugin changed");
            ExpectRefusal(() => service.Revert(folder), "remove modified plugin");
            CheckBytes(installed, foreign, "modified plugin survived refusal");
            File.WriteAllBytes(installed, payload2);
            File.WriteAllText(source + ".sha256", Hash(foreign));
            CheckStatus(service, folder, false, true, "corrupted package permits revert only");
            ExpectRefusal(() => service.Patch(folder), "install bad package");
            File.Delete(source);
            CheckStatus(service, folder, false, true, "uninstall without release package");

            // Simulate interruption during a native-only update: the new DLL is
            // already visible, the old manifest is still live and the journal is durable.
            File.WriteAllBytes(manifest, previousManifest);
            string journal = Path.Combine(folder, "backups", "AwesomeForTheXR",
                Hash(Original).Substring(0, 16), "pending.json");
            string json = "{\"BeforeHash\":\"" + Hash(FirstBuild) + "\",\"AfterHash\":\"" + Hash(FirstBuild) +
                "\",\"BeforeNativeHash\":\"" + Hash(payload1) + "\",\"AfterNativeHash\":\"" + Hash(payload2) +
                "\",\"PreviousAssembly\":" + JsonBytes(FirstBuild) + ",\"PreviousNative\":" + JsonBytes(payload1) +
                ",\"PreviousManifest\":" + JsonBytes(previousManifest) + "}";
            File.WriteAllText(journal, json);
            CheckStatus(service, folder, false, true, "interrupted transaction");
            service.Revert(folder);
            CheckBytes(target, Original, "exact original after interrupted update");
            if (File.Exists(journal) || File.Exists(manifest) || File.Exists(installed))
                throw new InvalidOperationException("Revert left transaction files or the runtime plugin installed.");
        }

        private static string JsonBytes(byte[] bytes) => "[" + string.Join(",", bytes) + "]";

        private static void WritePackage(string source, byte[] payload)
        {
            File.WriteAllBytes(source, payload);
            File.WriteAllText(source + ".sha256", Hash(payload));
        }

        private static PatcherService Recipe(byte[] output, string package = null)
        {
            return new PatcherService(Hash(Original), Hash(output), bytes =>
            {
                if (!bytes.SequenceEqual(Original))
                    throw new InvalidDataException("Recipe did not receive the pristine bytes.");
                return (byte[])output.Clone();
            }, package);
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        private static void CheckStatus(PatcherService service, string folder, bool canPatch,
            bool canRevert, string scenario)
        {
            var status = service.Inspect(folder);
            if (status.CanPatch != canPatch || status.CanRevert != canRevert ||
                string.IsNullOrWhiteSpace(status.Message))
                throw new InvalidOperationException(scenario + ": unexpected status: " + status.Message +
                    " (patch=" + status.CanPatch + ", revert=" + status.CanRevert + ").");
        }

        private static void CheckBytes(string path, byte[] expected, string scenario)
        {
            if (!File.ReadAllBytes(path).SequenceEqual(expected))
                throw new InvalidDataException(scenario + ": incorrect file contents.");
        }

        private static void ExpectRefusal(Action action, string scenario)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            catch (InvalidDataException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException(scenario + ": operation unexpectedly succeeded.");
        }
    }
}
