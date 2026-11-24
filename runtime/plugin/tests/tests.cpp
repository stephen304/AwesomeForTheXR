#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "fixture.h"
#include "../src/input_context.h"
#include "../src/headset_retry.h"

namespace {
void check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

template<class Fn> Fn entry(HMODULE module, const char* name) {
    auto address = GetProcAddress(module, name);
    check(address != nullptr, name);
    return reinterpret_cast<Fn>(address);
}

struct Expected {
    const char* scenario;
    bool accepted, createdInstance;
    unsigned systemQueries;
    unsigned graphicsQueries;
    const char* reason;
    const char* diagnostic;
};

const Expected cases[] = {
    {"success",            true,  true,   1, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_SUCCESS"},
    {"late-headset",       true,  true,   3, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_SUCCESS"},
    {"destroy-failure",    true,  true,   1, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_ERROR_RUNTIME_FAILURE"},
    {"unsupported-headset",false, true,   1, 0, "No VR headset is available. Check that your headset is connected and ready.",                 "xrGetSystem: XR_ERROR_FORM_FACTOR_UNSUPPORTED"},
    {"no-runtime",         false, false,  0, 0, "The 32-bit OpenXR runtime is unavailable. Check your active OpenXR runtime.",   "xrCreateInstance: XR_ERROR_RUNTIME_UNAVAILABLE"},
    {"no-api",             false, false,  0, 0, "The active runtime does not support OpenXR 1.0.",              "xrCreateInstance: XR_ERROR_API_VERSION_UNSUPPORTED"},
    {"no-extension",       false, false,  0, 0, "The active OpenXR runtime does not support Direct3D 11.", "xrCreateInstance: XR_ERROR_EXTENSION_NOT_PRESENT"},
    {"runtime-properties", false, true,   0, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "xrGetInstanceProperties: XR_ERROR_RUNTIME_FAILURE"},
    {"system-properties",  false, true,   1, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "xrGetSystemProperties: XR_ERROR_SYSTEM_INVALID"},
    {"lookup",             false, true,   1, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "Resolve xrGetD3D11GraphicsRequirementsKHR: XR_ERROR_FUNCTION_UNSUPPORTED"},
    {"null-entry",         false, true,   1, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "no function address for xrGetD3D11GraphicsRequirementsKHR"},
    {"requirements",       false, true,   1, 1, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "xrGetD3D11GraphicsRequirementsKHR: XR_ERROR_RUNTIME_FAILURE"},
};

void testHeadsetTimeout() {
    unsigned elapsed = 0, queries = 0;
    const auto result = waitForHeadset(
        [&] { ++queries; return XR_ERROR_FORM_FACTOR_UNAVAILABLE; },
        [&] { return 1000u + elapsed; },
        [&](unsigned delay) {
            check(delay == 250 && elapsed < 10000, "incorrect headset retry interval or deadline");
            elapsed += delay;
        });
    check(result == XR_ERROR_FORM_FACTOR_UNAVAILABLE && elapsed == 10000 && queries == 41, "incorrect headset timeout");
}

// All channels are either zero or full intensity so comparison is exact in sRGB.
using Pixels = std::array<UINT, 4>;

void checkEyePixels(ID3D11Device* device, ID3D11Texture2D* output, const Pixels& source) {
    check(output != nullptr, "missing eye swapchain");
    D3D11_TEXTURE2D_DESC desc{};
    output->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> copy;
    check(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &copy)), "staging eye texture");
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    device->GetImmediateContext(&context);
    context->CopyResource(copy.Get(), output);
    D3D11_MAPPED_SUBRESOURCE read{};
    check(SUCCEEDED(context->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &read)), "read submitted eye");
    const auto* pixels = static_cast<const BYTE*>(read.pData);
    const bool flipped = std::memcmp(pixels, source.data() + 2, 2 * sizeof(UINT)) == 0 &&
                         std::memcmp(pixels + read.RowPitch, source.data(), 2 * sizeof(UINT)) == 0;
    context->Unmap(copy.Get(), 0);
    check(flipped, "wrong eye or vertical orientation in submitted texture");
}

void testHeadGeometry(HMODULE plugin, Fixture& state) {
    InputContext context = InputContext::Menus;
    auto beginFrame = entry<int (__cdecl*)(int, InputContext)>(plugin, "AFTXR_BeginCameraFrame");
    auto begin = [&](int number) { return beginFrame(number, context); };
    auto pose = entry<void (__cdecl*)(int, float*)>(plugin, "AFTXR_EyePose");
    auto projection = entry<void (__cdecl*)(int, float, float, float*)>(plugin, "AFTXR_EyeProjection");
    auto description = entry<void (__cdecl*)(int, float*)>(plugin, "AFTXR_EyeDescription");
    auto recenter = entry<void (__cdecl*)()>(plugin, "AFTXR_Recenter");
    auto submit = entry<void (__cdecl*)(int)>(plugin, "UnityRenderEvent");
    auto closeTo = [](float actual, float expected) { return std::abs(actual - expected) < 1.e-4f; };
    auto checkPose = [&](int anchor, std::array<float,7> expected) {
        float actual[7];
        pose(anchor, actual);
        for (int i = 0; i < 7; ++i) check(closeTo(actual[i], expected[i]), "wrong recentered Unity anchor pose");
    };
    auto frame = [&](int number) {
        check(begin(number) == 1, "begin geometry frame");
        submit(0x41585234);
    };
    state.nextEvent = XR_SESSION_STATE_READY;
    frame(1);
    checkPose(-1, {0, 0, 0, 0, 0, 0, 1});
    checkPose(0, {-.04f, .01f, .02f, 0, -.28f, 0, .96f});
    checkPose(1, {.03f, -.02f, -.01f, 0, .6f, 0, .8f});
    for (int i = 0; i < 2; ++i) {
        float desc[4], m[16];
        description(i, desc);
        projection(i, .1f, 100.f, m);
        check(desc[0] == (i ? 1100 : 1200) && desc[1] == (i ? 1300 : 1250), "wrong target size");
        const auto& f = state.eyeFovs[i];
        // The four runtime boundary rays must map to the four edges of this eye's clip rectangle.
        check(closeTo(m[0]*std::tan(f.angleLeft)-m[8], -1) && closeTo(m[0]*std::tan(f.angleRight)-m[8], 1) &&
              closeTo(m[5]*std::tan(f.angleDown)-m[9], -1) && closeTo(m[5]*std::tan(f.angleUp)-m[9], 1),
              "per-eye off-axis projection");
    }
    // Same head, parallel eye cameras. A change of optics must never steer the player.
    state.eyePoses[0].orientation = state.eyePoses[1].orientation = {0, 0, 0, 1};
    auto pointer = entry<int (__cdecl*)(int, float*, uint32_t*)>(plugin, "AFTXR_PointerHand");
    uint32_t generation[2]{};
    auto hand = [&](int i, float* pose) { return pointer(i, pose, &generation[i]); };
    float tracked[7]{};
    check(hand(0,tracked) == 0, "unfocused menu pointer");
    state.nextEvent = XR_SESSION_STATE_FOCUSED;
    state.handFlags[1] &= ~XR_SPACE_LOCATION_POSITION_TRACKED_BIT; // Rotation-only controller.
    frame(2);
    check(hand(0,tracked) == 3 && closeTo(tracked[0],-.04f) && closeTo(tracked[2],.02f), "left aim/trigger");
    check(hand(1,tracked) == 1 && closeTo(tracked[0],.03f), "right aim/trigger");
    state.nextEvent = XR_SESSION_STATE_VISIBLE;
    checkPose(-1, {0, 0, 0, 0, 0, 0, 1});
    checkPose(0, {-.04f, .01f, .02f, 0, 0, 0, 1});

    recenter();
    state.head = {{0, .6f, 0, .8f}, {999, 999, 999}};
    state.headFlags = XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    frame(3);
    check(hand(0,tracked) == 0 && hand(1,tracked) == 0, "stale pointer after focus loss");
    checkPose(-1, {0, 0, 0, 0, -.6f, 0, .8f}); // No invalid position or premature recenter.
    state.head = {{999,999,999,999}, {.2f, 1.5f, -.4f}};
    state.headFlags = XR_SPACE_LOCATION_POSITION_VALID_BIT;
    frame(4);
    checkPose(-1, {.1f, .1f, .2f, 0, -.6f, 0, .8f});

    // Yaw 90 degrees, looking straight up. Recenter removes translation and yaw, keeping pitch.
    state.head.orientation = {.5f, .5f, -.5f, .5f};
    state.headFlags |= XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    frame(5);
    const float half = std::sqrt(.5f);
    checkPose(-1, {0, 0, 0, -half, 0, 0, half});
    auto l = state.eyePoses[0].position;
    checkPose(0, {state.head.position.z-l.z, l.y-state.head.position.y, state.head.position.x-l.x,
                  0, half, 0, half});
    state.head.position.x += .1f;
    state.head.orientation = {0, half, 0, half};
    state.eyeFlags = 0; // Head tracking still updates when the runtime temporarily cannot locate the eyes.
    frame(6);
    checkPose(-1, {0, 0, -.1f, 0, 0, 0, 1});

    state.nextEvent = XR_SESSION_STATE_FOCUSED;
    state.handFlags[0] &= ~XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
    state.pointerSelect[0].held = false;
    frame(7);
    check(hand(0,tracked) == 0 && hand(1,tracked) == 1, "inferred release from previously tracked hand");
    state.handFlags[0] = Fixture::trackedHand;
    state.pointerSelect[0].held = true;
    state.handFlags[1] &= ~XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    frame(8);
    check(hand(0,tracked) == 3 && hand(1,tracked) == 0, "tracking recovery or stale rotation");
    state.handActive[0] = false;
    frame(9);
    check(hand(0,tracked) == 0, "inactive aim");
    state.handActive[0] = true;
    state.handFlags[0] &= ~XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
    state.handFlags[1] = Fixture::trackedHand & ~XR_SPACE_LOCATION_POSITION_VALID_BIT;
    frame(10);
    check(hand(0,tracked) == 0 && hand(1,tracked) == 0, "temporary aim inactivity erased tracking history");
    auto before = generation[0];
    auto other = generation[1];
    state.handProfiles[0] = nullptr;
    state.profileChanged = true;
    frame(11);
    state.handProfiles[0] = "valve/index_controller";
    state.profileChanged = true;
    frame(12);
    check(hand(0,tracked) == 0 && generation[0] == before, "same profile returned without live position");
    state.handProfiles[0] = "khr/simple_controller";
    state.profileChanged = true;
    frame(13);
    check(hand(0,tracked) == 3 && generation[0] != before, "replacement rotation-only source");
    check(hand(1,tracked) == 0 && generation[1] == other, "replacement interrupted the other hand");

    before = generation[0];
    state.nextEvent = XR_SESSION_STATE_VISIBLE;
    state.followingEvent = XR_SESSION_STATE_FOCUSED;
    state.pointerSelect[0].held = false;
    frame(14);
    frame(15); // Unity had no pointer consumer in the frame that delivered both events.
    check(hand(0,tracked) == 1 && generation[0] != before, "dashboard interruption lost before Unity read it");
    check(hand(1,tracked) == 0 && generation[1] != other, "dashboard interruption missing on other hand");

    state.handFlags[1] = Fixture::trackedHand;
    state.pointerSelect[0].held = state.pointerSelect[1].held = true;
    frame(16);
    check(hand(0,tracked) == 3 && hand(1,tracked) == 3, "both hands held before tracking loss");
    const auto leftBeforeLoss = generation[0], rightBeforeLoss = generation[1];
    state.handFlags[1] &= ~XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
    state.pointerSelect[1].held = false;
    frame(17); // Unity skips the pointer read on the only frame with lost tracking.
    state.handFlags[1] = Fixture::trackedHand;
    frame(18);
    check(hand(1,tracked) == 1 && generation[1] == rightBeforeLoss + 1, "missed right-hand interruption");
    check(hand(0,tracked) == 3 && generation[0] == leftBeforeLoss, "right-hand loss interrupted the left drag");

    context = InputContext::GameplayWithGlove;
    auto merge = entry<int (__cdecl*)(int, InputContext, XrVector2f*, XrVector2f*)>(plugin, "AFTXR_MergeGamepadSticks");
    auto checkSticks = [&](int number, XrVector2f padMove, XrVector2f padTurn, XrVector2f wantMove, XrVector2f wantTurn, bool wantXrMove) {
        check(merge(number, context, &padMove, &padTurn) == (wantXrMove ? 1 : 0), "wrong movement source");
        check(padMove.x == wantMove.x && padMove.y == wantMove.y &&
              padTurn.x == wantTurn.x && padTurn.y == wantTurn.y, "wrong GameInput stick contribution");
    };
    const auto syncs = state.actionSyncs;
    state.handActive[0] = false;
    state.sticks[0] = {.5f, -.5f};
    state.sticks[1] = {-.4f, .3f};
    // GameInput first, then rendering, then a second read. The stronger physical
    // movement wins even though the XR diagonal is longer but below retail's gate.
    checkSticks(19, {.55f, 0}, {}, {.55f, 0}, state.sticks[1], false);
    frame(19);
    checkSticks(19, {}, {.9f, 0}, state.sticks[0], {.9f, 0}, true);
    check(state.actionSyncs == syncs + 1 && hand(0,tracked) == 0, "sticks resynced or required pointer tracking");

    state.sticks[0] = {.75f, .25f};
    state.stickActive[1] = false;
    frame(20); // Rendering first. An inactive right action must preserve the physical pad.
    checkSticks(20, {}, {.1f, -.2f}, state.sticks[0], {.1f, -.2f}, true);
    checkSticks(20, {-.75f, 0}, {}, {-.75f, 0}, {}, false);
    check(state.actionSyncs == syncs + 2, "render and GameInput observed different input frames");
    state.syncNotFocused = true;
    checkSticks(21, {}, {}, {}, {}, false);
    frame(21);
    state.syncNotFocused = false;
    state.nextEvent = XR_SESSION_STATE_VISIBLE;
    frame(22);
    checkSticks(22, {.6f, 0}, {}, {.6f, 0}, {}, false);
    state.nextEvent = XR_SESSION_STATE_FOCUSED;
    state.stickActive[0] = false;
    state.stickActive[1] = true;
    frame(23);
    checkSticks(23, {}, {}, {}, state.sticks[1], false);

    state.stickActive[0] = true;
    state.sticks[0] = {.25f, -.125f};
    checkSticks(24, {-.5f, .4f}, {}, state.sticks[0], state.sticks[1], true);
    checkSticks(24, {0, -.51f}, {}, {0, -.51f}, state.sticks[1], false);
    state.sticks[0] = {};
    checkSticks(25, {.49f, -.5f}, {}, {.49f, -.5f}, state.sticks[1], false);

    auto readDeployParachutePressed = entry<int (__cdecl*)(int, InputContext)>(plugin, "AFTXR_GetDeployParachutePressed");
    auto deployPressed = [&](int number) { return readDeployParachutePressed(number, context); };
    const auto beforeDeploy = state.actionSyncs;
    state.deployHeld = true;
    check(deployPressed(26) == 1 && deployPressed(26) == 1, "deploy press changed between reads");
    frame(26);
    check(deployPressed(27) == 0 && state.actionSyncs == beforeDeploy + 2, "held deploy or duplicate sync");
    state.deployHeld = false;
    check(deployPressed(28) == 0, "release produced a press");
    state.deployActive = false;
    state.deployHeld = true;
    check(deployPressed(29) == 0, "inactive deploy");
    state.deployActive = true;
    check(deployPressed(30) == 0, "reconnected while held");
    state.deployHeld = false;
    check(deployPressed(31) == 0, "release after reconnect");
    state.nextEvent = XR_SESSION_STATE_VISIBLE;
    state.followingEvent = XR_SESSION_STATE_FOCUSED;
    state.deployHeld = true;
    frame(32);
    check(deployPressed(32) == 0, "held across dashboard");
    state.deployHeld = false;
    check(deployPressed(33) == 0, "release after dashboard");
    state.deployHeld = true;
    check(deployPressed(35) == 0, "press leaked across an unsampled frame");
    state.deployHeld = false;
    check(deployPressed(36) == 0, "release after input gap");
    state.deployHeld = true;
    check(deployPressed(37) == 1, "fresh press after recovery");

    state.handTicks[0] = state.handTicks[1] = 0;
    auto readSpectatorGesturePresses = entry<int (__cdecl*)(int, InputContext)>(plugin, "AFTXR_GetSpectatorGesturePresses");
    auto gestures = [&](int number) { return readSpectatorGesturePresses(number, context); };
    state.thumbsUp[0].held = true;
    check(gestures(38) == 1 && state.handTicks[0] == 1 && state.handTicks[1] == 0, "left pad feedback");
    state.flipOff[1].held = true;
    check(gestures(39) == 2 && state.handTicks[0] == 1 && state.handTicks[1] == 1, "right grip feedback while left pad held");
    state.thumbsUp[1].held = state.flipOff[0].held = true;
    check(gestures(40) == 3 && gestures(40) == 3, "opposite hands masked or consumed a fresh press");
    check(gestures(41) == 0, "held gestures repeated");
    state.thumbsUp[1].active = false;
    state.thumbsUp[1].held = false;
    check(gestures(42) == 0, "inactive pad");
    state.thumbsUp[1].active = state.thumbsUp[1].held = true;
    check(gestures(43) == 0, "reconnected pad fired while held");
    check(state.handTicks[0] == 2 && state.handTicks[1] == 2, "duplicate feedback or wrong hand");
    state.thumbsUp[0].held = state.flipOff[0].held = false;
    check(gestures(44) == 0, "gesture release fired");
    state.hapticResult = XR_ERROR_RUNTIME_FAILURE;
    state.thumbsUp[0].held = state.flipOff[0].held = true;
    check(gestures(45) == 3 && state.handTicks[0] == 3 && state.handTicks[1] == 2,
          "simultaneous gestures on one hand need one tick and both inputs even if vibration fails");

    context = InputContext::Menus;
    state.hapticResult = XR_SUCCESS;
    state.handActive[0] = true;
    state.pointerSelect[0].held = state.pointerSelect[1].held = false;
    state.flipOff[0].held = false;
    state.deployHeld = false;
    frame(46);
    state.deployHeld = true;
    state.pointerSelect[1].held = true;
    check(deployPressed(47) == 0, "a fresh press in the menu must not request deployment");
    check(gestures(47) == 0, "right trigger fired a gesture");
    frame(47); // Input collection and rendering both see this press.
    check(hand(1,tracked) == 3 && state.handTicks[0] == 3 && state.handTicks[1] == 3, "right trigger feedback");
    state.pointerSelect[0].held = true;
    frame(48); // The other hand can take the pointer while the right trigger is held.
    check(hand(0,tracked) == 3 && gestures(48) == 0 && state.handTicks[0] == 4 && state.handTicks[1] == 3,
          "left trigger feedback while right held");
    state.pointerSelect[0].held = false;
    frame(49);
    state.flipOff[0].held = state.pointerSelect[0].held = true;
    frame(50);
    check(gestures(50) == 0 && state.handTicks[0] == 5 && state.handTicks[1] == 3, "menu trigger must tick without firing the grip action");
    checkSticks(50, {}, {}, {}, {}, false);
    context = InputContext::Levels;
    state.levelStickActive = false;
    frame(51);
    check(state.levelsActive && state.menusActive, "gallery requires both navigation and pointer");
    checkSticks(51, {.6f, 0}, {}, {.6f, 0}, {}, false);
    state.levelStickActive = true;
    state.stickActive[1] = false;
    frame(52);
    checkSticks(52, {}, {.2f, 0}, state.levelStick, {.2f, 0}, false);
    const auto gallerySyncs = state.actionSyncs;
    context = InputContext::Inactive;
    checkSticks(52, {}, {}, {}, {}, false);
    check(state.actionSyncs == gallerySyncs, "transition performed a second sync in one frame");
    checkSticks(53, {}, {}, {}, {}, false); // GameInput samples the loading screen before the camera.
    frame(53);
    check(state.actionSyncs == gallerySyncs && hand(0,tracked) == 0 && hand(1,tracked) == 0,
          "loading polled input or kept a gallery pointer");
    const auto leftTicks = state.handTicks[0], rightTicks = state.handTicks[1];
    context = InputContext::GameplayWithGlove;
    state.stickActive[1] = true;
    frame(54);
    check(state.gameActive && !state.menusActive && state.actionSyncs == gallerySyncs + 1 &&
          gestures(54) == 0 && deployPressed(54) == 0 &&
          state.handTicks[0] == leftTicks && state.handTicks[1] == rightTicks,
          "held buttons leaked into gameplay after loading");
    checkSticks(54, {}, {}, {}, state.sticks[1], false);

    // Both consumers can synchronize first. Each press must produce the same result in either order.
    int number = 54;
    for (bool cameraFirst : {false, true}) {
        auto sample = [&](int expectedPresses, unsigned ticksPerHand) {
            ++number;
            const auto syncsBefore = state.actionSyncs;
            const auto leftTicks = state.handTicks[0], rightTicks = state.handTicks[1];
            if (cameraFirst) frame(number);
            check(gestures(number) == expectedPresses, "wrong glove-gated gesture");
            if (!cameraFirst) frame(number);
            check(gestures(number) == expectedPresses && state.actionSyncs == syncsBefore + 1,
                  "camera and GameInput did not share one gesture sample");
            check(state.handTicks[0] == leftTicks + ticksPerHand && state.handTicks[1] == rightTicks + ticksPerHand,
                  "wrong haptic count for glove state or call order");
        };
        context = InputContext::Gameplay;
        state.thumbsUp[0].held = state.flipOff[1].held = false;
        sample(0, 0);
        state.thumbsUp[0].held = state.flipOff[1].held = true;
        sample(0, 0);
        context = InputContext::GameplayWithGlove;
        sample(0, 0); // Held across unlock.
        state.thumbsUp[0].held = state.flipOff[1].held = false;
        state.deployHeld = false;
        sample(0, 0);
        state.thumbsUp[0].held = state.flipOff[1].held = state.deployHeld = true;
        state.sticks[0] = {.75f, 0};
        sample(3, 1);
        check(deployPressed(number) == 1, "deployment alongside gestures");
        context = InputContext::Gameplay;
        check(gestures(number) == 0 && deployPressed(number) == 1, "relocking must clear only cached gestures");
        checkSticks(number, {}, {}, state.sticks[0], state.sticks[1], true);
    }

    context = InputContext::Levels;
    state.pointerSelect[0].held = state.pointerSelect[1].held = false;
    frame(++number);
    checkSticks(number, {}, {}, state.levelStick, {}, false);
    check(hand(0,tracked) == 1, "missing gallery aim");
    const float previousY = tracked[1];
    const auto ticksBeforeFade = state.handTicks[0];
    const auto syncsBeforeFade = state.actionSyncs;
    context = InputContext::MenuAimOnly;
    checkSticks(number, {}, {}, {}, {}, false);
    check(hand(0,tracked) == 1 && state.actionSyncs == syncsBeforeFade, "same-frame lock lost aim or resynced");
    state.eyePoses[0].position.y += .2f;
    state.pointerSelect[0].held = true;
    state.pointerSelect[1].active = false;
    frame(++number);
    check(!state.levelsActive && state.menusActive && hand(1,tracked) == 1, "locked menu lost aim or enabled navigation");
    check(hand(0,tracked) == 3 && closeTo(tracked[1], previousY + .2f) && state.handTicks[0] == ticksBeforeFade,
          "locked aim did not move or a locked press vibrated");
    const auto lockedGeneration = generation[0];
    state.pointerSelect[1].active = true;
    frame(++number);
    check(hand(1,tracked) == 1, "released trigger during the fade");
    const auto releasedGeneration = generation[1], rightTicksBeforeUnlock = state.handTicks[1];
    state.pointerSelect[1].held = true;
    context = InputContext::Menus;
    frame(++number);
    check(hand(0,tracked) == 3 && generation[0] != lockedGeneration && state.handTicks[0] == ticksBeforeFade,
          "held trigger crossed the fade without a button interruption");
    check(hand(1,tracked) == 3 && generation[1] == releasedGeneration && state.handTicks[1] == rightTicksBeforeUnlock + 1,
          "fresh trigger on the first unlocked frame was lost");
    state.pointerSelect[0].held = false;
    frame(++number);
    state.pointerSelect[0].held = true;
    frame(++number);
    check(hand(0,tracked) == 3 && state.handTicks[0] == ticksBeforeFade + 1, "fresh menu press after the fade");
}

void testFrames(HMODULE plugin, ID3D11Device* device, Fixture& state, bool loseSession) {
    InputContext context = InputContext::Menus;
    auto beginFrame = entry<int (__cdecl*)(int, InputContext)>(plugin, "AFTXR_BeginCameraFrame");
    auto begin = [&](int number) { return beginFrame(number, context); };
    auto submit = entry<void (__cdecl*)(int)>(plugin, "UnityRenderEvent");
    constexpr int endFrameEvent = 0x41585234;
    state.nextEvent = XR_SESSION_STATE_READY;
    if (loseSession) {
        check(begin(1) == 1 && state.starts == 1, "session never started");
        submit(endFrameEvent);
        state.nextEvent = XR_SESSION_STATE_LOSS_PENDING;
        check(begin(2) == -1, "session loss must use the fatal path");
        auto status = entry<const char* (__cdecl*)()>(plugin, "AFTXR_GetLaunchStatus");
        check(std::strstr(status(), "session was lost") != nullptr, "missing session loss diagnostic");
        return;
    }

    auto rendered = entry<void (__cdecl*)(int, IUnknown*, int)>(plugin, "AFTXR_RenderedEye");
    const Pixels colors[] = {{0xff0000ff, 0xff00ffff, 0xffff0000, 0xffff00ff},
                             {0xff00ff00, 0xffffff00, 0xffffffff, 0xff000000}};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> eyes[2];
    for (unsigned i = 0; i < 2; ++i) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = desc.Height = 2;
        desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{colors[i].data(), 2 * sizeof(UINT)};
        check(SUCCEEDED(device->CreateTexture2D(&desc, &data, &eyes[i])), "Unity eye texture");
    }
    auto stereoFrame = [&](int frame) {
        check(begin(frame) == 1, "failed to begin stereo frame");
        for (int i = 0; i < 2; ++i) rendered(i, eyes[i].Get(), frame);
        submit(endFrameEvent);
        for (int i = 0; i < 2; ++i) checkEyePixels(device, state.eyeImages[i], colors[i]);
    };

