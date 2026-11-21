using System;

namespace AwesomePatcher.IntegrationTests
{
    internal static partial class Program
    {
        private static void TestMenuPointer()
        {
            uint leftGeneration = 0, rightGeneration = 0;
            void Frame(int left, int right, bool over, int owner, string phases)
            {
                int state = MenuPointer.SelectHand(left, right, leftGeneration, rightGeneration);
                MenuPointer.UpdateButtons(over && state != 0, state);
                string actual = (MenuPointer.down ? "d" : "") + (MenuPointer.held ? "h" : "") + (MenuPointer.up ? "u" : "");
                if (actual != phases || MenuPointer.owner != owner)
                    throw new InvalidOperationException("Unexpected menu pointer owner or button phase: " + actual);
            }
            Options.isVRSupportEnabled = false;
            if (!MenuPointer.MenuDown(0) || !MenuPointer.MenuHeld(0) || !MenuPointer.MenuUp(0) ||
                !MenuPointer.MouseDown(1) || MenuPointer.MouseAxis("mouse x") != 2 || MenuPointer.RawMouseAxis("Mouse X") != 7 || !MenuPointer.AnyHeld())
                throw new InvalidOperationException("Desktop mouse did not pass through to Unity.");
            Options.isVRSupportEnabled = true;
            if (MenuPointer.MenuDown(0) || MenuPointer.MenuHeld(0) || MenuPointer.MenuUp(0) ||
                MenuPointer.MouseDown(1) || MenuPointer.MouseAxis("mouse x") != 0 || MenuPointer.RawMouseAxis("Mouse X") != 0 || MenuPointer.AnyHeld() || MenuPointer.AnyDown())
                throw new InvalidOperationException("Physical mouse reached VR input.");
            // Unity's combined anyKeyDown is false while the physical mouse remains held.
            UnityEngine.Time.frameCount++;
            UnityEngine.Input.mouseDown = false;
            UnityEngine.Input.keyHeld = UnityEngine.Input.keyDown = true;
            if (!MenuPointer.AnyHeld() || !MenuPointer.AnyDown() || UnityEngine.Input.anyKeyDown)
                throw new InvalidOperationException("A held mouse swallowed the following keyboard edge.");
            UnityEngine.Time.frameCount++;
            UnityEngine.Input.keyDown = false;
            UnityEngine.Input.secondKeyDown = true;
            if (MenuPointer.AnyDown() || !MenuPointer.AnyHeld())
                throw new InvalidOperationException("A second key prematurely rearmed aggregate input.");
            Frame(1,1,true,1,"");
            Frame(1,3,true,1,"dh");
            Frame(3,3,true,0,""); // Switch during a drag. Neither hand can complete it.
            Frame(3,1,true,0,"");
            Frame(1,1,true,0,"");
            Frame(3,1,true,0,"dh");
            Frame(3,1,true,0,"h");
            Frame(1,1,true,0,"u");
            Frame(3,1,true,0,"dh");
            Frame(3,1,false,0,"");
            Frame(3,1,true,0,""); // Crossing back onto the panel with a held trigger.
            Frame(0,0,true,0,"");
            Frame(3,1,true,0,""); // Reconnect with trigger held.
            Frame(1,1,true,0,"");
            // Return to the same pause screen after pulling both triggers during gameplay.
            MenuPointer.ResetButtons();
            Frame(3,3,true,0,""); // Neither select nor switch hands.
            Frame(1,1,true,0,""); // No release from the cancelled input.
            Frame(3,1,true,0,"dh");
            MenuPointer.ResetButtons(); // Change screen during a drag.
            Frame(1,1,true,0,"");
            Frame(3,1,true,0,"dh");
            Frame(0,1,true,0,""); // Tracking loss while pressed.
            Frame(1,1,true,0,""); // A release on recovery cannot activate the button.
            Frame(3,1,true,0,"dh");
            ++rightGeneration;
            Frame(3,3,true,0,"h"); // The other hand recovered pressed between reads. The drag continues.
            Frame(1,1,true,0,"u"); // The unaffected hand can finish that drag normally.
            Frame(3,1,true,0,"dh");
            ++leftGeneration;
            Frame(1,1,true,0,""); // Owner lost tracking and released between reads. Cancel its drag.
            Frame(3,1,true,0,"dh"); // A fresh press works immediately.
            ++leftGeneration;
            ++rightGeneration;
            Frame(1,1,true,0,""); // Released while the dashboard owned input.
            Frame(3,1,true,0,"dh");
            ++leftGeneration;
            Frame(3,1,true,0,""); // Still held after focus returned.
            Frame(1,1,true,0,"");
            Frame(3,1,true,0,"dh");
            Console.WriteLine("PASS: desktop and VR mouse routing, pointer hand switching, click phases, drag cancellation and tracking recovery.");
        }
    }
}

internal static class Options { public static bool isVRSupportEnabled; }
internal static partial class MenuPointer { private static void Sample() {} }

// A physical mouse and optional simultaneous keyboard key, with no Unity player required.
namespace UnityEngine
{
    internal struct Vector3 {}
    internal enum KeyCode { Space = 32, A = 97, Mouse0 = 323, Mouse6 = 329 }
    internal static class Time { internal static int frameCount; }
    internal static class Input
    {
        internal static bool mouseDown = true, keyHeld, keyDown, secondKeyDown;
        public static bool GetMouseButton(int i) => true;
        public static bool GetMouseButtonDown(int i) => mouseDown;
        public static bool GetMouseButtonUp(int i) => true;
        public static bool GetKey(KeyCode k) => k == KeyCode.Mouse0 || (k == KeyCode.Space && keyHeld) || (k == KeyCode.A && secondKeyDown);
        public static bool GetKeyDown(KeyCode k) => (k == KeyCode.Mouse0 && mouseDown) || (k == KeyCode.Space && keyDown) || (k == KeyCode.A && secondKeyDown);
        public static bool anyKey => true;
        public static bool anyKeyDown => mouseDown;
        public static float GetAxis(string axis) => 2;
        public static float GetAxisRaw(string axis) => 7;
        public static Vector3 mousePosition => new Vector3();
    }
}
