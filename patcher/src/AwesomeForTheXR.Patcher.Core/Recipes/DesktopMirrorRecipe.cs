using System;
using System.Linq;
using BindingFlags = System.Reflection.BindingFlags;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomeForTheXR.Patcher.Core
{
    internal sealed partial class CameraRecipe
    {
        private void PatchDesktopMirror()
        {
            DisableMirrorToggle();

            var desktop = MirrorField("Camera", camera);
            var sync = MirrorMethod("Update", module.TypeSystem.Void);
            var il = sync.Body.GetILProcessor();
            sync.Body.Variables.Add(new VariableDefinition(UnityType("GameObject")));
            var left = Field(rig, "leftEyeCamera");
            var exists = Unity("Object", "op_Implicit", module.TypeSystem.Boolean, false, UnityType("Object"));
            var ready = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, desktop);
            il.Emit(OpCodes.Call, exists);
            il.Emit(OpCodes.Brtrue, ready);

            // A fresh camera defaults to the whole window, automatic aspect and a symmetric projection.
            // Never CopyFrom an XR eye: it also copies the transform and the custom projection.
            var tr = UnityType("Transform");
            var none = module.TypeSystem.Void;
            il.Emit(OpCodes.Ldstr, "AwesomeForTheXR Desktop Camera");
            il.Emit(OpCodes.Newobj, Unity("GameObject", ".ctor", none, true, module.TypeSystem.String));
            il.Emit(OpCodes.Stloc_0);
            SetMirrorObjectActive(il, false);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Callvirt, Unity("GameObject", "get_transform", tr, true));
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Method(rig, "get_centerEyeAnchor"));
            il.Emit(OpCodes.Callvirt, Unity("Transform", "set_parent", none, true, tr));
            foreach (var property in new[] { "localPosition", "localRotation", "localScale" })
            {
                var type = property == "localRotation" ? "Quaternion" : "Vector3";
                var value = property == "localPosition" ? "zero" : property == "localRotation" ? "identity" : "one";
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Call, Unity(type, "get_" + value, UnityType(type), false));
                il.Emit(OpCodes.Callvirt, Unity("Transform", "set_" + property, none, true, UnityType(type)));
            }
            il.Emit(OpCodes.Pop);

            var methods = Method(rig, "EnsureGameObjectIntegrity").Body.Instructions
                .Select(i => i.Operand).OfType<GenericInstanceMethod>().ToList();
            var add = methods.First(m => m.Name == "AddComponent");
            var get = methods.First(m => m.Name == "GetComponent");
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Callvirt, add); // AddComponent<Camera>
            il.Emit(OpCodes.Stfld, desktop);
            SetMirrorFloat(il, desktop, "fieldOfView", 75f);
            SetMirrorFloat(il, desktop, "depth", 50f);

            var copySettings = MakeMirrorEffectCopier();
            var followEnabled = MakeMirrorEnabledSync();
            var bloom = module.GetTypeReferences().Single(t => t.Name == "BloomAndFlares");
            var desktopBloom = MakeDesktopBloom(bloom);
            var effects = new[] { bloom, module.GetTypeReferences().Single(t => t.Name == "MotionBlur") };
            var sources = new FieldDefinition[effects.Length];
            var copies = new FieldDefinition[effects.Length];
            for (int i = 0; i < effects.Length; i++)
            {
                var type = effects[i];
                sources[i] = MirrorField(type.Name + "Source", type);
                copies[i] = MirrorField(type.Name, type);
                var getEffect = new GenericInstanceMethod(get.ElementMethod);
                var addEffect = new GenericInstanceMethod(add.ElementMethod);
                getEffect.GenericArguments.Add(type);
                addEffect.GenericArguments.Add(type == bloom ? desktopBloom : type);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, left);
                il.Emit(OpCodes.Callvirt, getEffect);
                il.Emit(OpCodes.Stfld, sources[i]);
                var skip = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, sources[i]);
                il.Emit(OpCodes.Brfalse, skip);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Callvirt, addEffect);
                il.Emit(OpCodes.Stfld, copies[i]);
                LoadMirrorPair(il, sources[i], copies[i]);
                il.Emit(OpCodes.Call, copySettings);
                LoadMirrorPair(il, sources[i], copies[i]);
                il.Emit(OpCodes.Call, followEnabled);
                il.Append(skip);
            }

            PatchMirrorBloomQuality(sources[0], copies[0], exists);

            // Match world rendering, while the camera keeps its own viewport, projection and output.
            foreach (var property in new[] { "clearFlags", "backgroundColor", "renderingPath", "hdr", "useOcclusionCulling" })
            {
                TypeReference type;
                switch (property)
                {
                    case "clearFlags": type = UnityType("CameraClearFlags"); break;
                    case "backgroundColor": type = UnityType("Color"); break;
                    case "renderingPath": type = UnityType("RenderingPath"); break;
                    default: type = module.TypeSystem.Boolean; break;
                }
                CopyMirrorProperty(il, left, desktop, property, type, false);
            }
            SetMirrorObjectActive(il, true);
            il.Append(ready);
            LoadMirrorPair(il, left, desktop);
            il.Emit(OpCodes.Call, followEnabled);
            // These can change when the level or graphics settings change.
            CopyMirrorProperty(il, left, desktop, "cullingMask", module.TypeSystem.Int32, true);
            CopyMirrorProperty(il, left, desktop, "nearClipPlane", module.TypeSystem.Single, true);
            CopyMirrorProperty(il, left, desktop, "farClipPlane", module.TypeSystem.Single, true);
            for (int i = 0; i < effects.Length; i++)
            {
                LoadMirrorPair(il, sources[i], copies[i]);
                il.Emit(OpCodes.Call, followEnabled);
            }
            il.Emit(OpCodes.Ret);

            // The rig owns this child across Start/LateUpdate; scene teardown destroys it with the rig.
            var anchors = Method(rig, "UpdateAnchors");
            var anchorIL = anchors.Body.GetILProcessor();
            var ret = anchors.Body.Instructions.Last();
            anchorIL.InsertBefore(ret, anchorIL.Create(OpCodes.Ldarg_0));
            anchorIL.InsertBefore(ret, anchorIL.Create(OpCodes.Call, sync));

            // Disabling just the rig component leaves its children active. Resume on its next update.
            var disable = new MethodDefinition("OnDisable", MethodAttributes.Private | MethodAttributes.HideBySig, none);
            rig.Methods.Add(disable);
            var stop = disable.Body.GetILProcessor();
            var end = stop.Create(OpCodes.Ret);
            stop.Emit(OpCodes.Ldarg_0); stop.Emit(OpCodes.Ldfld, desktop); stop.Emit(OpCodes.Call, exists);
            stop.Emit(OpCodes.Brfalse, end);
            stop.Emit(OpCodes.Ldarg_0); stop.Emit(OpCodes.Ldfld, desktop); stop.Emit(OpCodes.Ldc_I4_0);
            stop.Emit(OpCodes.Callvirt, Unity("Behaviour", "set_enabled", none, true, module.TypeSystem.Boolean));
            stop.Append(end);
        }

        private FieldDefinition MirrorField(string name, TypeReference type)
        {
            var field = new FieldDefinition("aftxrDesktop" + name, FieldAttributes.Private, type);
            rig.Fields.Add(field);
            return field;
        }

        private MethodDefinition MirrorMethod(string name, TypeReference result, bool isStatic = false)
        {
            var flags = MethodAttributes.Private | MethodAttributes.HideBySig;
            var method = new MethodDefinition("AFTXR_Desktop" + name, isStatic ? flags | MethodAttributes.Static : flags, result);
            method.Body.InitLocals = true;
            rig.Methods.Add(method);
            return method;
        }

        private void SetMirrorObjectActive(ILProcessor il, bool active)
        {
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(active ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, Unity("GameObject", "SetActive", module.TypeSystem.Void, true, module.TypeSystem.Boolean));
        }

        private void SetMirrorFloat(ILProcessor il, FieldDefinition desktop, string property, float value)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, desktop);
            il.Emit(OpCodes.Ldc_R4, value);
            il.Emit(OpCodes.Callvirt, Unity("Camera", "set_" + property, module.TypeSystem.Void, true, module.TypeSystem.Single));
        }

        private static void LoadMirrorPair(ILProcessor il, FieldDefinition source, FieldDefinition copy)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, source);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, copy);
        }

        private void CopyMirrorProperty(ILProcessor il, FieldDefinition source, FieldDefinition copy,
            string name, TypeReference type, bool onlyIfChanged)
        {
            var get = Unity("Camera", "get_" + name, type, true);
            var done = il.Create(OpCodes.Nop);
            if (onlyIfChanged)
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, source); il.Emit(OpCodes.Callvirt, get);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, copy); il.Emit(OpCodes.Callvirt, get);
                il.Emit(OpCodes.Beq, done);
            }
            LoadMirrorPair(il, copy, source);
            il.Emit(OpCodes.Callvirt, get);
            il.Emit(OpCodes.Callvirt, Unity("Camera", "set_" + name, module.TypeSystem.Void, true, type));
            if (onlyIfChanged) il.Append(done);
        }

        private MethodDefinition MakeMirrorEnabledSync()
        {
            var behaviour = UnityType("Behaviour");
            var method = MirrorMethod("FollowEnabled", module.TypeSystem.Void, true);
            method.Parameters.Add(new ParameterDefinition(behaviour));
            method.Parameters.Add(new ParameterDefinition(behaviour));
            var il = method.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            var enabled = Unity("Behaviour", "get_enabled", module.TypeSystem.Boolean, true);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, enabled);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, enabled);
            il.Emit(OpCodes.Beq, done);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, enabled);
            il.Emit(OpCodes.Callvirt, Unity("Behaviour", "set_enabled", module.TypeSystem.Void, true, module.TypeSystem.Boolean));
            il.Append(done);
            return method;
        }

        // Unity 4 keeps the stock bloom's private DontSave materials alive after a scene unload.
        // Only the desktop instance uses this subclass. The eye effects and their assets stay independent.
        private TypeDefinition MakeDesktopBloom(TypeReference stockBloom)
        {
            var none = module.TypeSystem.Void;
            var type = new TypeDefinition("", "AFTXRDesktopBloom",
                TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class, stockBloom);
            module.Types.Add(type);
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, none);
            type.Methods.Add(ctor);
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, new MethodReference(".ctor", none, stockBloom) { HasThis = true });
            il.Emit(OpCodes.Ret);

            var destroy = new MethodDefinition("OnDestroy", MethodAttributes.Private | MethodAttributes.HideBySig, none);
            type.Methods.Add(destroy);
            il = destroy.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, stockBloom);
            il.Emit(OpCodes.Call, TypeFromHandle());
            ForEachEffectField(destroy, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                (body, field, read) =>
                {
                    var material = new VariableDefinition(UnityType("Material"));
                    destroy.Body.Variables.Add(material);
                    var skip = body.Create(OpCodes.Nop);
                    body.Emit(OpCodes.Ldloc, field);
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Callvirt, read);
                    body.Emit(OpCodes.Isinst, material.VariableType);
                    body.Emit(OpCodes.Stloc, material);
                    body.Emit(OpCodes.Ldloc, material);
                    body.Emit(OpCodes.Call, Unity("Object", "op_Implicit", module.TypeSystem.Boolean, false, UnityType("Object")));
                    body.Emit(OpCodes.Brfalse, skip);
                    body.Emit(OpCodes.Ldloc, material);
                    body.Emit(OpCodes.Call, Unity("Object", "DestroyImmediate", none, false, UnityType("Object")));
                    body.Append(skip);
                });
            return type;
        }

        private MethodReference TypeFromHandle() => module.GetMemberReferences().OfType<MethodReference>()
            .First(m => m.DeclaringType.FullName == "System.Type" && m.Name == "GetTypeFromHandle");

        // The game's quality routine updates the eyes in place, including changes between two enabled presets.
        private void PatchMirrorBloomQuality(FieldDefinition source, FieldDefinition copy, MethodReference exists)
        {
            var quality = Method(module.GetType("PostEffectsQualityManager"), "DetermineEffects");
            var refresh = MirrorMethod("RefreshBloomQuality", module.TypeSystem.Void);
            refresh.IsAssembly = true;
            var il = refresh.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, copy);
            il.Emit(OpCodes.Call, exists);
            il.Emit(OpCodes.Brfalse, done); // Quality can be applied before the mirror is created.
            foreach (var name in new[] { "bloomBlurIterations", "lensflares", "lensflareIntensity" })
            {
                var field = quality.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>()
                    .First(f => f.DeclaringType.FullName == source.FieldType.FullName && f.Name == name);
                LoadMirrorPair(il, copy, source);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Stfld, field);
            }
            il.Append(done);

            // Only the Oculus-rig branch assigns this local. Use the actual left eye's owning rig.
            var leftEye = (VariableDefinition)At(quality, 0xd2).Operand;
            il = quality.Body.GetILProcessor();
            var tail = quality.Body.Instructions.Last();
            tail.OpCode = OpCodes.Ldloc;
            tail.Operand = leftEye; // Preserve branches to the stock final return.
            done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Call, exists);
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldloc, leftEye);
            il.Emit(OpCodes.Ldtoken, rig);
            var typeFromHandle = TypeFromHandle();
            il.Emit(OpCodes.Call, typeFromHandle);
            il.Emit(OpCodes.Callvirt, Unity("Component", "GetComponentInParent", UnityType("Component"), true, typeFromHandle.ReturnType));
            var owner = new VariableDefinition(rig);
            quality.Body.Variables.Add(owner);
            il.Emit(OpCodes.Castclass, rig);
            il.Emit(OpCodes.Stloc, owner);
            il.Emit(OpCodes.Ldloc, owner);
            il.Emit(OpCodes.Call, exists);
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldloc, owner);
            il.Emit(OpCodes.Call, refresh);
            il.Append(done);
        }

        // Copy public configuration, including inherited shader references. Each component owns its private state.
        private MethodDefinition MakeMirrorEffectCopier()
        {
            var obj = module.TypeSystem.Object;
            var method = MirrorMethod("CopyEffectSettings", module.TypeSystem.Void, true);
            method.Parameters.Add(new ParameterDefinition(obj));
            method.Parameters.Add(new ParameterDefinition(obj));
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, new MethodReference("GetType", TypeFromHandle().ReturnType, obj) { HasThis = true });
            ForEachEffectField(method, BindingFlags.Instance | BindingFlags.Public, (body, field, read) =>
            {
                var write = new MethodReference("SetValue", module.TypeSystem.Void, field.VariableType) { HasThis = true };
                write.Parameters.Add(new ParameterDefinition(obj));
                write.Parameters.Add(new ParameterDefinition(obj));
                body.Emit(OpCodes.Ldloc, field);
                body.Emit(OpCodes.Ldarg_1);
                body.Emit(OpCodes.Ldloc, field);
                body.Emit(OpCodes.Ldarg_0);
                body.Emit(OpCodes.Callvirt, read);
                body.Emit(OpCodes.Callvirt, write);
            });
            return method;
        }

        // The caller leaves the System.Type to enumerate on the stack.
        private void ForEachEffectField(MethodDefinition method, BindingFlags visibility,
            Action<ILProcessor, VariableDefinition, MethodReference> visit)
        {
            var core = module.TypeSystem.CoreLibrary;
            var fieldType = new TypeReference("System.Reflection", "FieldInfo", module, core);
            var flags = new TypeReference("System.Reflection", "BindingFlags", module, core, true);
            var fields = new VariableDefinition(new ArrayType(fieldType));
            var index = new VariableDefinition(module.TypeSystem.Int32);
            var field = new VariableDefinition(fieldType);
            method.Body.InitLocals = true;
            method.Body.Variables.Add(fields);
            method.Body.Variables.Add(index);
            method.Body.Variables.Add(field);
            var getFields = new MethodReference("GetFields", fields.VariableType, TypeFromHandle().ReturnType) { HasThis = true };
            getFields.Parameters.Add(new ParameterDefinition(flags));
            var read = new MethodReference("GetValue", module.TypeSystem.Object, fieldType) { HasThis = true };
            read.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, (int)visibility);
            il.Emit(OpCodes.Callvirt, getFields);
            il.Emit(OpCodes.Stloc, fields);
            var loop = il.Create(OpCodes.Ldloc, fields);
            var test = il.Create(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Br, test);
            il.Append(loop);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Stloc, field);
            visit(il, field, read);
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, index);
            il.Append(test);
            il.Emit(OpCodes.Ldloc, fields);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Blt, loop);
            il.Emit(OpCodes.Ret);
        }

        private void DisableMirrorToggle()
        {
            var getter = Rewrite(Method(display, "get_mirrorMode"));
            getter.Emit(OpCodes.Ldc_I4_1);
            getter.Emit(OpCodes.Ret);
            Rewrite(Method(display, "set_mirrorMode")).Emit(OpCodes.Ret);

            var update = Method(module.GetType("OVRMainMenu"), "Update");
            var menu = update.Body.GetILProcessor();
            var hotkey = At(update, 0x9d);
            var resume = At(update, 0xc0);
            At(update, 0x8b).Operand = resume;
            while (hotkey != resume)
            {
                var next = hotkey.Next;
                menu.Remove(hotkey);
                hotkey = next;
            }
        }
    }
}

