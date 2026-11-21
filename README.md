# AwesomeForTheXR

This is an AI-assisted attempt to patch OpenXR support into the arcade skydiving game AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome.

AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome is a "semi-sequel" to AaaaaAAaaaAAAaaAAAAaAAAAA!!! - A Reckless Disregard for Gravity which, among other additions like more levels, includes VR support for Oculus. Specifically, the Oculus Legacy Runtime 0.5/0.6, which is used on the Oculus DK2.

Because the game lacks support for later versions of the Oculus runtime, playing the game in VR is limited to only headsets that support the Legacy Runtime like the DK2, meaning most modern headsets are unable to play the game.

## Goals

This project aims to patch in support for modern VR/XR runtimes in order to preserve this game and keep it accessible for people to enjoy, provided their headset is supported by SteamVR's 32-bit OpenXR runtime.

## Quick Start

1. Clone the repo and run `Build.ps1`, or download the latest release zip.
2. With the game closed, run `Patch.ps1`, or unzip the release and open the patcher app.
3. Press `Y` to confirm patching or click the Patch button in the patcher GUI.
4. Run the game from steam using any of the 4 VR launch options

## Features

* Patches the original 32-bit game to use SteamVR through OpenXR 1.0 and Direct3D 11. The patcher has a simple UI that auto-detects your game location and backs up original files.
* Uses the original Oculus eye camera rig to send frames to OpenXR. The eye cameras are positioned using headset data and should support canted displays.
* Adds a better desktop view rendered from a new camera at the headset's center position.
* Fixes cover art in SteamVR's Now Playing panel when the game is launched through Steam.

## Approach

The patcher modifies the game runner to load an OpenXR plugin that submits the existing Oculus camera rig's frames to SteamVR. The dedicated Oculus launcher executable is patched to strip out all Oculus functionality and simply launch `Awesome.exe -vr`, which handles setting up SteamVR when `-vr` is present. All VR launch options behave the same and use OpenXR and D3D11. Only (show gfx options) requests the graphics dialog.

## Troubleshooting

* If the game erors with 'The 32-bit OpenXR runtime is unavailable', make sure you're using SteamVR 2.17 or later and select `Set SteamVR as OpenXR Runtime` under SteamVR Settings > OpenXR.

## License

MIT
