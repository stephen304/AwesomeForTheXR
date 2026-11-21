using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AwesomeForTheXR.Patcher.Core
{
    // All method edits target the original assembly fingerprint checked by GameAssemblyRecipe.
    internal sealed class CameraRecipe
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
            var first=At(start,0x5c);
            var coroutine=start.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.Name=="CallbackCoroutine");
            var resume=coroutine.Previous.Previous;
            // A pre-existing branch targets first. Keep that instruction as the jump.
            first.OpCode=OpCodes.Br; first.Operand=resume;
            while(first.Next!=resume) il.Remove(first.Next);

            Rewrite(Method(manager,"OnDisable")).Emit(OpCodes.Ret);
            il=Rewrite(Method(manager,"OnApplicationQuit"));
            il.Emit(OpCodes.Call,Native("AFTXR_ReleaseLaunch",none));
            il.Emit(OpCodes.Ret);
            il=Rewrite(Method(manager,"get_isHSWDisplayed"));
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            // The game reads this check during the splash. SteamVR owns headset notices.
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

            var anchors=Method(rig,"UpdateAnchors");
            var first=anchors.Body.Instructions.First();
            il=anchors.Body.GetILProcessor();
            il.InsertBefore(first,il.Create(OpCodes.Call,Method(manager,"get_display")));
            il.InsertBefore(first,il.Create(OpCodes.Callvirt,Method(display,"BeginFrame")));
            foreach(var field in new[]{"leftEyeCamera","rightEyeCamera"}) {
                il.InsertBefore(first,il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first,il.Create(OpCodes.Ldfld,Field(rig,field)));
                il.InsertBefore(first,il.Create(field=="leftEyeCamera" ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1));
                il.InsertBefore(first,il.Create(OpCodes.Call,apply));
            }
        }
    }
}
