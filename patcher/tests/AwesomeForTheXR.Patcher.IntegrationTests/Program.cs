using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AwesomeForTheXR.Patcher.Core;

namespace AwesomeForTheXR.Patcher.IntegrationTests
{
    internal static class Program
    {
        private static readonly byte[] Original = Encoding.ASCII.GetBytes("synthetic assembly v1");
        private static readonly byte[] FirstBuild = Encoding.ASCII.GetBytes("synthetic patched build 1");
        private static readonly byte[] SecondBuild = Encoding.ASCII.GetBytes("synthetic patched build 2");

        private static int Main()
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                "AwesomeForTheXR-smoke-" + Guid.NewGuid().ToString("N")));
            try
            {
                TestPatchCycle(folder.FullName);
                Console.WriteLine("PASS: patch, reapply from pristine backup, verification and revert.");
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

        private static PatcherService Recipe(byte[] output)
        {
            return new PatcherService(Hash(Original), Hash(output), bytes =>
            {
                if (!bytes.SequenceEqual(Original))
                    throw new InvalidDataException("Recipe did not receive the pristine bytes.");
                return (byte[])output.Clone();
            });
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
