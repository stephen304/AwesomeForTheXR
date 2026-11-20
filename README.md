# AwesomeForTheXR

This is an AI-assisted attempt to patch OpenXR support into the game AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome.

AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome is a "semi-sequel" to AaaaaAAaaaAAAaaAAAAaAAAAA!!! - A Reckless Disregard for Gravity 
which, among other additions like more levels, includes VR support for Oculus. Specifically, the Oculus Legacy Runtime 0.6 and lower, commonly used on the 
Oculus DK2.

Because the game lacks support for later versions of the Oculus runtime, playing the game is limited to only headsets that support the 
Legacy Runtime, meaning most modern headsets are unable to play the game.

## Goals

This project aims to create a patcher that can connect the rendered output from the game to the OpenXR runtime to allow any OpenVR capable headset to once again enjoy this game.

## Approach

The patcher modifies the game CSharp assembly to load an OpenXR plugin that submits frames, re-using the existing unity oculus camera rig.
