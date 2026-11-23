# AwesomeForTheXR

This is an AI-assisted attempt to patch OpenXR support into the arcade skydiving game AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome.

AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome is a "semi-sequel" to AaaaaAAaaaAAAaaAAAAaAAAAA!!! - A Reckless Disregard for Gravity which, among other additions like more levels, includes VR support for Oculus. Specifically, the Oculus Legacy Runtime 0.5/0.6, which is used on the Oculus DK2.

Because the game lacks support for later versions of the Oculus runtime, playing the game in VR is limited to only headsets that support the Legacy Runtime like the DK2, meaning most modern headsets are unable to play the game.

## Goals

This project aims to patch in support for modern VR/XR runtimes in order to preserve this game and keep it accessible for people to enjoy, provided their headset is supported by SteamVR's 32-bit OpenXR runtime.

## Quick Start

1. Download the latest release zip.
2. With the game closed, run `AwesomePatcher.exe`.
3. Confirm the game location and click the Patch button.
4. Run the game from steam using any of the 4 VR launch options.

## Building from Source / Development

1. Clone the repo and run `Build.ps1`
2. Run `Patch.ps1`
3. Confirm the game location and press `Y` then `Enter` to patch.
4. Run the game using any VR launch option.

To package a single executable patcher after building it, run `Package.ps1` from the repository root. It writes `artifacts/release/AwesomePatcher.exe`. The patcher targets .NET Framework 4.8.

The same executable accepts `verify`, `patch`, and `revert` in a terminal. Run `AwesomePatcher.exe --help` for options.

## Features

* Patches the original 32-bit game to use SteamVR through OpenXR 1.0 and Direct3D 11.
* Simple patcher UI that auto-detects your game location and backs up original files.
* Adapts the original Oculus eye camera rig with headset display orientation and sends frames to OpenXR.
* Adds a better desktop view rendered from a new camera at the headset's center position.
* Switches the game cursor from mouse to pointing and clicking with motion controls.
* Maps game controls to rebindable OpenXR actions in SteamVR.
* Upgrades movement (left stick) input to analog, bypassing the 50% binary threshold applied to gamepad.
* Fixes cover art in SteamVR's Now Playing panel when the game is launched through Steam.

## Approach

The patcher modifies the game runner to load an OpenXR plugin that submits the existing Oculus camera rig's frames to SteamVR. The dedicated Oculus launcher executable is patched to strip out all Oculus functionality and simply launch `Awesome.exe -vr`, which handles setting up SteamVR when `-vr` is present. All VR launch options behave the same and use OpenXR and D3D11. Only (show gfx options) requests the graphics dialog.

## Troubleshooting

* If the game erors with 'The 32-bit OpenXR runtime is unavailable', make sure you're using SteamVR 2.17 or later and select `Set SteamVR as OpenXR Runtime` under SteamVR Settings > OpenXR.

## License

MIT
