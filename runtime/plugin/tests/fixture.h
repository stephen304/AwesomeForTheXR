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
    unsigned starts = 0, stops = 0, frameWaits = 0, projections = 0, emptyFrames = 0;
    unsigned swapchainCreates = 0, swapchainDestroys = 0, releases = 0, sessionDestroys = 0;
    bool invalidCall = false;
    LUID adapterLuid{0xABCD1234, 0x1234};
    unsigned minimumLevel = 0xB000;
};

Fixture& currentFixture();
PFN_xrVoidFunction stereoFunction(const char* name);