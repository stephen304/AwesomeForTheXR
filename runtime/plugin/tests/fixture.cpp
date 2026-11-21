#define WIN32_LEAN_AND_MEAN
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <windows.h>
#include <d3d11.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <cstring>
#include "fixture.h"

namespace {
Fixture* state;
const XrInstance instance = (XrInstance)42;
constexpr XrSystemId systemId = 84;

bool scenario(const char* name) { return std::strcmp(state->scenario, name) == 0; }
void expect(bool condition) { if (!condition) state->invalidCall = true; }
}

Fixture& currentFixture() { return *state; }

extern "C" void __cdecl BindPreflightFixture(Fixture* fixture) { state = fixture; }

XRAPI_ATTR XrResult XRAPI_CALL xrCreateInstance(const XrInstanceCreateInfo* info, XrInstance* out) {
    ++state->creates;
    expect(info->type == XR_TYPE_INSTANCE_CREATE_INFO && !info->next && !info->enabledApiLayerCount &&
           info->applicationInfo.apiVersion == XR_MAKE_VERSION(1, 0, 0) &&
           info->enabledExtensionCount == 1 &&
           std::strcmp(info->enabledExtensionNames[0], XR_KHR_D3D11_ENABLE_EXTENSION_NAME) == 0);
    if (scenario("no-runtime")) return XR_ERROR_RUNTIME_UNAVAILABLE;
    if (scenario("no-extension")) return XR_ERROR_EXTENSION_NOT_PRESENT;
    if (scenario("no-api")) return XR_ERROR_API_VERSION_UNSUPPORTED;
    *out = instance;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrDestroyInstance(XrInstance handle) {
    expect(handle == instance);
    ++state->destroys;
    return scenario("destroy-failure") ? XR_ERROR_RUNTIME_FAILURE : XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrGetInstanceProperties(XrInstance handle, XrInstanceProperties* props) {
    expect(handle == instance && props->type == XR_TYPE_INSTANCE_PROPERTIES && !props->next);
    if (scenario("runtime-properties")) return XR_ERROR_RUNTIME_FAILURE;
    strcpy_s(props->runtimeName, "Fixture runtime");
    props->runtimeVersion = XR_MAKE_VERSION(1, 2, 3);
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrGetSystem(XrInstance handle, const XrSystemGetInfo* info, XrSystemId* out) {
    ++state->systemQueries;
    expect(handle == instance && info->type == XR_TYPE_SYSTEM_GET_INFO && !info->next &&
           info->formFactor == XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY);
    if (scenario("unsupported-headset")) return XR_ERROR_FORM_FACTOR_UNSUPPORTED;
    if (scenario("late-headset") && state->systemQueries < 3)
        return XR_ERROR_FORM_FACTOR_UNAVAILABLE;
    *out = systemId;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrGetSystemProperties(XrInstance handle, XrSystemId id, XrSystemProperties* props) {
    expect(handle == instance && id == systemId && props->type == XR_TYPE_SYSTEM_PROPERTIES && !props->next);
    if (scenario("system-properties")) return XR_ERROR_SYSTEM_INVALID;
    strcpy_s(props->systemName, "Fixture headset");
    return XR_SUCCESS;
}

static XrResult XRAPI_CALL graphics(XrInstance handle, XrSystemId id, XrGraphicsRequirementsD3D11KHR* req) {
    ++state->graphicsQueries;
    expect(handle == instance && id == systemId && req->type == XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR && !req->next);
    if (scenario("requirements")) return XR_ERROR_RUNTIME_FAILURE;
    req->adapterLuid = state->adapterLuid;
    req->minFeatureLevel = static_cast<D3D_FEATURE_LEVEL>(state->minimumLevel);
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrGetInstanceProcAddr(XrInstance handle, const char* name, PFN_xrVoidFunction* entry) {
    expect(handle == instance);
    if (std::strcmp(name, "xrGetD3D11GraphicsRequirementsKHR") == 0) {
        if (scenario("lookup")) return XR_ERROR_FUNCTION_UNSUPPORTED;
        *entry = scenario("null-entry") ? nullptr : reinterpret_cast<PFN_xrVoidFunction>(graphics);
    } else {
        *entry = stereoFunction(name);
        if (!*entry) return XR_ERROR_FUNCTION_UNSUPPORTED;
    }
    return XR_SUCCESS;
}
