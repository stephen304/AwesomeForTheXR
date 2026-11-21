using System;
using System.IO;
using System.Linq;
using AwesomeForTheXR.Patcher.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomeForTheXR.Patcher.IntegrationTests
{
    internal static partial class Program
    {
        // Optional private fixture: read the four required originals from a
        // supported install or its patch backups. All writes go to the test temp dir.
        private static void TestLaunchers(string temp, string source, string package)
        {
            string folder = Directory.CreateDirectory(Path.Combine(temp, "private player")).FullName;
            const string managed = "Awesome_Data/Managed/Assembly-CSharp.dll";
            const string player = "Awesome.exe", helper = "Awesome_DirectToRift.exe";
            const string plugin = "Awesome_Data/Plugins/AwesomeForTheXR32.dll";
            const string backup = "backups/AwesomeForTheXR/5CF071B3717923AB";
            string[] files = { managed, player, helper, "Awesome_Data/Plugins/OculusPlugin.dll" };
            foreach (var relative in files)
            {
                var clean = Path.Combine(source, backup, Path.GetFileName(relative));
                var from = File.Exists(clean) ? clean : Path.Combine(source, relative);
                var to = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to);
            }

            var service = PatcherService.ForGame(package);
            var expected = files.ToDictionary(f => f, f => Hash(File.ReadAllBytes(Path.Combine(folder, f))));
            string helperPath = Path.Combine(folder, helper);
            string playerPath = Path.Combine(folder, player);
            string manifest = Path.Combine(folder, "AwesomeForTheXR.manifest.json");
            CheckStatus(service, folder, true, false, "clean launcher fixture");

            // Force a failure at the last executable after the player was installed.
            using (File.Open(helperPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { service.Patch(folder); }
                catch (IOException) { failed = true; }
                if (!failed) throw new InvalidOperationException("Expected a sharing violation replacing the forwarder.");
            }
            foreach (var file in files)
                if (Hash(File.ReadAllBytes(Path.Combine(folder, file))) != expected[file])
                    throw new InvalidOperationException("Rollback failed: " + file);
            if (File.Exists(manifest) || File.Exists(Path.Combine(folder, plugin)))
                throw new InvalidOperationException("Failed transaction left an installation.");

            service.Patch(folder);
            CheckStatus(service, folder, false, true, "launcher install");
            CheckSceneGuards(Path.Combine(folder, managed));
            string patchedPlayer = Hash(File.ReadAllBytes(playerPath));
            if (patchedPlayer == expected[player] || Hash(File.ReadAllBytes(helperPath)) == expected[helper])
                throw new InvalidOperationException("The executable pair did not change.");

            File.Copy(Path.Combine(folder, backup, player), playerPath, true);
            CheckStatus(service, folder, true, true, "Steam restored only the player");
            service.Patch(folder);
            if (Hash(File.ReadAllBytes(playerPath)) != patchedPlayer)
                throw new InvalidOperationException("Reapplication produced a different player.");

            byte[] goodHelper = File.ReadAllBytes(helperPath);
            File.WriteAllBytes(helperPath, FirstBuild);
            CheckStatus(service, folder, false, false, "unknown launcher edit");
            File.WriteAllBytes(helperPath, goodHelper);
            var offline = PatcherService.ForGame(Path.Combine(temp, "absent package"));
            CheckStatus(offline, folder, false, true, "offline revert");
            offline.Revert(folder);
            foreach (var file in files)
                if (Hash(File.ReadAllBytes(Path.Combine(folder, file))) != expected[file])
                    throw new InvalidOperationException("Revert failed: " + file);
            if (File.Exists(manifest) || File.Exists(Path.Combine(folder, plugin)) ||
                Directory.GetFiles(Path.Combine(folder, backup), "*.previous").Length != 0)
                throw new InvalidOperationException("Revert left installation or transaction files.");
            Console.WriteLine("PASS: executable install, mid-install rollback, repair, tamper detection and offline revert.");
        }

        private static void CheckSceneGuards(string path)
        {
            using (var module = ModuleDefinition.ReadModule(path))
            {
                var loader = module.GetType("OculusCheckLoader");
                var body = loader.Methods.Single(m => m.Name == "Awake").Body.Instructions;
                var ready = loader.Methods.Single(m => m.Name == "AFTXR_IsOpenXRSceneReady");
                var enter = loader.Methods.Single(m => m.Name == "AFTXR_EnterOpenXRScene");
                var check = body.Single(i => i.OpCode == OpCodes.Call && i.Operand == ready);
                var scene = body.Single(i => i.OpCode == OpCodes.Call && i.Operand == enter);
                var store = (Instruction)check.Next.Operand;
                var mode = store.Next;
                var arcade = mode.Next.Next;
                var desktop = mode.Next.Operand as Instruction;
                if (check.Previous.OpCode != OpCodes.Brfalse || check.Previous.Operand != store.Previous ||
                    store.Previous.OpCode != OpCodes.Ldc_I4_0 || check.Next.OpCode != OpCodes.Br ||
                    store.OpCode != OpCodes.Stsfld || mode.OpCode != OpCodes.Ldsfld || mode.Operand != store.Operand ||
                    ((FieldReference)mode.Operand).Name != "isVRSupportEnabled" || mode.Next.OpCode != OpCodes.Brfalse ||
                    arcade.OpCode != OpCodes.Ldsfld || ((FieldReference)arcade.Operand).Name != "isDavebustersBuild" ||
                    arcade.Next.OpCode != OpCodes.Brtrue || arcade.Next.Operand != desktop || arcade.Next.Next != scene ||
                    desktop?.OpCode != OpCodes.Ldstr || (string)desktop.Operand != "AFTXR_LAUNCH_STATUS" ||
                    body.Any(i => i.Operand is MethodReference m && m.Name == "LoadLevel"))
                    throw new InvalidOperationException("The patched loader lost its guarded OpenXR scene entry.");

                if (!ready.Body.Instructions.Select(i => i.OpCode).SequenceEqual(new[] { OpCodes.Ldc_I4_0, OpCodes.Ret }) ||
                    !enter.Body.Instructions.Select(i => i.OpCode).SequenceEqual(new[] { OpCodes.Ret }))
                    throw new InvalidOperationException("The desktop milestone must keep both OpenXR scene hooks inactive.");
            }
        }
    }
}
