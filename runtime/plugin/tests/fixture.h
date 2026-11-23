#pragma once
#include <openxr/openxr.h>

// Owned by the test process even after the plugin releases its loader reference.
struct Fixture {
    const char* scenario;
    unsigned creates = 0, systemQueries = 0, graphicsQueries = 0, destroys = 0;
    void* expectedDevice = nullptr;
    ID3D11Texture2D* eyeImages[2]{}; // Borrowed until xrDestroySwapchain.
    unsigned sessionCreates = 0;
    XrSessionState nextEvent = XR_SESSION_STATE_UNKNOWN;
    XrSessionState followingEvent = XR_SESSION_STATE_UNKNOWN;
    bool profileChanged = false;
    const char* handProfiles[2] = {"valve/index_controller", "khr/simple_controller"};
    unsigned starts = 0, stops = 0, frameWaits = 0, projections = 0, emptyFrames = 0;
    unsigned swapchainCreates = 0, swapchainDestroys = 0, releases = 0, sessionDestroys = 0;
    XrPosef head{{0, 0, 0, 1}, {0.1f, 1.4f, -0.2f}};
    XrSpaceLocationFlags headFlags = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    static constexpr XrSpaceLocationFlags trackedHand = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_POSITION_TRACKED_BIT |
                                                       XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    XrSpaceLocationFlags handFlags[2] = {trackedHand, trackedHand};
    unsigned actionSyncs = 0;
    bool menusActive = false, levelsActive = false, gameActive = false;
    bool levelStickActive = true;
    XrVector2f levelStick{-.85f, .7f};
    bool syncNotFocused = false;
    bool deployActive = true, deployHeld = false, syncedDeploy = false, deployChanged = false;
    struct Button { bool held = false, synced = false, changed = false, active = true; };
    Button pointerSelect[2]{{true}, {}}, thumbsUp[2], flipOff[2];
    unsigned handTicks[2]{};
    XrResult hapticResult = XR_SUCCESS;
    bool stickActive[2] = {true, true};
    XrVector2f sticks[2] = {{.65f, -.8f}, {-.4f, .3f}};
    bool handActive[2] = {true, true};
    XrViewStateFlags eyeFlags = XR_VIEW_STATE_POSITION_VALID_BIT | XR_VIEW_STATE_ORIENTATION_VALID_BIT;
    XrPosef eyePoses[2] = {{{0, .28f, 0, .96f}, {.06f, 1.41f, -.22f}},
                           {{0, -.6f, 0, .8f}, {.13f, 1.38f, -.19f}}};
    XrFovf eyeFovs[2] = {{-.9f, .6f, .8f, -.5f}, {-.45f, .95f, .65f, -.85f}};
    bool invalidCall = false;
    LUID adapterLuid{0xABCD1234, 0x1234};
    unsigned minimumLevel = 0xB000;
};

Fixture& currentFixture();
PFN_xrVoidFunction stereoFunction(const char* name);