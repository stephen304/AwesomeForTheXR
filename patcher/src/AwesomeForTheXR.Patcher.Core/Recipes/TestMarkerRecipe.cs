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
        internal const string PatchedHash = "5C110C333F0DCB3BFEF00CB6C1A0407CF60A50CBFB69D85959B44019F1BF7D69";
        private const string Badge = "AwesomeForTheXR | native test patch v1";
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

                var awake = menu.Methods.Single(m => m.Name == "Awake" && !m.HasParameters);
                if (awake.MetadataToken.ToInt32() != 0x060006F6 || !awake.HasBody)
                    throw new InvalidDataException("The MainMenu initialization method differs from the recipe.");

                // Reuse the references from the game so all injected IL targets its original Mono profile.
                var rectCtor = ExistingMethod(module, 0x0A00009C,
                    "System.Void UnityEngine.Rect::.ctor(System.Single,System.Single,System.Single,System.Single)");
                var box = ExistingMethod(module, 0x0A000172,
                    "System.Void UnityEngine.GUI::Box(UnityEngine.Rect,System.String)");
                var log = ExistingMethod(module, 0x0A000033, "System.Void UnityEngine.Debug::Log(System.Object)");

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
                menu.Methods.Add(getVersion);
                var active = new FieldDefinition("aftxrMarkerActive", FieldAttributes.Private | FieldAttributes.Static,
                    module.TypeSystem.Boolean);
                menu.Fields.Add(active);
                InsertStartupCheck(awake, getVersion, active, log);
                AddMenuBadge(menu, active, rectCtor, box);

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

        private static void InsertStartupCheck(MethodDefinition awake, MethodDefinition getVersion,
            FieldDefinition active, MethodReference log)
        {
            var il = awake.Body.GetILProcessor();
            var originalStart = awake.Body.Instructions[0];
            il.InsertBefore(originalStart, il.Create(OpCodes.Call, getVersion));
            il.InsertBefore(originalStart, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(originalStart, il.Create(OpCodes.Ceq));
            il.InsertBefore(originalStart, il.Create(OpCodes.Stsfld, active));
            il.InsertBefore(originalStart, il.Create(OpCodes.Ldsfld, active));
            il.InsertBefore(originalStart, il.Create(OpCodes.Brfalse, originalStart));
            il.InsertBefore(originalStart, il.Create(OpCodes.Ldstr, Badge));
            il.InsertBefore(originalStart, il.Create(OpCodes.Call, log));
        }

        private static void AddMenuBadge(TypeDefinition menu, FieldDefinition active,
            MethodReference rectCtor, MethodReference box)
        {
            var draw = new MethodDefinition("OnGUI", MethodAttributes.Private | MethodAttributes.HideBySig,
                menu.Module.TypeSystem.Void);
            menu.Methods.Add(draw);
            var il = draw.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldsfld, active);
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldc_R4, 12f);
            il.Emit(OpCodes.Ldc_R4, 12f);
            il.Emit(OpCodes.Ldc_R4, 300f);
            il.Emit(OpCodes.Ldc_R4, 32f);
            il.Emit(OpCodes.Newobj, rectCtor);
            il.Emit(OpCodes.Ldstr, Badge);
            il.Emit(OpCodes.Call, box);
            il.Append(done);
        }
    }
}