    auto deployPressed = entry<int (__cdecl*)(int, InputContext)>(plugin, "AFTXR_GetDeployParachutePressed");
    context = InputContext::Inactive;
    state.followingEvent = XR_SESSION_STATE_FOCUSED;
    stereoFrame(1); // The headset gains focus before the splash accepts input.
    check(state.starts == 1 && state.projections == 1 && state.releases == 2, "first stereo projection");
    check(deployPressed(1, context) == 0 && state.actionSyncs == 0, "focused splash polled input");
    context = InputContext::Menus;
    check(begin(2) == 1, "begin incomplete frame");
    check(deployPressed(2, context) == 0 && state.menusActive && state.actionSyncs == 1,
          "first interactive menu frame must sync once");
    rendered(0, eyes[0].Get(), 2);
    submit(endFrameEvent);
    check(state.emptyFrames == 1 && state.projections == 1 && state.releases == 2, "submitted a stale eye");

    state.nextEvent = XR_SESSION_STATE_STOPPING;
    check(begin(3) == 1 && begin(4) == 1, "temporary stop quit the game");
    submit(endFrameEvent);
    check(state.stops == 1 && state.frameWaits == 2 && state.emptyFrames == 1, "rendered while idle");
    state.nextEvent = XR_SESSION_STATE_READY;
    stereoFrame(5);
    check(state.starts == 2 && state.frameWaits == 3 && state.projections == 2 && state.releases == 4, "resume failed");

