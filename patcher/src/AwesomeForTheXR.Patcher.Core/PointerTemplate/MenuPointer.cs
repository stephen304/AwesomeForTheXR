using System;
using System.Runtime.InteropServices;
using UnityEngine;

// This class is compiled into a template, then bound to the retail game's Unity/Mono types.
internal static partial class MenuPointer
{
    [DllImport("AwesomeForTheXR32", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AFTXR_PointerHand(int hand, out OVRPose pose, out uint generation);

    private static OVRCameraRig rig;
    private static int frame = -1;
    private static bool wanted, hit;
    private static GameScreen screen;
    private static Vector3 uv;
    private static Renderer panel;
    private static LineRenderer beam;
    private static Material beamMaterial;
    private static Vector3 a, ab, ac, normal;
    private static Vector2 ta, tb, tc;

    internal static void Visibility(Behaviour graphic, bool show)
    {
        if (!Options.isVRSupportEnabled) { graphic.enabled = show; return; }
        if (wanted != show) ResetPointer();
        wanted = show;
        graphic.enabled = show && hit;
    }

    internal static void RegisterPanel(Component plane)
    {
        if (!Options.isVRSupportEnabled || OVRManager.instance == null) return;
        string name = plane.gameObject.name;
        bool menu = name == "OVRGuiObjectMain" && plane.transform.parent == null;
        if (!menu && name != "HUDRenderTexture") return;
        var ownerRig = (OVRCameraRig)OVRManager.instance.GetComponent(typeof(OVRCameraRig));
        if (ownerRig == null) return;
        // The menu is a scene root. The jump/replay HUD belongs to the moved LeftEyeJunk group.
        if (!menu && (plane.transform.parent == null || plane.transform.parent.parent != ownerRig.centerEyeAnchor)) return;
        ownerRig.AFTXR_SetPointerPanel((MeshFilter)plane.GetComponent(typeof(MeshFilter)));
    }

    internal static void SetRig(OVRCameraRig active, MeshFilter surface)
    {
        if (!Options.isVRSupportEnabled || (active != null && (!active.enabled || !active.gameObject.activeInHierarchy))) return;
        ResetPointer();
        if (beam != null) UnityEngine.Object.Destroy(beam.gameObject);
        beam = null;
        rig = active;
        panel = BindPanel(surface);
    }

    internal static void ReleaseRig(OVRCameraRig disabled)
    {
        if (ReferenceEquals(rig, disabled)) SetRig(null, null);
    }

    private static void ResetPointer()
    {
        if (held && GUIController.instance != null) GUIController.instance.InvalidateButtonHitOnDown();
        ResetButtons();
        frame = -1;
        hit = false;
        if (Cursor.singleton != null && Cursor.singleton.gui != null) Cursor.singleton.gui.enabled = false;
        if (beam != null) beam.enabled = false;
    }

    // Unity's built-in rectangular Plane and Quad both have affine UVs, but use different
    // local axes. Use a triangle from the actual mesh for the world-to-texture mapping.
    private static Renderer BindPanel(MeshFilter surface)
    {
        if (surface == null || surface.sharedMesh == null) return null;
        Mesh mesh = surface.sharedMesh;
        int[] indices = mesh.triangles;
        Vector3[] vertices = mesh.vertices;
        Vector2[] coords = mesh.uv;
        if (indices.Length < 3 || coords.Length != vertices.Length) return null;
        int i = indices[0], j = indices[1], k = indices[2];
        a = vertices[i]; ab = vertices[j] - a; ac = vertices[k] - a;
        normal = Vector3.Cross(ab, ac);
        if (Vector3.Dot(normal, normal) < 1e-12f) return null;
        ta = coords[i]; tb = coords[j]; tc = coords[k];
        return (Renderer)surface.GetComponent(typeof(Renderer));
    }

    private static bool Intersect(Transform tracking, OVRPose aim, out Vector3 origin, out Vector3 end)
    {
        origin = tracking.TransformPoint(aim.position);
        Vector3 forward = aim.orientation * new Vector3(0, 0, 1);
        Vector3 target = tracking.TransformPoint(aim.position + forward);
        Transform surface = panel.transform;
        Vector3 start = surface.InverseTransformPoint(origin);
        Vector3 ray = surface.InverseTransformPoint(target) - start;
        float denom = Vector3.Dot(normal, ray);
        end = origin;
        if (Math.Abs(denom) < 1e-8f) return false;
        float distance = Vector3.Dot(normal, a - start) / denom;
        if (distance <= 0) return false;
        Vector3 point = start + ray * distance;
        Vector3 offset = point - a;
        float norm2 = Vector3.Dot(normal, normal);
        float b = Vector3.Dot(Vector3.Cross(offset, ac), normal) / norm2;
        float c = Vector3.Dot(Vector3.Cross(ab, offset), normal) / norm2;
        uv = new Vector3(ta.x + b*(tb.x-ta.x) + c*(tc.x-ta.x), ta.y + b*(tb.y-ta.y) + c*(tc.y-ta.y), 0);
        if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) return false;
        end = surface.TransformPoint(point);
        return true;
    }

    private static void DrawBeam(Vector3 start, Vector3 end, Transform tracking)
    {
        if (beam == null)
        {
            if (beamMaterial == null)
            {
                Shader shader = Shader.Find("Particles/Alpha Blended");
                if (shader == null) shader = Shader.Find("Particles/Additive");
                if (shader == null) return;
                // The retail menu and HUD both draw at Transparent+100 (3100).
                beamMaterial = new Material(shader) { renderQueue = 3101 };
            }
            GameObject go = new GameObject("OpenXR menu pointer beam");
            go.transform.parent = tracking;
            go.layer = panel.gameObject.layer;
            beam = (LineRenderer)go.AddComponent(typeof(LineRenderer));
            beam.sharedMaterial = beamMaterial;
            beam.useWorldSpace = true;
            beam.SetVertexCount(2);
            beam.SetColors(new Color(.7f,.9f,1,.28f), new Color(.83f,.96f,1,.65f));
        }
        float width = (tracking.TransformPoint(new Vector3(.003f,0,0)) - tracking.position).magnitude;
        beam.SetWidth(width, width * .55f);
        beam.SetPosition(0, start);
        beam.SetPosition(1, end);
        beam.enabled = true;
    }

    // All consumers see the same edges, including the gallery's LateUpdate.
    internal static void Sample()
    {
        GUIController gui = GUIController.instance;
        GameScreen current = gui != null ? gui.currentScreen : null;
        if (!ReferenceEquals(current, screen))
        {
            ResetPointer();
            screen = current;
        }
        if (frame == Time.frameCount) return;
        if (rig == null || rig.centerEyeAnchor == null)
        {
            ResetPointer();
            frame = Time.frameCount;
            return;
        }
        frame = Time.frameCount;
        rig.AFTXR_PrepareHeadPose();
        OVRPose left, right;
        uint leftGeneration, rightGeneration;
        int l = AFTXR_PointerHand(0, out left, out leftGeneration);
        int r = AFTXR_PointerHand(1, out right, out rightGeneration);
        int state = SelectHand(l, r, leftGeneration, rightGeneration);
        Cursor cursor = Cursor.singleton;
        Vector3 start = new Vector3(), end = new Vector3();
        Transform tracking = rig.centerEyeAnchor.parent;
        bool wasHeld = held;
        bool ready = wanted && current != null && cursor != null &&
            panel != null && panel.enabled && panel.gameObject.activeInHierarchy;
        hit = ready && state != 0
            && Intersect(tracking, owner == 0 ? left : right, out start, out end);
        if (cursor != null && cursor.gui != null) cursor.gui.enabled = hit;
        if (wasHeld && (!hit || cancelHold) && gui != null) gui.InvalidateButtonHitOnDown();
        UpdateButtons(hit, state);
        if (!hit) { if (beam != null) beam.enabled = false; return; }
        cursor.mousePosition = new Vector3(uv.x * Screen.width, uv.y * Screen.height, 0);
        cursor.mousePositionUnclamped = cursor.mousePosition;
        cursor.transform.position = uv;
        DrawBeam(start, end, tracking);
    }

    internal static void UpdateCursor() { if (wanted) Sample(); }
    internal static bool CanHit() { if (!Options.isVRSupportEnabled) return true; Sample(); return hit; }
    internal static Ray ButtonRay(Camera camera, Vector3 position)
    {
        if (!Options.isVRSupportEnabled) return camera.ScreenPointToRay(position);
        Rect rect = camera.rect;
        return camera.ViewportPointToRay(new Vector3((uv.x-rect.x)/rect.width, (uv.y-rect.y)/rect.height, 0));
    }
}
