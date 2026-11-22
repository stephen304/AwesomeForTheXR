using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomePatcher.Core
{
    internal static class ButtonInputRecipe
    {
        internal static void Apply(ModuleDefinition game, ModuleReference plugin)
        {
            var input = game.GetType("GameInput");
            var legacy = input.Methods.Single(m => m.Name == "get_Parachute");
            var owner = input.Fields.Single(f => f.Name == "singleton");
            var alive = game.GetMemberReferences().OfType<MethodReference>().Single(m =>
                m.FullName == "System.Boolean UnityEngine.Object::op_Implicit(UnityEngine.Object)");
            var pressed = new FieldDefinition("aftxrDeployPressed", FieldAttributes.Private, game.TypeSystem.Boolean);
            input.Fields.Add(pressed);

            const string entry = "AFTXR_GetDeployParachutePressed";
            var native = new MethodDefinition(entry, MethodAttributes.Private | MethodAttributes.Static |
                MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, game.TypeSystem.Int32) {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle, entry, plugin)
            };
            native.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
            input.Methods.Add(native);

            // Preserve the physical press and admit XR deployment only during active gameplay.
            var read = new MethodDefinition("AFTXR_ReadParachutePress", MethodAttributes.Private | MethodAttributes.Static,
                game.TypeSystem.Boolean);
            input.Methods.Add(read);
            var il = read.Body.GetILProcessor();
            var options = game.GetType("Options");
            var utilities = game.GetType("Utilities");
            var accepted = il.Create(OpCodes.Ldc_I4_1);
            var rejected = il.Create(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, legacy);
            il.Emit(OpCodes.Brtrue, accepted);
            il.Emit(OpCodes.Ldsfld, options.Fields.Single(f => f.Name == "isVRSupportEnabled"));
            il.Emit(OpCodes.Brfalse, rejected);
            var time = game.GetTypeReferences().Single(t => t.FullName == "UnityEngine.Time");
            il.Emit(OpCodes.Call, new MethodReference("get_frameCount", game.TypeSystem.Int32, time));
            il.Emit(OpCodes.Call, native);
            il.Emit(OpCodes.Brfalse, rejected);
            il.Emit(OpCodes.Ldsfld, game.GetType("GameController").Fields.Single(f => f.Name == "singleton"));
            il.Emit(OpCodes.Call, alive);
            il.Emit(OpCodes.Brfalse, rejected);
            il.Emit(OpCodes.Call, utilities.Methods.Single(m => m.Name == "get_IsMidGame"));
            il.Emit(OpCodes.Ret);
            il.Append(accepted);
            il.Emit(OpCodes.Ret);
            il.Append(rejected);
            il.Emit(OpCodes.Ret);

            // Follow the retail collector's input gates, reset, and end-of-frame press lifetime.
            void StoreAfter(string method, string field, Instruction value)
            {
                var body = input.Methods.Single(m => m.Name == method).Body;
                var next = body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                    i.Operand is FieldReference f && f.DeclaringType == input && f.Name == field).Next;
                var edit = body.GetILProcessor();
                edit.InsertBefore(next, edit.Create(OpCodes.Ldarg_0));
                edit.InsertBefore(next, value);
                edit.InsertBefore(next, edit.Create(OpCodes.Stfld, pressed));
            }
            StoreAfter("GatherPCInput", "parachute", Instruction.Create(OpCodes.Call, read));
            StoreAfter("ResetInputs", "parachute", Instruction.Create(OpCodes.Ldc_I4_0));
            StoreAfter("LateUpdate", "lastParachute", Instruction.Create(OpCodes.Ldc_I4_0));

            const string name = "DeployParachutePressed";
            var getter = new MethodDefinition("get_" + name, MethodAttributes.Public | MethodAttributes.Static |
                MethodAttributes.HideBySig | MethodAttributes.SpecialName, game.TypeSystem.Boolean);
            input.Methods.Add(getter);
            input.Properties.Add(new PropertyDefinition(name, PropertyAttributes.None, game.TypeSystem.Boolean) { GetMethod = getter });
            var get = getter.Body.GetILProcessor();
            var readOwner = get.Create(OpCodes.Ldsfld, owner);
            get.Emit(OpCodes.Ldsfld, owner);
            get.Emit(OpCodes.Call, alive);
            get.Emit(OpCodes.Brtrue, readOwner);
            get.Emit(OpCodes.Ldc_I4_0);
            get.Emit(OpCodes.Ret);
            get.Append(readOwner);
            get.Emit(OpCodes.Ldfld, pressed);
            get.Emit(OpCodes.Ret);

            var update = game.GetType("Parachute").Methods.Single(m => m.Name == "Update");
            update.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand == legacy).Operand = getter;
        }
    }
}