    state.nextEvent = XR_SESSION_STATE_STOPPING;
    check(begin(6) == 1, "quit before EXITING");
    state.nextEvent = XR_SESSION_STATE_EXITING;
    check(begin(7) == 0 && state.stops == 2 && state.frameWaits == 3, "runtime exit did not quit cleanly");
}

void testDevice(const wchar_t* pluginPath, const std::filesystem::path& folder, const std::string& scenario) {
    using Microsoft::WRL::ComPtr;
    ComPtr<IDXGIFactory1> factory;
    check(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))), "DXGI factory");
    // Prefer a non-default adapter if it can create a device. This also covers
    // the Basic Render Driver on machines exposing it through this DXGI interface.
    UINT index = 0;
    ComPtr<ID3D11Device> device;
    DXGI_ADAPTER_DESC desc{};
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter> adapter;
        HRESULT hr = factory->EnumAdapters(i, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        check(SUCCEEDED(hr), "DXGI enumeration");
        ComPtr<ID3D11Device> candidate;
        UINT flags = scenario == "device-single-thread" ? D3D11_CREATE_DEVICE_SINGLETHREADED : 0;
        if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, nullptr, 0,
                                     D3D11_SDK_VERSION, &candidate, nullptr, nullptr))) continue;
        check(SUCCEEDED(adapter->GetDesc(&desc)), "adapter description");
        device = candidate;
        index = i;
        if (i > 0) break;
    }
    check(device.Get() != nullptr, "no test D3D11 device");

    auto fixture = LoadLibraryW((folder / "openxr_loader.dll").c_str());
    auto plugin = LoadLibraryW(pluginPath);
    check(fixture && plugin, "graphics test DLLs");
    Fixture state{scenario.c_str()};
    state.adapterLuid = desc.AdapterLuid;
    state.minimumLevel = device->GetFeatureLevel();
    state.expectedDevice = device.Get();
    if (scenario == "device-wrong-adapter") state.adapterLuid.HighPart ^= 0x40000000;
    if (scenario == "device-low-level") state.minimumLevel = D3D_FEATURE_LEVEL_12_2;
    entry<void (__cdecl*)(Fixture*)>(fixture, "BindPreflightFixture")(&state);
    auto select = entry<int (__cdecl*)()>(plugin, "AFTXR_SelectUnityAdapter");
    auto setSlot = entry<void (__cdecl*)(ID3D11Device* const*)>(plugin, "AFTXR_SetUnityDeviceSlot");
    auto ready = entry<int (__cdecl*)()>(plugin, "AFTXR_IsOpenXRSceneReady");
    auto status = entry<const char* (__cdecl*)()>(plugin, "AFTXR_GetLaunchStatus");
    auto finish = entry<void (__cdecl*)()>(plugin, "AFTXR_ReleaseLaunch");
    auto refs = [&] { device->AddRef(); return device->Release(); };
    auto before = refs();
    ID3D11Device* current = nullptr;
    check(select() == -1 && ready() == 0, "graphics accepted before preparation");
    check(entry<int (__cdecl*)()>(plugin, "AFTXR_PrepareLaunch")() == 1, "prepare graphics fixture");
    if (scenario == "device-wrong-adapter") {
        check(select() == -1 && strstr(status(), "unavailable"), "could not reject missing adapter");
        // Independently exercise the scene check with an actual device on the wrong GPU.
    } else {
        check(select() == static_cast<int>(index), "incorrect Unity adapter index");
    }

    // Unity owns the slot. Its contents at registration must never be acquired or cached.
    const bool missing = scenario == "device-null";
    current = missing ? device.Get() : nullptr;
    setSlot(&current);
    check(refs() == before, "took a Unity device reference before managed startup");
    current = missing ? nullptr : device.Get();
    if (scenario.find("stereo-") == 0) {
        const int accepted = ready();
        check(accepted == 1, status());
        if (scenario == "stereo-geometry") testHeadGeometry(plugin, state);
        else testFrames(plugin, device.Get(), state, scenario == "stereo-loss");
        finish();
        check(state.swapchainDestroys == state.swapchainCreates && state.sessionDestroys == 1 && state.destroys == 1 &&
              !state.invalidCall, "incorrect stereo lifetime or OpenXR call order");
        check(refs() == before, "device reference survived stereo cleanup");
        FreeLibrary(plugin);
        FreeLibrary(fixture);
        return;
    }
    check(ready() == 0, "scene opened before session creation");
    const bool valid = scenario == "device-match";
    const char* expected = "OpenXR camera rendering failed. xrCreateSession: XR_ERROR_GRAPHICS_DEVICE_INVALID (-38)";
    if (scenario == "device-single-thread") expected = "Unity created a single-threaded D3D11 device that cannot be shared with OpenXR.";
    if (missing) expected = "Unity did not provide a Direct3D 11 device for the headset.";
    if (scenario == "device-wrong-adapter") expected = "Unity created its Direct3D 11 device on a different GPU than the headset requires.";
    if (scenario == "device-low-level") expected = "Unity's Direct3D 11 device does not meet the headset's minimum feature level.";
    check(std::string(status()) == expected, "wrong managed startup diagnostic");
    check(state.sessionCreates == (valid ? 1u : 0u), "incorrect number of OpenXR session creations");
    check(refs() == before + (valid ? 1 : 0), "incorrect device ownership after validation");

    current = missing ? device.Get() : nullptr;
    check(ready() == 0 && std::string(status()) == expected, "repeated scene check changed its result");
    std::ifstream input(folder / "AwesomeForTheXR-launcher.log");
    std::string log{std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
    const char* marker = "Managed startup: validating Unity's D3D11 device.";
    check(log.find(marker) != log.npos && log.find(marker) == log.rfind(marker), "expected exactly one device check");
    finish();
    check(refs() == before, "device reference survived cleanup");
    check(state.destroys == 1 && !state.invalidCall && select() == -1, "graphics cleanup");
    FreeLibrary(plugin);
    FreeLibrary(fixture);
}

