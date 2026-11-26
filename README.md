# AwesomeForTheXR

This is an AI-assisted patch that adds OpenXR support to the arcade base-jumping game AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome.

> [!NOTE]
> Also check out [eliwss0's VR remaster](https://github.com/eliwss0/V-AaaaaAAaaaAAAaaAAAAaAAAAAR), which goes further and also includes hand and item models in game!

AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome is a "semi-sequel" to AaaaaAAaaaAAAaaAAAAaAAAAA!!! - A Reckless Disregard for Gravity, bringing new levels and VR support for Oculus. The SDK used only supports the Oculus Legacy Runtime 0.5/0.6, which is used on the Oculus DK2. This prevents the VR mode from being played on modern headsets like the Quest or lighthouse based headsets.

## Goals

This project aims to patch in support for OpenXR in order to preserve this game and keep its VR mode playable far into the future. The only requirement is a headset supported by a 32-bit OpenXR runtime such as SteamVR 2.17 or later.

## Quick Start

1. Download `AwesomePatcher.exe` from the latest GitHub Release.
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
* Adapts the original Oculus eye camera rig in the game with headset display orientation and sends frames through OpenXR to SteamVR or another compatible runtime.
* Adds a better desktop view rendered from a new camera at the headset's center position.
* Switches the game cursor from mouse to pointing and clicking with motion controls.
* Maps game controls to OpenXR actions that can be rebound by the player in SteamVR.
* Upgrades movement (left stick) input to analog, bypassing the 50% binary threshold applied to gamepad.
* Fixes cover art in SteamVR's Now Playing panel when the game is launched through Steam.

## Default Controls

The patch supplies default OpenXR bindings for Valve Index Knuckles and Oculus Touch.

| Action | Knuckles | Oculus Touch
|---|---|---|
| Menu Navigation | Either trigger | Either trigger |
| Level Select | Left stick, trigger click, or click and drag | Left stick, trigger click, or click and drag
| Move | Left stick | Left stick |
| Turn left or right | Right stick | Right stick |
| Recenter view | Left B | Y |
| Escape (Pause/Resume/Exit) | Right B |  B |
| Deploy Parachute | Right A | A |
| Thumbs Up | Press either trackpad | Click right stick |
| Flip Off | Squeeze either grip | Squeeze either grip |
| Spray Graffiti | Either trigger | Either trigger |
| Use Espresso | Left A | X |

## Approach

The patcher modifies the game runner to load an OpenXR plugin that submits the existing Oculus camera rig's frames to SteamVR. The dedicated Oculus launcher executable is patched to strip out all Oculus functionality and simply launch `Awesome.exe -vr`, which initializes VR through OpenXR when `-vr` is present. All VR launch options behave the same and use OpenXR and D3D11. Only (show gfx options) requests the graphics dialog. Many other parts of the game are also patched to add in modern OpenXR actions that players can rebind in SteamVR, motion control pointer support, and analog player movement. Where possible, I aimed to preserve original game behavior - especially the normal desktop (non-vr) mode, and only added enhancements to added behavior (better desktop mirror cam, analog vr movement, vr controller pointer ray).

## Troubleshooting

* If the game errors with 'The 32-bit OpenXR runtime is unavailable', make sure you're using SteamVR 2.17 or later and select `Set SteamVR as OpenXR Runtime` under SteamVR Settings > OpenXR.

## Known Issues
* The desktop mirror cam occasionally glitches out and shows a small pixelated view. I'm not sure how to reliably reproduce this so I'll take any hints.
* Switching between the game window and other windows can be a little glitchy and having the desktop window focused can interfere with using the mouse.
* The desktop window size affects the size of the menu render area. Leave it full sized for best results.

## License

MIT
