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
        internal const string PatchedHash = "DC11D9F4BE450E6F28EFE9CA4AF5ED54BBEE833192E5E892EC9268D7FA204FE3";
        private const string VrEnabled = "AwesomeForTheXR | VR mode enabled";
        private const string VrDisabled = "AwesomeForTheXR | VR mode disabled";
        private const string ModuleId = "9cdf774b-84c8-4e61-9bc0-b2132f76e6fe";

        internal static byte[] Rewrite(byte[] original)
        {
            if (PatchFiles.Digest(original) != OriginalHash)
                throw new InvalidDataException("The menu marker recipe requires the supported original assembly.");

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

                var dll = new ModuleReference("AwesomeForTheXR32");
                module.ModuleReferences.Add(dll);
                var getVersion = new MethodDefinition("AFTXR_GetMarkerVersion",
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl,
                    module.TypeSystem.Int32)
                {
                    ImplAttributes = MethodImplAttributes.PreserveSig,
                    PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle,
                        "AFTXR_GetMarkerVersion", dll)
                };
                loader.Methods.Add(getVersion);
                var status = new FieldDefinition("aftxrVrStatus", FieldAttributes.Assembly | FieldAttributes.Static,
                    module.TypeSystem.String);
                loader.Fields.Add(status);
                InsertStartupCheck(awake, modeRead.Next, getVersion, status, log);
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

        private static void InsertStartupCheck(MethodDefinition awake, Instruction sceneBranch,
            MethodDefinition getVersion, FieldDefinition status, MethodReference log)
        {
            var il = awake.Body.GetILProcessor();
            var disabled = il.Create(OpCodes.Ldstr, VrDisabled);
            var storeStatus = il.Create(OpCodes.Stsfld, status);
            // The game has parsed -vr. Keep its mode on the stack for the original scene branch.
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Call, getVersion));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Bne_Un, sceneBranch));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Dup));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Brfalse, disabled));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Ldstr, VrEnabled));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Br, storeStatus));
            il.InsertBefore(sceneBranch, disabled);
            il.InsertBefore(sceneBranch, storeStatus);
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Ldsfld, status));
            il.InsertBefore(sceneBranch, il.Create(OpCodes.Call, log));
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
            il.Emit(OpCodes.Ldc_R4, 300f);
            il.Emit(OpCodes.Ldc_R4, 32f);
            il.Emit(OpCodes.Newobj, rectCtor);
            il.Emit(OpCodes.Ldsfld, status);
            il.Emit(OpCodes.Call, box);
            il.Append(done);
        }
    }
}
