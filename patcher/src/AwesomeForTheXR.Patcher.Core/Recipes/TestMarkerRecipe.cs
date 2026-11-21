using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomeForTheXR.Patcher.Core
{
    // One recipe for the fingerprinted Unity 4.5.5p5 Windows build.
    internal static class TestMarkerRecipe
    {
        internal const string OriginalHash = "5CF071B3717923AB22111754867F8091F9977C7088BCCA82963D34F033688D97";
        internal const string PatchedHash = "8F15612E80B22215A85F32E02E55F49F7A695CDC4BDB8116E5F79D311051DBD3";
        private const string VrDisabled = "AwesomeForTheXR | VR mode disabled";
        private const string ModuleId = "9cdf774b-84c8-4e61-9bc0-b2132f76e6fe";

        internal static byte[] Rewrite(byte[] original)
        {
            if (PatchFiles.Digest(original) != OriginalHash)
                throw new InvalidDataException("The launcher recipe requires the supported original assembly.");

            using (var input = new MemoryStream(original, false))
            using (var module = ModuleDefinition.ReadModule(input))
            using (var output = new MemoryStream())
            {
                var menu = module.GetType("MainMenu");
                if (module.Mvid != new Guid(ModuleId) || menu == null ||
                    menu.BaseType.FullName != "UnityEngine.MonoBehaviour" ||
                    menu.Methods.Any(m => m.Name == "OnGUI"))
                    throw new InvalidDataException("The expected MainMenu component was not found.");

                var loader = module.GetType("OculusCheckLoader");
                var awake = loader?.Methods.SingleOrDefault(m => m.Name == "Awake" && !m.HasParameters);
                if (awake == null || awake.MetadataToken.ToInt32() != 0x0600042B || !awake.HasBody)
                    throw new InvalidDataException("The expected VR startup method was not found.");

                // Reuse the references from the game so all injected IL targets its original Mono profile.
                var rectCtor = ExistingMethod(module, 0x0A00009C,
                    "System.Void UnityEngine.Rect::.ctor(System.Single,System.Single,System.Single,System.Single)");
                var box = ExistingMethod(module, 0x0A000172,
                    "System.Void UnityEngine.GUI::Box(UnityEngine.Rect,System.String)");
                var log = ExistingMethod(module, 0x0A000033, "System.Void UnityEngine.Debug::Log(System.Object)");
                var vrEnabled = module.GetType("Options")?.Fields.SingleOrDefault(field =>
                    field.Name == "isVRSupportEnabled" && field.IsPublic && field.IsStatic &&
                    field.FieldType.MetadataType == MetadataType.Boolean);
                if (vrEnabled == null)
                    throw new InvalidDataException("The game's VR mode field differs from the recipe.");
                var modeRead = awake.Body.Instructions.SingleOrDefault(i =>
                    i.OpCode == OpCodes.Ldsfld && i.Operand == vrEnabled && i.Next?.OpCode == OpCodes.Brfalse);
                if (modeRead == null)
                    throw new InvalidDataException("The expected VR scene selection was not found.");

                var status = new FieldDefinition("aftxrVrStatus", FieldAttributes.Assembly | FieldAttributes.Static,
                    module.TypeSystem.String);
                loader.Fields.Add(status);
                ReplaceOculusSelection(awake, modeRead, vrEnabled, status, log);
                AddMenuBadge(menu, status, rectCtor, box);

                // Pin the PE timestamp so the same source produces the release digest on every install.
                module.Write(output, new WriterParameters { Timestamp = 0 });
                return output.ToArray();
            }
        }

        private static MethodReference ExistingMethod(ModuleDefinition module, int token, string signature)
        {
            var method = module.LookupToken(token) as MethodReference;
            if (method == null || method.FullName != signature)
                throw new InvalidDataException("Unexpected Unity method reference: " + signature);
            return method;
        }

        private static void ReplaceOculusSelection(MethodDefinition awake, Instruction modeRead,
            FieldDefinition vrEnabled, FieldDefinition status, MethodReference log)
        {
            // Stock IL_036f..0376 promotes the raw -vr request to gameplay VR.
            // Replace that assignment only. Keep the two original scene guards.
            var input = awake.Body.Instructions.Single(i => i.Offset == 0x036f);
            var oldStore = modeRead.Previous;
            var vrBranch = modeRead.Next;
            var arcadeRead = vrBranch.Next;
            var arcadeBranch = arcadeRead.Next;
            var sceneName = arcadeBranch.Next;
            var loadLevel = sceneName.Next;
            var done = loadLevel.Next;
            if (input.OpCode != OpCodes.Ldloc_0 || input.Next.OpCode != OpCodes.Brfalse ||
                input.Next.Operand != modeRead || input.Next.Next.OpCode != OpCodes.Ldc_I4_1 ||
                input.Next.Next.Next != oldStore || oldStore.OpCode != OpCodes.Stsfld || oldStore.Operand != vrEnabled ||
                arcadeRead.OpCode != OpCodes.Ldsfld || !(arcadeRead.Operand is FieldReference arcadeFlag) ||
                arcadeFlag.FullName != "System.Boolean Options::isDavebustersBuild" ||
                arcadeBranch.OpCode != OpCodes.Brtrue || vrBranch.Operand != done || arcadeBranch.Operand != done ||
                sceneName.OpCode != OpCodes.Ldstr || (string)sceneName.Operand != "LoaderScene_oculus" ||
                loadLevel.OpCode != OpCodes.Call || !(loadLevel.Operand is MethodReference load) ||
                load.FullName != "System.Void UnityEngine.Application::LoadLevel(System.String)" || done.OpCode != OpCodes.Ret)
                throw new InvalidDataException("The original VR and arcade scene guards differ from the recipe.");

            var module = awake.Module;
            var environment = new TypeReference("System", "Environment", module, module.TypeSystem.CoreLibrary);
            var read = new MethodReference("GetEnvironmentVariable", module.TypeSystem.String, environment);
            read.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            var equals = ExistingMethod(module, 0x0a0000f6,
                "System.Boolean System.String::op_Equality(System.String,System.String)");

            var attrs = MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig;
            var sceneReady = new MethodDefinition("AFTXR_IsOpenXRSceneReady", attrs, module.TypeSystem.Boolean);
            awake.DeclaringType.Methods.Add(sceneReady);
            // Future: validate Unity's actual D3D11 device and session eligibility.
            // A successful graphics preflight alone cannot enable the stock VR rig.
            var readyIL = sceneReady.Body.GetILProcessor();
            readyIL.Emit(OpCodes.Ldc_I4_0);
            readyIL.Emit(OpCodes.Ret);

            var enterScene = new MethodDefinition("AFTXR_EnterOpenXRScene", attrs, module.TypeSystem.Void);
            awake.DeclaringType.Methods.Add(enterScene);
            // Future: enter the ported scene once its legacy callbacks have replacements.
            enterScene.Body.GetILProcessor().Emit(OpCodes.Ret);

            var il = awake.Body.GetILProcessor();
            var noGraphics = il.Create(OpCodes.Ldc_I4_0);
            var ifPrepared = input.Next;
            var checkScene = ifPrepared.Next;
            input.OpCode = OpCodes.Ldstr;
            input.Operand = "AFTXR_GRAPHICS_PREPARED";
            il.InsertBefore(ifPrepared, il.Create(OpCodes.Call, read));
            il.InsertBefore(ifPrepared, il.Create(OpCodes.Ldstr, "1"));
            il.InsertBefore(ifPrepared, il.Create(OpCodes.Call, equals));
            ifPrepared.Operand = noGraphics;
            checkScene.OpCode = OpCodes.Call;
            checkScene.Operand = sceneReady;
            il.InsertBefore(oldStore, il.Create(OpCodes.Br, oldStore));
            il.InsertBefore(oldStore, noGraphics);

            sceneName.OpCode = OpCodes.Call;
            sceneName.Operand = enterScene;
            il.Remove(loadLevel);
            // Both original guard exits still target this instruction and report the outcome.
            done.OpCode = OpCodes.Ldstr;
            done.Operand = "AFTXR_LAUNCH_STATUS";
            il.Emit(OpCodes.Call, read);
            var store = il.Create(OpCodes.Stsfld, status);
            il.InsertBefore(done, il.Create(OpCodes.Ldstr, "AwesomeForTheXR | VR mode enabled"));
            il.InsertBefore(done, il.Create(OpCodes.Br, store));
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue, store);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, VrDisabled);
            il.Append(store);
            il.Emit(OpCodes.Ldsfld, status);
            il.Emit(OpCodes.Call, log);
            il.Emit(OpCodes.Ret);
        }

        private static void AddMenuBadge(TypeDefinition menu, FieldDefinition status,
            MethodReference rectCtor, MethodReference box)
        {
            var draw = new MethodDefinition("OnGUI", MethodAttributes.Private | MethodAttributes.HideBySig,
                menu.Module.TypeSystem.Void);
            menu.Methods.Add(draw);
            var il = draw.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldsfld, status);
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldc_R4, 12f);
            il.Emit(OpCodes.Ldc_R4, 12f);
            il.Emit(OpCodes.Ldc_R4, 460f);
            il.Emit(OpCodes.Ldc_R4, 32f);
            il.Emit(OpCodes.Newobj, rectCtor);
            il.Emit(OpCodes.Ldsfld, status);
            il.Emit(OpCodes.Call, box);
            il.Append(done);
        }
    }
}
