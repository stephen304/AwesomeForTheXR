using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomePatcher.Core
{
    // All method edits target the original assembly fingerprint checked by GameAssemblyRecipe.
    internal sealed partial class CameraRecipe
    {
        private readonly ModuleDefinition module;
        private readonly ModuleReference plugin;
        private readonly TypeDefinition manager, rig, display, eyeObserver;
        private readonly TypeReference camera;
        private readonly MethodReference frameCount;
        private readonly MethodDefinition abort;

        internal static void Apply(ModuleDefinition module, ModuleReference plugin, MethodDefinition abort)
        {
            var recipe = new CameraRecipe(module, plugin, abort);
            recipe.PatchDisplay();
            recipe.PatchManager();
            recipe.PatchRig();
            recipe.PatchDesktopMirror();
            recipe.MoveHeadAttachments();
            recipe.PatchGameplayHead();
        }

        private CameraRecipe(ModuleDefinition module, ModuleReference plugin, MethodDefinition abort)
        {
            this.module = module;
            this.plugin = plugin;
            this.abort = abort;
            manager = module.GetType("OVRManager");
            rig = module.GetType("OVRCameraRig");
            display = module.GetType("OVRDisplay");
            camera = Field(rig, "leftEyeCamera").FieldType;
            eyeObserver = new TypeDefinition("", "AFTXREyeCamera",
                TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.Sealed, rig.BaseType);
            module.Types.Add(eyeObserver);
            frameCount = Unity("Time", "get_frameCount", module.TypeSystem.Int32, false);
        }

        private static MethodDefinition Method(TypeDefinition type, string name) =>
            type.Methods.Single(m => m.Name == name);
        private static FieldDefinition Field(TypeDefinition type, string name) =>
            type.Fields.Single(f => f.Name == name);
        private static ILProcessor Rewrite(MethodDefinition method)
        {
            method.Body = new MethodBody(method) { InitLocals = true };
            return method.Body.GetILProcessor();
        }
        private static Instruction At(MethodDefinition method, int offset) =>
            method.Body.Instructions.Single(i => i.Offset == offset);
        private TypeReference UnityType(string name) =>
            module.GetTypeReferences().First(t => t.Namespace == "UnityEngine" && t.Name == name);

        private MethodReference Unity(string type, string name, TypeReference result, bool instance,
            params TypeReference[] args)
        {
            var method = new MethodReference(name, result, UnityType(type)) { HasThis = instance };
            foreach (var arg in args) method.Parameters.Add(new ParameterDefinition(arg));
            return method;
        }

        private MethodDefinition Native(string name, TypeReference result, params TypeReference[] args)
        {
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static |
                MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, result)
            {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle, name, plugin)
            };
            foreach (var arg in args) method.Parameters.Add(new ParameterDefinition(arg));
            eyeObserver.Methods.Add(method);
            return method;
        }

        private MethodDefinition Managed(string name, bool isStatic, TypeReference result, params TypeReference[] args)
        {
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.HideBySig |
                (isStatic ? MethodAttributes.Static : 0), result);
            foreach (var arg in args) method.Parameters.Add(new ParameterDefinition(arg));
            eyeObserver.Methods.Add(method);
            return method;
        }

        private void PatchDisplay()
        {
            var none = module.TypeSystem.Void;
            var id = module.TypeSystem.Int32;
            var scalar = module.TypeSystem.Single;
            var descType = display.NestedTypes.Single(t => t.Name == "EyeRenderDesc");
            var describe = Native("AFTXR_EyeDescription", none, id, new ByReferenceType(descType));
            var il = Rewrite(Method(display, "ConfigureEyeDesc"));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, Field(display,"eyeDescs"));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldelema, descType);
            il.Emit(OpCodes.Call, describe);
            il.Emit(OpCodes.Ret);

            var pose = module.GetType("OVRPose");
            var getPose = Native("AFTXR_EyePose", none, id, new ByReferenceType(pose));
            var eyePose = Method(display,"GetEyePose");
            il = Rewrite(eyePose);
            eyePose.Body.Variables.Add(new VariableDefinition(pose));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca_S, eyePose.Body.Variables[0]);
            il.Emit(OpCodes.Call, getPose);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ret);

            // Both managed APIs use the one prediction made by BeginFrame. OVREye.Center is -1.
            il = Rewrite(Method(display, "GetHeadPose"));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Call, eyePose);
            il.Emit(OpCodes.Ret);

            var matrix = UnityType("Matrix4x4");
            var getProjection = Native("AFTXR_EyeProjection",none,id,scalar,scalar,new ByReferenceType(matrix));
            var projection = Method(display,"GetProjection");
            il = Rewrite(projection);
            projection.Body.Variables.Add(new VariableDefinition(matrix));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldloca_S, projection.Body.Variables[0]);
            il.Emit(OpCodes.Call, getProjection);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ret);

            // Keep the original RecenteredPose event and remove its LibOVR call.
            var reset = Method(display,"RecenterPose");
            At(reset,0).Operand = Native("AFTXR_Recenter",none);
            reset.Body.GetILProcessor().Remove(At(reset,5));
            Rewrite(Method(display,"ForceSymmetricProj")).Emit(OpCodes.Ret);

            // Unity's original WaitForEndOfFrame coroutine reaches this after camera image effects.
            il = Rewrite(Method(display,"EndFrame"));
            il.Emit(OpCodes.Ldc_I4, 0x41585234);
            il.Emit(OpCodes.Call, Unity("GL","IssuePluginEvent",none,false,id));
            il.Emit(OpCodes.Ret);
        }

        private void PatchManager()
        {
            var none = module.TypeSystem.Void;
            var flag = module.TypeSystem.Boolean;
            var awake = Method(manager,"Awake");
            var il = awake.Body.GetILProcessor();
            // Preserve duplicate-manager rejection, constructor calls and the optional recenter.
            // Delete the SDK version, platform and legacy graphics/direct-mode checks.
            var init = At(awake,0x24);
            var displayInit = At(awake,0xf9);
            for (var i=init; i!=displayInit;) { var next=i.Next; il.Remove(i); i=next; }
            il.InsertBefore(displayInit,il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(displayInit,il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(displayInit,il.Create(OpCodes.Call,Method(manager,"set_isSupportedPlatform")));
            var tail = At(awake,0x136);
            while (tail.Next!=null) il.Remove(tail.Next);
            tail.OpCode=OpCodes.Ldc_I4_1; tail.Operand=null;
            il.Emit(OpCodes.Call,Unity("Application","set_runInBackground",none,false,flag));
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call,Unity("QualitySettings","set_vSyncCount",none,false,module.TypeSystem.Int32));
            il.Emit(OpCodes.Ret);

            var start=Method(manager,"Start");
            il=start.Body.GetILProcessor();
            // The desktop camera now clears the window. Retain only the original end-of-frame coroutine.
            var coroutine = start.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "CallbackCoroutine");
            var resume = coroutine.Previous.Previous;
            while (start.Body.Instructions[0] != resume) il.Remove(start.Body.Instructions[0]);

            Rewrite(Method(manager,"OnDisable")).Emit(OpCodes.Ret);
            il=Rewrite(Method(manager,"OnApplicationQuit"));
            il.Emit(OpCodes.Call,Native("AFTXR_ReleaseLaunch",none));
            il.Emit(OpCodes.Ret);
            // The game reads this check during the splash. SteamVR owns headset notices.
            il=Rewrite(Method(manager,"get_isHSWDisplayed"));
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            Rewrite(Method(manager,"Update")).Emit(OpCodes.Ret);
            var begin=Method(display,"BeginFrame");
            il=Rewrite(begin);
            il.Emit(OpCodes.Call,frameCount);
            il.Emit(OpCodes.Call,Native("AFTXR_BeginCameraFrame",module.TypeSystem.Int32,module.TypeSystem.Int32));
            var quit=il.Create(OpCodes.Call,Unity("Application","Quit",none,false));
            var done=il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse,quit);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Bgt,done);
            il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call,abort); il.Emit(OpCodes.Ret);
            // The duplicate return value still on the stack must be consumed before Quit.
            var pop=il.Create(OpCodes.Pop);
            il.Append(pop); il.Append(quit); il.Append(done);
            begin.Body.Instructions.First(i=>i.OpCode==OpCodes.Brfalse).Operand=pop;

            var mirror=module.GetType("ToggleMirrorMode");
            foreach(var name in new[]{"Start","Update","SetExistingMirrorMode"})
                Rewrite(Method(mirror,name)).Emit(OpCodes.Ret);
        }

        private void PatchRig()
        {
            var none=module.TypeSystem.Void;
            var id=module.TypeSystem.Int32;
            var eyeField=new FieldDefinition("eye",FieldAttributes.Private,id);
            var cameraField=new FieldDefinition("eyeCamera",FieldAttributes.Private,camera);
            eyeObserver.Fields.Add(eyeField); eyeObserver.Fields.Add(cameraField);
            var ctor=new MethodDefinition(".ctor",MethodAttributes.Public|MethodAttributes.HideBySig|
                MethodAttributes.SpecialName|MethodAttributes.RTSpecialName,none);
            eyeObserver.Methods.Add(ctor);
            var il=ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call,new MethodReference(".ctor",none,rig.BaseType){HasThis=true});
            il.Emit(OpCodes.Ret);

            var attach=Managed("Attach",true,none,camera,id);
            il=attach.Body.GetILProcessor();
            var add=(GenericInstanceMethod)Method(rig,"EnsureGameObjectIntegrity").Body.Instructions
                .Select(i=>i.Operand).OfType<GenericInstanceMethod>().First(m=>m.Name=="AddComponent");
            var addObserver=new GenericInstanceMethod(add.ElementMethod);
            addObserver.GenericArguments.Add(eyeObserver);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt,Unity("Component","get_gameObject",UnityType("GameObject"),true));
            il.Emit(OpCodes.Callvirt,addObserver);
            il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stfld,cameraField);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld,eyeField);
            il.Emit(OpCodes.Ret);
            var configure=Method(rig,"ConfigureCamera");
            var ret=configure.Body.Instructions.Last();
            il=configure.Body.GetILProcessor();
            il.InsertBefore(ret,il.Create(OpCodes.Dup));
            il.InsertBefore(ret,il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(ret,il.Create(OpCodes.Call,attach));

            // Take the output from this exact camera, after it rendered this Unity frame.
            var report=Native("AFTXR_RenderedEye",none,id,module.TypeSystem.IntPtr,id);
            var post=Managed("OnPostRender",false,none);
            il=post.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld,eyeField);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld,cameraField);
            il.Emit(OpCodes.Callvirt,Unity("Camera","get_targetTexture",UnityType("RenderTexture"),true));
            il.Emit(OpCodes.Callvirt,Unity("Texture","GetNativeTexturePtr",module.TypeSystem.IntPtr,true));
            il.Emit(OpCodes.Call,frameCount); il.Emit(OpCodes.Call,report); il.Emit(OpCodes.Ret);

            var apply=Managed("ApplyProjection",true,none,camera,id);
            il=apply.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call,Method(manager,"get_display")); il.Emit(OpCodes.Ldarg_1);
            foreach (var clip in new[]{"get_nearClipPlane","get_farClipPlane"}) {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt,Unity("Camera",clip,module.TypeSystem.Single,true));
            }
            il.Emit(OpCodes.Callvirt,Method(display,"GetProjection"));
            il.Emit(OpCodes.Callvirt,Unity("Camera","set_projectionMatrix",none,true,UnityType("Matrix4x4")));
            il.Emit(OpCodes.Ret);

            var pose = module.GetType("OVRPose");
            var applyPose = Managed("ApplyPose", true, none, UnityType("Transform"), pose);
            il = applyPose.Body.GetILProcessor();
            foreach (var field in new[] { "position", "orientation" }) {
                var value = Field(pose, field);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarga_S, applyPose.Parameters[1]);
                il.Emit(OpCodes.Ldfld, value);
                var setter = field == "position" ? "set_localPosition" : "set_localRotation";
                il.Emit(OpCodes.Callvirt, Unity("Transform", setter, none, true, value.FieldType));
            }
            il.Emit(OpCodes.Ret);

            var headFrame = new FieldDefinition("aftxrHeadPoseFrame", FieldAttributes.Private, id);
            rig.Fields.Add(headFrame);
            var awake = Method(rig, "Awake").Body.GetILProcessor();
            var first = awake.Body.Instructions[0];
            awake.InsertBefore(first, awake.Create(OpCodes.Ldarg_0));
            awake.InsertBefore(first, awake.Create(OpCodes.Ldc_I4_M1));
            awake.InsertBefore(first, awake.Create(OpCodes.Stfld, headFrame));

            // Menu input can need the head-locked surface before the normal eye/mirror update.
            var prepare = new MethodDefinition("AFTXR_PrepareHeadPose", MethodAttributes.Assembly | MethodAttributes.HideBySig, none);
            rig.Methods.Add(prepare);
            il = prepare.Body.GetILProcessor();
            var refresh = il.Create(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, headFrame);
            il.Emit(OpCodes.Call, frameCount);
            il.Emit(OpCodes.Bne_Un, refresh);
            il.Emit(OpCodes.Ret);
            il.Append(refresh);
            il.Emit(OpCodes.Call, frameCount);
            il.Emit(OpCodes.Stfld, headFrame);
            var getDisplay = Method(manager, "get_display");
            il.Emit(OpCodes.Call, getDisplay);
            il.Emit(OpCodes.Callvirt, Method(display, "BeginFrame"));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Method(rig, "get_centerEyeAnchor"));
            il.Emit(OpCodes.Call, getDisplay);
            il.Emit(OpCodes.Ldc_R8, 0d);
            il.Emit(OpCodes.Callvirt, Method(display, "GetHeadPose"));
            il.Emit(OpCodes.Call, applyPose);
            il.Emit(OpCodes.Ret);

            il = Rewrite(Method(rig, "UpdateAnchors"));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, prepare);
            foreach (var side in new[] { "left", "right" }) {
                var eye = side == "left" ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, Field(rig, side + "EyeCamera"));
                il.Emit(eye);
                il.Emit(OpCodes.Call, apply);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, Method(rig, "get_" + side + "EyeAnchor"));
                il.Emit(OpCodes.Call, getDisplay);
                il.Emit(eye);
                il.Emit(OpCodes.Callvirt, Method(display, "GetEyePose"));
                il.Emit(OpCodes.Call, applyPose);
            }
            il.Emit(OpCodes.Ret);
        }

        private void MoveHeadAttachments()
        {
            // The stock prefabs put shared menu geometry and the jump HUD under the left camera.
            // Awake has just aligned all three siblings at the rig origin. Reparent once here,
            // before UpdateAnchors applies any eye offset or cant. Keep the HUD subtree intact.
            var transform = UnityType("Transform");
            var move = Managed("MoveHeadAttachment", true, module.TypeSystem.Void, rig, module.TypeSystem.String);
            move.Body.Variables.Add(new VariableDefinition(transform));
            var il = move.Body.GetILProcessor();
            var done = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Method(rig, "get_leftEyeAnchor"));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, Unity("Transform", "Find", transform, true, module.TypeSystem.String));
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Brfalse, done);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Method(rig, "get_centerEyeAnchor"));
            il.Emit(OpCodes.Callvirt, Unity("Transform", "set_parent", module.TypeSystem.Void, true, transform));
            il.Append(done);

            var awake = Method(rig, "Awake");
            var end = awake.Body.Instructions.Last();
            il = awake.Body.GetILProcessor();
            foreach (var child in new[] { "HUDFadePlane", "Spotlight", "ScanlinesOVRGuiObjectMain", "LeftEyeJunk" }) {
                il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(end, il.Create(OpCodes.Ldstr, child));
                il.InsertBefore(end, il.Create(OpCodes.Call, move));
            }
        }

        private void PatchGameplayHead()
        {
            var transform = UnityType("Transform");
            var vector = UnityType("Vector3");
            var ray = UnityType("Ray");
            var getPosition = Unity("Transform", "get_position", vector, true);
            var getForward = Unity("Transform", "get_forward", vector, true);
            var player = module.GetType("Player");
            var proximity = module.GetType("PlayerProximity");
            var wind = module.GetType("Wind");

            // Resolve the center on the same rig that owns the game's rendering camera.
            // Each component caches it during its existing VR-only initialization.
            var findHead = Managed("FindHeadAnchor", true, transform);
            var il = findHead.Body.GetILProcessor();
            il.Emit(OpCodes.Call, Method(module.GetType("OculusSteamVRSwitcher"), "GetVRCameraLeftGameObject"));
            il.Emit(OpCodes.Callvirt, Unity("GameObject", "get_transform", transform, true));
            il.Emit(OpCodes.Callvirt, Unity("Transform", "get_parent", transform, true));
            var getComponent = Method(rig, "EnsureGameObjectIntegrity").Body.Instructions
                .Select(i => i.Operand).OfType<GenericInstanceMethod>().First(m => m.Name == "GetComponent");
            var getRig = new GenericInstanceMethod(getComponent.ElementMethod);
            getRig.GenericArguments.Add(rig);
            il.Emit(OpCodes.Callvirt, getRig);
            il.Emit(OpCodes.Callvirt, Method(rig, "get_centerEyeAnchor"));
            il.Emit(OpCodes.Ret);

            var headRay = Managed("HeadRay", true, ray, transform);
            il = headRay.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, getPosition);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, getForward);
            il.Emit(OpCodes.Newobj, Unity("Ray", ".ctor", module.TypeSystem.Void, true, vector, vector));
            il.Emit(OpCodes.Ret);

            foreach (var owner in new[] { player, proximity }) {
                // These two private fields were used exclusively for head direction and aiming.
                var head = Field(owner, "oculusCameraLeft");
                head.Name = "aftxrHeadAnchor";
                owner.Fields.Remove(Field(owner, "oculusCameraRight"));
                var start = Method(owner, "Start");
                var store = At(start, 0x15);
                var read = At(start, 0x0b);
                var resume = At(start, 0x2a);
                il = start.Body.GetILProcessor();
                read.OpCode = OpCodes.Call;
                read.Operand = findHead;
                il.Remove(read.Next);
                while (store.Next != resume) il.Remove(store.Next);

                // Keep the VR branch target and the original desktop ray and hit processing.
                // Player.FixedUpdate already reads the renamed field for airborne steering.
                bool graffiti = owner == player;
                var aim = Method(owner, graffiti ? "TagGraffiti" : "CastMouseRay");
                var first = At(aim, graffiti ? 0x41 : 0x0a);
                resume = At(aim, graffiti ? 0x95 : 0x63);
                il = aim.Body.GetILProcessor();
                while (first.Next != resume) il.Remove(first.Next);
                il.InsertBefore(resume, il.Create(OpCodes.Ldfld, head));
                il.InsertBefore(resume, il.Create(OpCodes.Call, headRay));
                il.InsertBefore(resume, il.Create(graffiti ? OpCodes.Stloc_1 : OpCodes.Stloc_0));
            }

            var windHead = new FieldDefinition("aftxrHeadAnchor", FieldAttributes.Private, transform);
            wind.Fields.Add(windHead);
            var setup = Method(wind, "SetupWindBlurOculus");
            il = setup.Body.GetILProcessor();
            var entry = setup.Body.Instructions[0];
            il.InsertBefore(entry, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(entry, il.Create(OpCodes.Call, findHead));
            il.InsertBefore(entry, il.Create(OpCodes.Stfld, windHead));
            // The original emitter search now starts at the center, which owns the jump HUD group.
            var emitterRoot = At(setup, 0x01);
            emitterRoot.OpCode = OpCodes.Ldarg_0;
            emitterRoot.Operand = null;
            var emitters = emitterRoot.Next;
            il.InsertBefore(emitters, il.Create(OpCodes.Ldfld, windHead));
            il.InsertBefore(emitters, il.Create(OpCodes.Callvirt, Unity("Component", "get_gameObject", UnityType("GameObject"), true)));
            var windBlur = Method(wind, "HandleWindBlurOculus");
            var source = At(windBlur, 0);
            source.OpCode = OpCodes.Ldarg_0;
            source.Operand = null;
            source.Next.OpCode = OpCodes.Ldfld;
            source.Next.Operand = windHead;
        }
    }
}
