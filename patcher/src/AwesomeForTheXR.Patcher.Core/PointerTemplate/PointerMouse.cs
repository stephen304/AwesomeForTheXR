using System;
using UnityEngine;

internal static partial class MenuPointer
{
    private static KeyCode[] keys;
    private static int keyFrame = -1;
    private static bool nonMouseHeld, nonMouseDown;

    internal static bool MenuDown(int button) { if (!Options.isVRSupportEnabled) return Input.GetMouseButtonDown(button); Sample(); return down; }
    internal static bool MenuHeld(int button) { if (!Options.isVRSupportEnabled) return Input.GetMouseButton(button); Sample(); return held; }
    internal static bool MenuUp(int button) { if (!Options.isVRSupportEnabled) return Input.GetMouseButtonUp(button); Sample(); return up; }
    internal static bool Splash(int button) { if (!Options.isVRSupportEnabled) return Input.GetMouseButtonDown(button); Sample(); return splashDown; }
    internal static float MouseAxis(string axis) { return Options.isVRSupportEnabled ? 0 : Input.GetAxis(axis); }
    internal static float RawMouseAxis(string axis) { return Options.isVRSupportEnabled ? 0 : Input.GetAxisRaw(axis); }
    internal static bool MouseDown(int button) { return !Options.isVRSupportEnabled && Input.GetMouseButtonDown(button); }
    internal static bool MouseHeld(int button) { return !Options.isVRSupportEnabled && Input.GetMouseButton(button); }
    internal static bool MouseUp(int button) { return !Options.isVRSupportEnabled && Input.GetMouseButtonUp(button); }
    internal static Vector3 MousePosition() { return Options.isVRSupportEnabled ? new Vector3() : Input.mousePosition; }
    internal static bool AnyDown() { if (!Options.isVRSupportEnabled) return Input.anyKeyDown; SampleKeys(); return nonMouseDown; }
    internal static bool AnyHeld() { if (!Options.isVRSupportEnabled) return Input.anyKey; SampleKeys(); return nonMouseHeld; }

    private static void SampleKeys()
    {
        if (keyFrame == Time.frameCount) return;
        keyFrame = Time.frameCount;
        nonMouseHeld = nonMouseDown = false;
        if (!Input.anyKey && !Input.anyKeyDown) return;
        if (keys == null) keys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
        foreach (KeyCode key in keys)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) continue;
            bool pressed = Input.GetKeyDown(key);
            bool heldNow = Input.GetKey(key);
            nonMouseHeld |= heldNow;
            nonMouseDown |= pressed;
            // Unity's aggregate is rearmed only after all contributing keys have been released.
            if (heldNow && !pressed) { nonMouseDown = false; return; }
        }
    }
}
