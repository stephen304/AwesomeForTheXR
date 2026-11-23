using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomePatcher.Core
{
    internal static class ButtonInputRecipe
    {
        internal static void Apply(ModuleDefinition game, ModuleReference plugin, MethodDefinition inputContext)
        {
            var input = game.GetType("GameInput");
            var legacy = input.Methods.Single(m => m.Name == "get_Parachute");
            var owner = input.Fields.Single(f => f.Name == "singleton");
            var alive = game.GetMemberReferences().OfType<MethodReference>().Single(m =>
                m.FullName == "System.Boolean UnityEngine.Object::op_Implicit(UnityEngine.Object)");
            var pressed = new FieldDefinition("aftxrDeployPressed", FieldAttributes.Private, game.TypeSystem.Boolean);
            input.Fields.Add(pressed);

            MethodDefinition NativePressReader(string entry)
            {
                var method = new MethodDefinition(entry, MethodAttributes.Private | MethodAttributes.Static |
                    MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, game.TypeSystem.Int32) {
                    ImplAttributes = MethodImplAttributes.PreserveSig,
                    PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle, entry, plugin)
                };
                method.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
                method.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
                input.Methods.Add(method);
                return method;
            }
            var native = NativePressReader("AFTXR_GetDeployParachutePressed");

            // The original physical press takes precedence. XR deployment is gated by the game action set.
            var read = new MethodDefinition("AFTXR_ReadParachutePress", MethodAttributes.Private | MethodAttributes.Static,
                game.TypeSystem.Boolean);
            input.Methods.Add(read);
            var il = read.Body.GetILProcessor();
            var options = game.GetType("Options");
            var accepted = il.Create(OpCodes.Ldc_I4_1);
            var rejected = il.Create(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, legacy);
            il.Emit(OpCodes.Brtrue, accepted);
            var vrEnabled = options.Fields.Single(f => f.Name == "isVRSupportEnabled");
            il.Emit(OpCodes.Ldsfld, vrEnabled);
            il.Emit(OpCodes.Brfalse, rejected);
            var time = game.GetTypeReferences().Single(t => t.FullName == "UnityEngine.Time");
            var frame = new MethodReference("get_frameCount", game.TypeSystem.Int32, time);
            il.Emit(OpCodes.Call, frame);
            il.Emit(OpCodes.Call, inputContext);
            il.Emit(OpCodes.Call, native);
            il.Emit(OpCodes.Ret);
            il.Append(accepted);
            il.Emit(OpCodes.Ret);
            il.Append(rejected);
            il.Emit(OpCodes.Ret);

            // Follow the retail collector's input gates, reset, and end-of-frame press lifetime.
            void StoreAfter(string method, string field, FieldDefinition target, Instruction value)
            {
                var body = input.Methods.Single(m => m.Name == method).Body;
                var next = body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                    i.Operand is FieldReference f && f.DeclaringType == input && f.Name == field).Next;
                var edit = body.GetILProcessor();
                edit.InsertBefore(next, edit.Create(OpCodes.Ldarg_0));
                edit.InsertBefore(next, value);
                edit.InsertBefore(next, edit.Create(OpCodes.Stfld, target));
            }
            StoreAfter("GatherPCInput", "parachute", pressed, Instruction.Create(OpCodes.Call, read));
            StoreAfter("ResetInputs", "parachute", pressed, Instruction.Create(OpCodes.Ldc_I4_0));
            StoreAfter("LateUpdate", "lastParachute", pressed, Instruction.Create(OpCodes.Ldc_I4_0));

            var gestures = new FieldDefinition("aftxrGesturePresses", FieldAttributes.Private, game.TypeSystem.Int32);
            input.Fields.Add(gestures);
            var gather = input.Methods.Single(m => m.Name == "GatherPCInput").Body;
            var afterGestures = gather.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                i.Operand is FieldReference field && field.DeclaringType == input && field.Name == "flipOff").Next;
            var collect = gather.GetILProcessor();
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Ldsfld, vrEnabled));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Brfalse, afterGestures));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Ldarg_0));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Call, frame));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Call, inputContext));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Call, NativePressReader("AFTXR_GetSpectatorGesturePresses")));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Stfld, gestures));
            StoreAfter("ResetInputs", "flipOff", gestures, Instruction.Create(OpCodes.Ldc_I4_0));
            StoreAfter("LateUpdate", "lastFlipOff", gestures, Instruction.Create(OpCodes.Ldc_I4_0));

            // Merge after retail edge detection so adjacent physical and XR presses stay independent.
            void AddGesture(string property, int mask)
            {
                var body = input.Properties.Single(p => p.Name == property).GetMethod.Body;
                var edit = body.GetILProcessor();
                var result = body.Instructions.Last();
                result.OpCode = OpCodes.Ldsfld; // Existing branches to the common return now enter the merge.
                result.Operand = owner;
                edit.Emit(OpCodes.Ldfld, gestures);
                edit.Emit(OpCodes.Ldc_I4, mask);
                edit.Emit(OpCodes.And);
                edit.Emit(OpCodes.Ldc_I4_0);
                edit.Emit(OpCodes.Cgt_Un);
                edit.Emit(OpCodes.Or);
                edit.Emit(OpCodes.Ret);
            }
            AddGesture("ThumbsUp", 1);
            AddGesture("FlipOff", 2);

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