void run(const wchar_t* pluginPath, const std::filesystem::path& folder, const Expected& test) {
    // Hold the fixture while reading counters, then drop our reference to check
    // that the production plugin really released its own after startup cleanup.
    auto fixture = LoadLibraryW((folder / "openxr_loader.dll").c_str());
    auto plugin = LoadLibraryW(pluginPath);
    check(fixture && plugin, "could not load test fixture or production plugin");
    Fixture state{test.scenario};
    entry<void (__cdecl*)(Fixture*)>(fixture, "BindPreflightFixture")(&state);
    auto prepare = entry<int (__cdecl*)()>(plugin, "AFTXR_PrepareLaunch");
    auto finish = entry<void (__cdecl*)()>(plugin, "AFTXR_ReleaseLaunch");
    auto status = entry<const char* (__cdecl*)()>(plugin, "AFTXR_GetLaunchStatus");

    finish(); // Cleanup is safe even before any request.
    std::atomic<bool> start{false};
    std::atomic<int> matches{0};
    std::vector<std::thread> callers;
    for (int i = 0; i < 8; ++i) {
        callers.emplace_back([&] {
            while (!start.load()) std::this_thread::yield();
            if (prepare() == static_cast<int>(test.accepted)) ++matches;
        });
    }
    start = true;
    for (auto& caller : callers) caller.join();
    check(matches == 8 && state.creates == 1, "callers disagreed or created more than one instance");
    check(!state.invalidCall, "unexpected API arguments");
    check(state.destroys == static_cast<unsigned>(test.createdInstance && !test.accepted), "wrong instance lifetime after preparation");
    check(state.systemQueries == test.systemQueries, "wrong headset retry count");
    check(state.graphicsQueries == test.graphicsQueries, "wrong graphics query count");
    check(std::string(status()) == test.reason, "unexpected launch status");

    finish();
    finish();
    check(prepare() == 0 && state.creates == 1 && state.destroys == static_cast<unsigned>(test.createdInstance),
          "cleanup was repeated or preparation restarted after cleanup");
    check(!state.invalidCall, "invalid instance passed to cleanup");
    bool destroyFailed = std::string(test.scenario) == "destroy-failure";
    FreeLibrary(fixture);
    check((GetModuleHandleW(L"openxr_loader.dll") != nullptr) == destroyFailed, "unexpected loader lifetime");
    FreeLibrary(plugin);

    std::ifstream logFile(folder / "AwesomeForTheXR-launcher.log");
    std::string log{std::istreambuf_iterator<char>(logFile), std::istreambuf_iterator<char>()};
    check(log.find("PID " + std::to_string(GetCurrentProcessId()) + "\n") != std::string::npos, "missing fresh log");
    check(log.find(test.diagnostic) != log.npos, "missing expected OpenXR diagnostic");
    if (test.accepted)
        check(log.find("00001234:ABCD1234") != log.npos && log.find("0xB000") != log.npos, "wrong graphics data in log");
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        check(argc == 3, "expected plugin path and scenario");
        auto folder = std::filesystem::absolute(argv[0]).parent_path();
        auto scenario = std::filesystem::path(argv[2]).string();
        if (scenario == "no-headset") {
            testHeadsetTimeout();
            return 0;
        }
        if (scenario.find("device-") == 0 || scenario.find("stereo-") == 0) {
            testDevice(argv[1], folder, scenario);
            return 0;
        }
        for (const auto& test : cases) {
            if (scenario == test.scenario) {
                run(argv[1], folder, test);
                return 0;
            }
        }
        throw std::runtime_error("unknown preflight test case");
    } catch (const std::exception& e) {
        std::fprintf(stderr, "%s\n", e.what());
        return 1;
    }
}