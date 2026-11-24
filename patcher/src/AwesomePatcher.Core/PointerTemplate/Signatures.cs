// Compile-time signatures for the fingerprinted player and injected input code.
using System;
namespace UnityEngine
{
    class Object {
        public extern string name { get; }
        public static extern bool operator ==(Object a, Object b);
        public static extern bool operator !=(Object a, Object b);
        public override bool Equals(object o) { return base.Equals(o); }
        public override int GetHashCode() { return base.GetHashCode(); }
        public static extern void Destroy(Object obj);
    }
    class Component : Object {
        public extern GameObject gameObject { get; }
        public extern Transform transform { get; }
        public extern Component GetComponent(Type type);
    }
    class Behaviour : Component { public extern bool enabled { get; set; } }
    class GUITexture : Behaviour { }
    class GameObject : Object {
        public GameObject(string name) {}
        public extern bool activeInHierarchy { get; }
        public extern Component AddComponent(Type type);
        public extern Transform transform { get; }
        public extern int layer { get; set; }
    }
    class Transform : Component {
        public extern Vector3 position { get; set; }
        public extern Transform parent { get; set; }
        public extern Vector3 TransformPoint(Vector3 p);
        public extern Vector3 InverseTransformPoint(Vector3 p);
    }
    struct Vector3 {
        public float x,y,z;
        public Vector3(float x,float y,float z) { this.x=x; this.y=y; this.z=z; }
        public static extern Vector3 operator +(Vector3 a,Vector3 b);
        public static extern Vector3 operator -(Vector3 a,Vector3 b);
        public static extern Vector3 operator *(Vector3 a,float b);
        public static extern float Dot(Vector3 a,Vector3 b);
        public static extern Vector3 Cross(Vector3 a,Vector3 b);
        public extern float magnitude { get; }
    }
    struct Vector2 { public float x,y; }
    struct Quaternion { public static extern Vector3 operator *(Quaternion r,Vector3 v); }
    struct Color { public Color(float r,float g,float b,float a) {} }
    struct Ray {}
    struct Rect { public extern float x {get;} public extern float y {get;} public extern float width {get;} public extern float height {get;} }
    class Camera : Behaviour {
        public extern Ray ViewportPointToRay(Vector3 v);
        public extern Ray ScreenPointToRay(Vector3 v);
        public extern Rect rect {get;}
    }
    class Renderer : Component {
        public extern bool enabled {get;set;}
        public extern Material sharedMaterial {set;}
    }
    class LineRenderer : Renderer {
        public extern void SetVertexCount(int count);
        public extern void SetPosition(int index,Vector3 p);
        public extern void SetWidth(float start,float end);
        public extern void SetColors(Color start,Color end);
        public extern bool useWorldSpace {set;}
    }
    class Shader : Object { public static extern Shader Find(string name); }
    class Material : Object {
        public Material(Shader shader) {}
        public extern int renderQueue {set;}
    }
    class MeshFilter : Component { public extern Mesh sharedMesh {get;} }
    class Mesh : Object {
        public extern Vector3[] vertices {get;}
        public extern int[] triangles {get;}
        public extern Vector2[] uv {get;}
    }
    static class Screen { public static extern int width {get;} public static extern int height {get;} }
    static class Time { public static extern int frameCount {get;} }
    enum KeyCode { Mouse0 = 323, Mouse6 = 329 }
    static class Input {
        public static extern bool GetMouseButton(int i);
        public static extern bool GetMouseButtonDown(int i);
        public static extern bool GetMouseButtonUp(int i);
        public static extern bool GetKey(KeyCode key);
        public static extern bool GetKeyDown(KeyCode key);
        public static extern bool anyKey {get;}
        public static extern bool anyKeyDown {get;}
        public static extern Vector3 mousePosition {get;}
        public static extern float GetAxis(string axis);
        public static extern float GetAxisRaw(string axis);
    }
}
class Options { public static bool isVRSupportEnabled; }
struct OVRPose { public UnityEngine.Vector3 position; public UnityEngine.Quaternion orientation; }
class OVRCameraRig : UnityEngine.Behaviour {
    public extern UnityEngine.Transform centerEyeAnchor {get;}
    public extern void AFTXR_PrepareHeadPose();
    public extern void AFTXR_SetPointerPanel(UnityEngine.MeshFilter surface);
}
class OVRManager : UnityEngine.Behaviour { public static extern OVRManager instance { get; } }
class Cursor : UnityEngine.Behaviour {
    public static Cursor singleton;
    public UnityEngine.GUITexture gui;
    public UnityEngine.Vector3 mousePosition, mousePositionUnclamped;
}
class GUIController : UnityEngine.Behaviour {
    public static GUIController instance;
    public GameScreen currentScreen;
    public extern bool InputEnabled { get; }
    public extern void InvalidateButtonHitOnDown();
}
class GameScreen : UnityEngine.Behaviour {}
class LevelSelectScreen : GameScreen { public bool inputEnabled; }
class SetupScreen : GameScreen { public bool inputEnabled; }
class GraphicsScreen : GameScreen { public bool inputEnabled; }
class ChoiceScreen : GameScreen { public bool inputEnabled; }
class LoadingScreen : GameScreen {}
class LoaderScene : UnityEngine.Behaviour { public bool loading, hasAnimationBegun; }
class GameInput : UnityEngine.Behaviour { public static GameInput singleton; public bool inputEnabled, disabledInputAtStartOfLevel; }
class GameController : UnityEngine.Behaviour { public static GameController singleton; }
static class PlayerData
{
    public static extern bool GloveEnabled { get; }
    public static extern bool SprayCanEnabled { get; }
    public static extern bool CaffeineEnabled { get; }
}
static class Utilities { public static extern bool IsMidGame { get; } }
