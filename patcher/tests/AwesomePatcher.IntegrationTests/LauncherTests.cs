using System;
using System.IO;
using System.Linq;
using AwesomePatcher.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomePatcher.IntegrationTests
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

            var catalog = new TestArtworkCatalog();
            var service = new PatcherService(package, catalog);
            string art = Path.Combine(folder, SteamVrArtwork.Name);
            var expected = files.ToDictionary(f => f, f => Hash(File.ReadAllBytes(Path.Combine(folder, f))));
            string helperPath = Path.Combine(folder, helper);
            string playerPath = Path.Combine(folder, player);
            string manifest = Path.Combine(folder, "AwesomeForTheXR.manifest.json");
            const string loader = "openxr_loader.dll";
            string loaderPath = Path.Combine(folder, loader);
            File.WriteAllBytes(loaderPath, FirstBuild);
            CheckStatus(service, folder, false, false, "unowned loader collision");
            File.Delete(loaderPath);
            File.WriteAllText(art, "third-party data");
            CheckStatus(service, folder, false, false, "unowned artwork collision");
            File.Delete(art);
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

            catalog.FailAfterAdd = true;
            try { service.Patch(folder); throw new InvalidOperationException("Expected a registration failure."); }
            catch (IOException) { }
            if (File.Exists(art) || File.Exists(manifest) || catalog.Registered)
                throw new InvalidOperationException("Partial artwork installation was not rolled back.");

            service.Patch(folder);
            CheckStatus(service, folder, false, true, "launcher install");
            if (!catalog.Registered || !File.Exists(art)) throw new InvalidOperationException("Artwork was not installed.");

            // Simulate the title-only release. Its installation record has no artwork field.
            catalog.Registered = false;
            File.Delete(art);
            var oldRecord = PatchFiles.Deserialize<InstallRecord>(File.ReadAllBytes(manifest));
            oldRecord.Format = "AwesomeForTheXR/openxr-install/4";
            oldRecord.ArtworkSha256 = null;
            File.WriteAllBytes(manifest, PatchFiles.Serialize(oldRecord));
            CheckStatus(service, folder, true, true, "upgrade from the title-only patch");
            service.Patch(folder);
            catalog.Registered = false;
            CheckStatus(service, folder, true, true, "lost SteamVR registration");
            service.Patch(folder);
            if (!catalog.Registered) throw new InvalidOperationException("Registration was not repaired.");

            catalog.OtherProvider = true;
            CheckStatus(service, folder, true, true, "new Steam or third-party provider");
            service.Patch(folder);
            if (catalog.Registered || !catalog.OtherProvider) throw new InvalidOperationException("Provider conflict.");
            CheckStatus(service, folder, false, true, "existing provider retained");
            catalog.OtherProvider = false;
            catalog.Available = false;
            CheckStatus(service, folder, false, true, "SteamVR absent");
            catalog.Available = true;
            service.Patch(folder);
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
            var offline = new PatcherService(Path.Combine(temp, "absent package"), catalog);
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
            if (!catalog.Registered || !File.Exists(art)) throw new InvalidOperationException("Artwork rollback failed.");
            CheckBytes(manifest, installedManifest, "manifest after interrupted revert");
            CheckBytes(loaderPath, loaderBytes, "loader after interrupted revert");
            CheckBytes(Path.Combine(folder, plugin), installedPlugin, "plugin after interrupted revert");
            CheckStatus(offline, folder, false, true, "offline recovery after interrupted revert");
            offline.Revert(folder);
            if (catalog.Registered || File.Exists(art)) throw new InvalidOperationException("Revert left artwork behind.");
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
            var prepare = rig.Methods.Single(m => m.Name == "AFTXR_PrepareHeadPose");
            var pointer = module.GetType("AFTXRMenuPointer");
            bool Calls(MethodDefinition method, string name) =>
                method.Body.Instructions.Any(i => i.Operand is MethodReference target && target.Name == name);
            var sample = pointer.Methods.Single(m => m.Name == "Sample");
            var head = prepare.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "ApplyPose");
            var calls = anchors.Where(i => i.Operand is MethodReference m && m.Name == "ApplyPose").Concat(new[] { head }).ToArray();
            if (!anchors.Any(i => i.Operand == prepare) || !sample.Body.Instructions.Any(i => i.Operand == prepare) ||
                Calls(sample, "UpdateAnchors") || Calls(prepare, "AFTXR_DesktopUpdate") || Calls(prepare, "ApplyProjection") ||
                !Calls(prepare, "BeginFrame") || !Calls(rig.Methods.Single(m => m.Name == "OnDisable"), "ReleaseRig") ||
                !Calls(pointer.Methods.Single(m => m.Name == "Visibility"), "ResetPointer") || !Calls(sample, "ResetPointer"))
                throw new InvalidOperationException("Pointer lifecycle or shared head prediction is missing.");
            var register = pointer.Methods.Single(m => m.Name == "RegisterPanel");
            var setter = rig.Methods.Single(m => m.Name == "AFTXR_SetPointerPanel");
            var field = rig.Fields.Single(f => f.Name == "aftxrPointerPanel");
            var enable = rig.Methods.Single(m => m.Name == "OnEnable");
            if (!Calls(module.GetType("ScaleOculusPlaneByAspect").Methods.Single(m => m.Name == "Start"), "RegisterPanel") ||
                !register.Body.Instructions.Any(i => i.Operand == setter) ||
                !setter.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand == field) ||
                !enable.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand == field) ||
                !Calls(enable, "SetRig") || !Calls(setter, "SetRig") || Calls(sample, "BindPanel") ||
                pointer.Methods.Any(m => m.HasBody && Calls(m, "FindObjectsOfType")))
                throw new InvalidOperationException("The rig must supply its own panel on registration and re-enable.");
            if (!Calls(module.GetType("InControl.UnityMouseAxisSource").Methods.Single(m => m.Name == "GetValue"), "RawMouseAxis") ||
                !Calls(module.GetType("InControl.UnityAnalogSource").Methods.Single(m => m.Name == "GetValue"), "GetAxisRaw"))
                throw new InvalidOperationException("InControl mouse and joystick axes must remain independently routed.");
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
                var display = module.GetType("OVRDisplay");
                var manager = module.GetType("OVRManager");
                var rig = module.GetType("OVRCameraRig");
                var mirror = rig.Methods.Single(m => m.Name == "AFTXR_DesktopUpdate");
                var calls = mirror.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToList();
                if (manager.Methods.Any(m => m.Name == "OnGUI") ||
                    !rig.Methods.Single(m => m.Name == "UpdateAnchors").Body.Instructions.Any(i => i.Operand == mirror) ||
                    !calls.Any(m => m.Name == "get_centerEyeAnchor") ||
                    !calls.Any(m => m.Name == "AddComponent" && m is GenericInstanceMethod g && g.GenericArguments[0].Name == "Camera") ||
                    calls.Any(m => m.Name == "GetEyeTexture" || m.Name == "set_projectionMatrix" ||
                        m.Name == "set_aspect" || m.Name == "set_targetTexture" || m.Name == "AFTXR_RenderedEye"))
                    throw new InvalidOperationException("Expected an independent center camera with an automatic window projection.");
                var bloom = module.GetType("AFTXRDesktopBloom");
                var refresh = rig.Methods.Single(m => m.Name == "AFTXR_DesktopRefreshBloomQuality");
                var quality = module.GetType("PostEffectsQualityManager").Methods.Single(m => m.Name == "DetermineEffects");
                if (!calls.OfType<GenericInstanceMethod>().Any(m => m.Name == "AddComponent" && m.GenericArguments[0] == bloom) ||
                    !bloom.Methods.Single(m => m.Name == "OnDestroy").Body.Instructions.Any(i =>
                        i.Operand is MethodReference destroy && destroy.FullName == "System.Void UnityEngine.Object::DestroyImmediate(UnityEngine.Object)") ||
                    !quality.Body.Instructions.Any(i => i.Operand == refresh) ||
                    !refresh.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld).Select(i => ((FieldReference)i.Operand).Name)
                        .SequenceEqual(new[] { "bloomBlurIterations", "lensflares", "lensflareIntensity" }))
                    throw new InvalidOperationException("The desktop bloom must release its materials and follow the game's quality settings.");
                var getter = display.Methods.Single(m => m.Name == "get_mirrorMode").Body.Instructions;
                var setter = display.Methods.Single(m => m.Name == "set_mirrorMode").Body.Instructions;
                if (getter.Count != 2 || getter[0].OpCode != OpCodes.Ldc_I4_1 || getter[1].OpCode != OpCodes.Ret ||
                    setter.Single().OpCode != OpCodes.Ret || manager.Methods.Single(m => m.Name == "Update").Body.Instructions.Single().OpCode != OpCodes.Ret ||
                    module.GetType("OVRMainMenu").Methods.Single(m => m.Name == "Update").Body.Instructions
                        .Any(i => i.Operand is MethodReference m && m.Name == "set_mirrorMode"))
                    throw new InvalidOperationException("The VR mirror must stay enabled across scenes and ignore the legacy hotkey.");
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
