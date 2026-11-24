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
            var owner = input.Fields.Single(f => f.Name == "singleton");
            var alive = game.GetMemberReferences().OfType<MethodReference>().Single(m =>
                m.FullName == "System.Boolean UnityEngine.Object::op_Implicit(UnityEngine.Object)");
            var presses = new FieldDefinition("aftxrGameplayPresses", FieldAttributes.Private, game.TypeSystem.Int32);
            input.Fields.Add(presses);

            const string entry = "AFTXR_GetGameplayPresses";
            var native = new MethodDefinition(entry, MethodAttributes.Private | MethodAttributes.Static |
                MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, game.TypeSystem.Int32) {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle, entry, plugin)
            };
            native.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
            native.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
            input.Methods.Add(native);

            var vrEnabled = game.GetType("Options").Fields.Single(f => f.Name == "isVRSupportEnabled");
            var time = game.GetTypeReferences().Single(t => t.FullName == "UnityEngine.Time");
            var frame = new MethodReference("get_frameCount", game.TypeSystem.Int32, time);
            var gather = input.Methods.Single(m => m.Name == "GatherPCInput").Body;
            var afterGestures = gather.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                i.Operand is FieldReference field && field.DeclaringType == input && field.Name == "flipOff").Next;
            var collect = gather.GetILProcessor();
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Ldsfld, vrEnabled));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Brfalse, afterGestures));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Ldarg_0));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Call, frame));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Call, inputContext));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Call, native));
            collect.InsertBefore(afterGestures, collect.Create(OpCodes.Stfld, presses));
            void ClearAfter(string method, string field)
            {
                var body = input.Methods.Single(m => m.Name == method).Body;
                var next = body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                    i.Operand is FieldReference f && f.DeclaringType == input && f.Name == field).Next;
                var edit = body.GetILProcessor();
                edit.InsertBefore(next, edit.Create(OpCodes.Ldarg_0));
                edit.InsertBefore(next, edit.Create(OpCodes.Ldc_I4_0));
                edit.InsertBefore(next, edit.Create(OpCodes.Stfld, presses));
            }
            ClearAfter("ResetInputs", "flipOff");
            ClearAfter("LateUpdate", "lastFlipOff");

            // Merge after retail edge detection so adjacent physical and XR presses stay independent.
            void AddGameplayPress(string property, int mask)
            {
                var body = input.Properties.Single(p => p.Name == property).GetMethod.Body;
                var edit = body.GetILProcessor();
                var result = body.Instructions.Last();
                result.OpCode = OpCodes.Ldsfld; // Existing branches to the common return now enter the merge.
                result.Operand = owner;
                edit.Emit(OpCodes.Ldfld, presses);
                edit.Emit(OpCodes.Ldc_I4, mask);
                edit.Emit(OpCodes.And);
                edit.Emit(OpCodes.Ldc_I4_0);
                edit.Emit(OpCodes.Cgt_Un);
                edit.Emit(OpCodes.Or);
                edit.Emit(OpCodes.Ret);
            }
            AddGameplayPress("ThumbsUp", 1);
            AddGameplayPress("FlipOff", 2);
            AddGameplayPress("Graffiti", 4);

            // These retail buttons also serve results shortcuts. Merge XR only for their gameplay callers.
            void AddGameplayOnlyGetter(string physicalProperty, string name, int mask, string consumer)
            {
                var legacy = input.Properties.Single(p => p.Name == physicalProperty).GetMethod;
                var getter = new MethodDefinition("get_" + name, MethodAttributes.Public | MethodAttributes.Static |
                    MethodAttributes.HideBySig | MethodAttributes.SpecialName, game.TypeSystem.Boolean);
                input.Methods.Add(getter);
                input.Properties.Add(new PropertyDefinition(name, PropertyAttributes.None, game.TypeSystem.Boolean) { GetMethod = getter });
                var get = getter.Body.GetILProcessor();
                var readPhysical = get.Create(OpCodes.Call, legacy);
                get.Emit(OpCodes.Ldsfld, owner);
                get.Emit(OpCodes.Call, alive);
                get.Emit(OpCodes.Brtrue, readPhysical);
                get.Emit(OpCodes.Ldc_I4_0);
                get.Emit(OpCodes.Ret);
                get.Append(readPhysical);
                get.Emit(OpCodes.Ret);
                AddGameplayPress(name, mask);

                var update = game.GetType(consumer).Methods.Single(m => m.Name == "Update");
                update.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand == legacy).Operand = getter;
            }
            AddGameplayOnlyGetter("Parachute", "DeployParachutePressed", 8, "Parachute");
            AddGameplayOnlyGetter("Espresso", "UseEspressoPressed", 16, "Player");
        }
    }
}
