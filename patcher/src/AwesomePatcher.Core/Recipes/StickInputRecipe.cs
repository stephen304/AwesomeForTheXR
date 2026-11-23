using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomePatcher.Core
{
    internal static class StickInputRecipe
    {
        internal static void Apply(ModuleDefinition game, ModuleReference plugin, MethodDefinition inputContext)
        {
            var input = game.GetType("GameInput");
            var gather = input.Methods.Single(m => m.Name == "GatherPCInput");
            var il = gather.Body.GetILProcessor();
            var vector = game.GetTypeReferences().Single(t => t.FullName == "UnityEngine.Vector2");
            var move = new VariableDefinition(vector);
            var turn = new VariableDefinition(vector);
            var analogMove = new VariableDefinition(vector);
            gather.Body.Variables.Add(move);
            gather.Body.Variables.Add(turn);
            gather.Body.Variables.Add(analogMove);
            gather.Body.InitLocals = true;

            const string entry = "AFTXR_MergeGamepadSticks";
            var merge = new MethodDefinition(entry, MethodAttributes.Private | MethodAttributes.Static |
                MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, game.TypeSystem.Int32) {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle, entry, plugin)
            };
            merge.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
            merge.Parameters.Add(new ParameterDefinition(game.TypeSystem.Int32));
            merge.Parameters.Add(new ParameterDefinition(new ByReferenceType(vector)));
            merge.Parameters.Add(new ParameterDefinition(new ByReferenceType(vector)));
            input.Methods.Add(merge);

            // Just after the retail inputEnabled guard and mouse scale initialization.
            var start = gather.Body.Instructions.Single(i => i.Offset == 0x12);
            var x = new FieldReference("x", game.TypeSystem.Single, vector);
            var y = new FieldReference("y", game.TypeSystem.Single, vector);
            void CaptureAxis(string getter, VariableDefinition stick, FieldReference component)
            {
                var reads = gather.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt &&
                    i.Operand is MethodReference m && m.DeclaringType.FullName == "InControl.InputDevice" &&
                    m.Name == getter).ToArray();
                var source = reads.First();
                il.InsertBefore(start, il.Create(OpCodes.Ldloca, stick));
                il.InsertBefore(start, il.Create(OpCodes.Call, (MethodReference)source.Previous.Operand));
                il.InsertBefore(start, il.Create(OpCodes.Callvirt, (MethodReference)source.Operand));
                il.InsertBefore(start, il.Create(OpCodes.Call, (MethodReference)source.Next.Operand));
                il.InsertBefore(start, il.Create(OpCodes.Stfld, component));

                // Replace just ActiveDevice -> stick axis -> float at the original use sites.
                foreach (var axis in reads) {
                    axis.Previous.OpCode = OpCodes.Ldloca;
                    axis.Previous.Operand = stick;
                    axis.OpCode = OpCodes.Ldfld;
                    axis.Operand = component;
                    il.Remove(axis.Next);
                }
            }
            CaptureAxis("get_LeftStickX", move, x);
            CaptureAxis("get_LeftStickY", move, y);
            CaptureAxis("get_RightStickX", turn, x);
            CaptureAxis("get_RightStickY", turn, y);

            var vrEnabled = game.GetType("Options").Fields.Single(f => f.Name == "isVRSupportEnabled");
            var time = new TypeReference("UnityEngine", "Time", game, vector.Scope);
            var frame = new MethodReference("get_frameCount", game.TypeSystem.Int32, time);
            il.InsertBefore(start, il.Create(OpCodes.Ldsfld, vrEnabled));
            il.InsertBefore(start, il.Create(OpCodes.Brfalse, start));
            il.InsertBefore(start, il.Create(OpCodes.Call, frame));
            il.InsertBefore(start, il.Create(OpCodes.Call, inputContext));
            il.InsertBefore(start, il.Create(OpCodes.Ldloca, move));
            il.InsertBefore(start, il.Create(OpCodes.Ldloca, turn));
            il.InsertBefore(start, il.Create(OpCodes.Call, merge));

            // The merge returns true only when XR supplies analog gameplay movement.
            il.InsertBefore(start, il.Create(OpCodes.Brfalse, start));
            il.InsertBefore(start, il.Create(OpCodes.Ldloc, move));
            il.InsertBefore(start, il.Create(OpCodes.Stloc, analogMove));
            il.InsertBefore(start, il.Create(OpCodes.Ldloca, move));
            il.InsertBefore(start, il.Create(OpCodes.Initobj, vector));

            // The final gameplay writes already include the original keyboard contribution.
            void AddAnalogMovement(string field, FieldReference component)
            {
                var write = gather.Body.Instructions.Last(i => i.OpCode == OpCodes.Stfld &&
                    i.Operand is FieldReference f && f.DeclaringType.FullName == input.FullName && f.Name == field);
                il.InsertBefore(write, il.Create(OpCodes.Ldloca, analogMove));
                il.InsertBefore(write, il.Create(OpCodes.Ldfld, component));
                il.InsertBefore(write, il.Create(OpCodes.Add));
            }
            AddAnalogMovement("horizontal", x);
            AddAnalogMovement("vertical", y);
        }
    }
}
