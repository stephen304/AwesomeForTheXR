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
            const string loader = "openxr_loader.dll";
            string loaderPath = Path.Combine(folder, loader);
            File.WriteAllBytes(loaderPath, FirstBuild);
            CheckStatus(service, folder, false, false, "unowned loader collision");
            File.Delete(loaderPath);
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
            if (File.Exists(manifest) || File.Exists(loaderPath) || File.Exists(Path.Combine(folder, plugin)))
                throw new InvalidOperationException("Failed transaction left an installation.");

            service.Patch(folder);
            CheckStatus(service, folder, false, true, "launcher install");
            CheckSceneGuards(Path.Combine(folder, managed));
            var loaderBytes = File.ReadAllBytes(loaderPath);
            CheckBytes(loaderPath, File.ReadAllBytes(Path.Combine(package, loader)), "packaged loader installed");
            File.WriteAllBytes(loaderPath, FirstBuild);
            CheckStatus(service, folder, false, false, "modified installed loader");
            File.WriteAllBytes(loaderPath, loaderBytes);
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
            // Deny the final manifest removal, after the loader and plugin have both
            // been removed. Recovery must restore their exact bytes without a package.
            byte[] installedManifest = File.ReadAllBytes(manifest);
            byte[] installedPlugin = File.ReadAllBytes(Path.Combine(folder, plugin));
            using (File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { offline.Revert(folder); }
                catch (IOException) { failed = true; }
                if (!failed) throw new InvalidOperationException("Expected a blocked manifest removal.");
            }
            CheckBytes(manifest, installedManifest, "manifest after interrupted revert");
            CheckBytes(loaderPath, loaderBytes, "loader after interrupted revert");
            CheckBytes(Path.Combine(folder, plugin), installedPlugin, "plugin after interrupted revert");
            CheckStatus(offline, folder, false, true, "offline recovery after interrupted revert");
            offline.Revert(folder);
            foreach (var file in files)
                if (Hash(File.ReadAllBytes(Path.Combine(folder, file))) != expected[file])
                    throw new InvalidOperationException("Revert failed: " + file);
            if (File.Exists(manifest) || File.Exists(loaderPath) || File.Exists(Path.Combine(folder, plugin)) ||
                Directory.GetFiles(Path.Combine(folder, backup), "*.previous").Length != 0)
                throw new InvalidOperationException("Revert left installation or transaction files.");
            Console.WriteLine("PASS: executable install, mid-install rollback, repair, tamper detection and offline revert.");
        }

        private static void CheckHeadAnchors(ModuleDefinition module)
        {
            var display = module.GetType("OVRDisplay");
            var getHead = display.Methods.Single(m => m.Name == "GetHeadPose").Body.Instructions;
            var rig = module.GetType("OVRCameraRig");
            var anchors = rig.Methods.Single(m => m.Name == "UpdateAnchors").Body.Instructions;
            var calls = anchors.Where(i => i.Operand is MethodReference m && m.Name == "ApplyPose").ToArray();
            var sources = new[] { "get_leftEyeAnchor", "get_rightEyeAnchor", "get_centerEyeAnchor" };
            var ids = new[] { OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_R8 };
            if (getHead[1].OpCode != OpCodes.Ldc_I4_M1 || ((MethodReference)getHead[2].Operand).Name != "GetEyePose" ||
                calls.Length != 3)
                throw new InvalidOperationException("Missing dedicated center pose.");
            for (int i = 0; i < calls.Length; ++i)
            {
                var read = calls[i].Previous;
                if (((MethodReference)read.Operand).Name != (i == 2 ? "GetHeadPose" : "GetEyePose") ||
                    read.Previous.OpCode != ids[i] || ((MethodReference)read.Previous.Previous.Previous.Operand).Name != sources[i])
                    throw new InvalidOperationException("Unity anchor received another viewpoint's pose.");
            }
            foreach (var target in new[] { "Player.TagGraffiti", "PlayerProximity.CastMouseRay" })
            {
                var names = target.Split('.');
                var body = module.GetType(names[0]).Methods.Single(m => m.Name == names[1]).Body.Instructions;
                var ray = body.Single(i => i.Operand is MethodReference m && m.Name == "HeadRay");
                if (((FieldReference)ray.Previous.Operand).Name != "aftxrHeadAnchor" ||
                    !body.Any(i => i.Operand is MethodReference m && m.Name == "ScreenPointToRay"))
                    throw new InvalidOperationException("Missing head or desktop aim in " + target);
            }
        }

        private static void CheckSceneGuards(string path)
        {
            using (var module = ModuleDefinition.ReadModule(path))
            {
                CheckHeadAnchors(module);
                var loader = module.GetType("OculusCheckLoader");
                var body = loader.Methods.Single(m => m.Name == "Awake").Body.Instructions;
                var ready = loader.Methods.Single(m => m.Name == "AFTXR_IsOpenXRSceneReady");
                var enter = loader.Methods.Single(m => m.Name == "AFTXR_EnterOpenXRScene");
                var check = body.Single(i => i.OpCode == OpCodes.Call && i.Operand == ready);
                var scene = body.Single(i => i.OpCode == OpCodes.Call && i.Operand == enter);
                var gameplayBranch = check.Next;
                var accepted = (Instruction)gameplayBranch.Operand;
                var store = (Instruction)accepted.Next.Operand;
                var abort = loader.Methods.Single(m => m.Name == "AFTXR_AbortVrLaunch");
                var rejected = gameplayBranch.Next;
                var mode = store.Next;
                var arcade = mode.Next.Next;
                var desktop = mode.Next.Operand as Instruction;
                var prepared = check.Previous.Previous.Previous;
                var observer = module.GetType("AFTXREyeCamera");
                var driver = module.GetType("OVRDisplay").Methods.Single(m => m.Name == "EndFrame").Body.Instructions;
                var shutdown = module.GetType("OVRManager").Methods.Single(m => m.Name == "OnApplicationQuit").Body.Instructions;
                if (prepared.OpCode != OpCodes.Brfalse || prepared.Operand != store.Previous ||
                    check.Previous.Operand != arcade.Next.Operand || check.Previous.Previous.Operand != arcade.Operand ||
                    !observer.Methods.Single(m => m.Name == "OnPostRender").Body.Instructions
                        .Any(i => i.Operand is MethodReference fn && fn.Name == "AFTXR_RenderedEye") ||
                    !driver.Any(i => i.Operand is MethodReference fn && fn.Name == "IssuePluginEvent") ||
                    !shutdown.Any(i => i.Operand is MethodReference fn && fn.Name == "AFTXR_ReleaseLaunch") ||
                    store.Previous.OpCode != OpCodes.Ldc_I4_0 || gameplayBranch.OpCode != OpCodes.Brtrue ||
                    accepted.OpCode != OpCodes.Ldc_I4_1 || accepted.Next.OpCode != OpCodes.Br ||
                    rejected.OpCode != OpCodes.Ldnull || rejected.Next.OpCode != OpCodes.Call ||
                    rejected.Next.Operand != abort || rejected.Next.Next.OpCode != OpCodes.Ret ||
                    store.OpCode != OpCodes.Stsfld || mode.OpCode != OpCodes.Ldsfld || mode.Operand != store.Operand ||
                    ((FieldReference)mode.Operand).Name != "isVRSupportEnabled" || mode.Next.OpCode != OpCodes.Brfalse ||
                    arcade.OpCode != OpCodes.Ldsfld || ((FieldReference)arcade.Operand).Name != "isDavebustersBuild" ||
                    arcade.Next.OpCode != OpCodes.Brtrue || !(arcade.Next.Operand is Instruction arcadeError) ||
                    arcadeError.OpCode != OpCodes.Ldstr || arcadeError.Next.Operand != abort || arcade.Next.Next != scene ||
                    desktop?.OpCode != OpCodes.Ldstr || (string)desktop.Operand != "AwesomeForTheXR | VR mode disabled" ||
                    body.Any(i => i.Operand is MethodReference m && m.Name == "LoadLevel"))
                    throw new InvalidOperationException("The patched loader lost its guarded OpenXR scene entry.");

                if (ready.PInvokeInfo?.EntryPoint != "AFTXR_IsOpenXRSceneReady" ||
                    ready.ReturnType.MetadataType != MetadataType.Int32 ||
                    abort.PInvokeInfo?.EntryPoint != "AFTXR_AbortVrLaunch" ||
                    !enter.Body.Instructions.Select(i => i.OpCode).SequenceEqual(new[] { OpCodes.Ldstr, OpCodes.Call, OpCodes.Ret }) ||
                    (string)enter.Body.Instructions[0].Operand != "LoaderScene_oculus" ||
                    ((MethodReference)enter.Body.Instructions[1].Operand).Name != "LoadLevel")
                    throw new InvalidOperationException("The loader must check native graphics readiness before the guarded scene entry.");
            }
        }
    }
}
