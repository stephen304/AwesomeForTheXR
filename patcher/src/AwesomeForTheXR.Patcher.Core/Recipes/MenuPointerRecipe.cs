using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomeForTheXR.Patcher.Core
{
    // Embed the pointer in Assembly-CSharp, using only retail Unity/Mono assembly references.
    internal sealed class MenuPointerRecipe
    {
        private readonly ModuleDefinition game;
        private readonly TypeDefinition source, pointer;
        private readonly ModuleReference plugin;
        private readonly Dictionary<MethodDefinition, MethodDefinition> methods = new Dictionary<MethodDefinition, MethodDefinition>();
        private readonly Dictionary<FieldDefinition, FieldDefinition> fields = new Dictionary<FieldDefinition, FieldDefinition>();
        private readonly IMetadataScope unity;

        internal static void Apply(ModuleDefinition game, ModuleReference plugin)
        {
            using (var data = typeof(MenuPointerRecipe).Assembly.GetManifestResourceStream("MenuPointer.template.dll"))
            using (var template = ModuleDefinition.ReadModule(data))
            {
                var recipe = new MenuPointerRecipe(game, plugin, template.GetType("MenuPointer"));
                var rig = game.GetType("OVRCameraRig");
                var setPanel = new MethodDefinition("AFTXR_SetPointerPanel", MethodAttributes.Assembly | MethodAttributes.HideBySig, game.TypeSystem.Void);
                setPanel.Parameters.Add(new ParameterDefinition(recipe.Bind(template.GetType("UnityEngine.MeshFilter"))));
                rig.Methods.Add(setPanel);
                recipe.CopyTemplate();
                recipe.ConnectPanel(rig, setPanel);
                recipe.ConnectGame();
            }
        }

        private MenuPointerRecipe(ModuleDefinition game, ModuleReference plugin, TypeDefinition source)
        {
            this.game = game;
            this.plugin = plugin;
            this.source = source;
            unity = game.AssemblyReferences.Single(r => r.Name == "UnityEngine");
            pointer = new TypeDefinition("", "AFTXRMenuPointer", source.Attributes, game.TypeSystem.Object);
            game.Types.Add(pointer);
        }

        private TypeReference Bind(TypeReference t)
        {
            if (t is ArrayType array) return new ArrayType(Bind(array.ElementType));
            if (t is ByReferenceType byRef) return new ByReferenceType(Bind(byRef.ElementType));
            if (t == source) return pointer;
            if (t.Namespace == "") return game.GetType(t.Name) ?? throw new InvalidDataException(t.FullName);
            if (t.Namespace == "UnityEngine") return new TypeReference(t.Namespace,t.Name,game,unity,t.IsValueType);
            if (t.Namespace != "System") throw new InvalidDataException("Unexpected pointer type " + t.FullName);
            switch (t.MetadataType)
            {
                case MetadataType.Void: return game.TypeSystem.Void;
                case MetadataType.Single: return game.TypeSystem.Single;
                case MetadataType.Int32: return game.TypeSystem.Int32;
                case MetadataType.UInt32: return game.TypeSystem.UInt32;
                case MetadataType.Boolean: return game.TypeSystem.Boolean;
                case MetadataType.String: return game.TypeSystem.String;
                case MetadataType.Object: return game.TypeSystem.Object;
            }
            return new TypeReference(t.Namespace,t.Name,game,game.TypeSystem.CoreLibrary,t.IsValueType);
        }

        private MethodReference Bind(MethodReference m)
        {
            if (m.DeclaringType == source) return methods[(MethodDefinition)m];
            if (Bind(m.DeclaringType) is TypeDefinition retail)
            {
                var target = retail.Methods.Single(x => x.FullName == m.FullName);
                if (target.IsPrivate) target.IsAssembly = true;
                return target;
            }
            var reference = new MethodReference(m.Name,Bind(m.ReturnType),Bind(m.DeclaringType)) { HasThis = m.HasThis };
            foreach (var p in m.Parameters) reference.Parameters.Add(new ParameterDefinition(Bind(p.ParameterType)));
            return reference;
        }

        private FieldReference Bind(FieldReference f)
        {
            if (f.DeclaringType == source) return fields[(FieldDefinition)f];
            if (Bind(f.DeclaringType) is TypeDefinition retail)
            {
                var target = retail.Fields.Single(x => x.FullName == f.FullName);
                if (target.IsPrivate) target.IsAssembly = true;
                return target;
            }
            return new FieldReference(f.Name,Bind(f.FieldType),Bind(f.DeclaringType));
        }

        private void CopyTemplate()
        {
            foreach (var f in source.Fields)
            {
                var copy = new FieldDefinition(f.Name,f.Attributes,Bind(f.FieldType));
                fields.Add(f,copy);
                pointer.Fields.Add(copy);
            }
            foreach (var m in source.Methods)
            {
                var copy = new MethodDefinition(m.Name,m.Attributes,Bind(m.ReturnType)) { ImplAttributes = m.ImplAttributes };
                foreach (var p in m.Parameters) copy.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Bind(p.ParameterType)));
                if (m.IsPInvokeImpl) copy.PInvokeInfo = new PInvokeInfo(m.PInvokeInfo.Attributes,m.PInvokeInfo.EntryPoint,plugin);
                methods.Add(m,copy);
                pointer.Methods.Add(copy);
            }
            foreach (var m in source.Methods.Where(m => m.HasBody))
            {
                var copy = methods[m];
                var body = copy.Body;
                body.InitLocals = m.Body.InitLocals;
                foreach (var local in m.Body.Variables) body.Variables.Add(new VariableDefinition(Bind(local.VariableType)));
                var instructions = m.Body.Instructions.ToDictionary(i => i, i => Instruction.Create(OpCodes.Nop));
                foreach (var i in m.Body.Instructions)
                {
                    var dest = instructions[i];
                    dest.OpCode = i.OpCode;
                    var operand = i.Operand;
                    if (operand is MethodReference method) operand = Bind(method);
                    else if (operand is FieldReference field) operand = Bind(field);
                    else if (operand is TypeReference type) operand = Bind(type);
                    else if (operand is Instruction branch) operand = instructions[branch];
                    else if (operand is Instruction[] table) operand = table.Select(entry => instructions[entry]).ToArray();
                    else if (operand is VariableDefinition local) operand = body.Variables[local.Index];
                    else if (operand is ParameterDefinition p) operand = copy.Parameters[p.Index];
                    dest.Operand = operand;
                    body.Instructions.Add(dest);
                }
                if (m.Body.HasExceptionHandlers) throw new InvalidDataException("Unsupported pointer exception region.");
            }
        }

        private MethodDefinition Method(string name) => pointer.Methods.Single(m => m.Name == name);
        private MethodDefinition Retail(string type, string name) => game.GetType(type).Methods.Single(m => m.Name == name);

        private static void Prefix(MethodDefinition m, params Instruction[] code)
        {
            var first = m.Body.Instructions[0];
            var il = m.Body.GetILProcessor();
            foreach (var instruction in code) il.InsertBefore(first,instruction);
        }

        private void ConnectPanel(TypeDefinition rig, MethodDefinition setPanel)
        {
            var surface = new FieldDefinition("aftxrPointerPanel", FieldAttributes.Private, setPanel.Parameters[0].ParameterType);
            rig.Fields.Add(surface);
            var il = setPanel.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, surface);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, Method("SetRig"));
            il.Emit(OpCodes.Ret);

            var enable = new MethodDefinition("OnEnable", MethodAttributes.Private | MethodAttributes.HideBySig, game.TypeSystem.Void);
            rig.Methods.Add(enable);
            il = enable.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, surface);
            il.Emit(OpCodes.Call, Method("SetRig"));
            il.Emit(OpCodes.Ret);
            Prefix(Retail("OVRCameraRig","OnDisable"), Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Call, Method("ReleaseRig")));

            // Start runs after all scene Awakes, including the VR manager and the legacy rig switcher.
            var start = new MethodDefinition("Start", MethodAttributes.Private | MethodAttributes.HideBySig, game.TypeSystem.Void);
            game.GetType("ScaleOculusPlaneByAspect").Methods.Add(start);
            il = start.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Method("RegisterPanel"));
            il.Emit(OpCodes.Ret);
        }

        private void ConnectGame()
        {
            var enabled = game.GetType("Options").Fields.Single(f => f.Name == "isVRSupportEnabled");
            var update = Retail("Cursor","Update");
            Prefix(update,Instruction.Create(OpCodes.Ldsfld,enabled),Instruction.Create(OpCodes.Brfalse,update.Body.Instructions[0]),
                Instruction.Create(OpCodes.Call,Method("UpdateCursor")),Instruction.Create(OpCodes.Ret));
            var button = Retail("GUIController","GetHitButton");
            Prefix(button,Instruction.Create(OpCodes.Call,Method("CanHit")),Instruction.Create(OpCodes.Brtrue,button.Body.Instructions[0]),
                Instruction.Create(OpCodes.Ldnull),Instruction.Create(OpCodes.Ret));
            foreach (var t in game.GetTypes().Where(t => t != pointer))
            foreach (var m in t.Methods.Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
            {
                if (!(i.Operand is MethodReference api)) continue;
                string replacement = null;
                if (api.DeclaringType.FullName == "UnityEngine.Input")
                {
                    bool menu = (t.Name == "GUIController" && m.Name == "Update") ||
                        (t.Name == "LevelSelectCamera" && m.Name == "LateUpdate");
                    switch (api.Name)
                    {
                        case "GetMouseButtonDown": replacement = menu ? "MenuDown" : t.Name == "LoaderScene" ? "Splash" : "MouseDown"; break;
                        case "GetMouseButtonUp": replacement = menu ? "MenuUp" : "MouseUp"; break;
                        case "GetMouseButton": replacement = menu ? "MenuHeld" : "MouseHeld"; break;
                        case "get_mousePosition": replacement = "MousePosition"; break;
                        case "get_anyKeyDown": replacement = "AnyDown"; break;
                        case "get_anyKey": replacement = "AnyHeld"; break;
                        case "GetAxisRaw":
                            if (t.FullName == "InControl.UnityMouseAxisSource" && m.Name == "GetValue")
                                replacement = "RawMouseAxis";
                            break;
                        case "GetAxis":
                            if (i.Previous?.Operand is string axis && (axis.Equals("mouse x",StringComparison.OrdinalIgnoreCase) ||
                                axis.Equals("mouse y",StringComparison.OrdinalIgnoreCase))) replacement = "MouseAxis";
                            break;
                    }
                }
                if (m == button && api.Name == "ScreenPointToRay") replacement = "ButtonRay";
                if (t.Name == "Cursor" && (m.Name == "Show" || m.Name == "Hide") && api.Name == "set_enabled")
                    replacement = "Visibility";
                if (replacement != null) { i.OpCode = OpCodes.Call; i.Operand = Method(replacement); }
            }
        }
    }
}
