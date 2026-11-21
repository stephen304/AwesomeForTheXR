#define WIN32_LEAN_AND_MEAN
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <openxr/openxr_platform.h>
#include <array>
#include <cstring>
#include "fixture.h"

namespace {
const XrSession session = (XrSession)1;
const XrSpace space = (XrSpace)2;
constexpr auto stereo = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
constexpr auto format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;

struct Eye {
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    bool acquired = false, waited = false;
};
std::array<Eye, 2> eyes;
bool running = false, frameOpen = false;
XrSessionState sessionState = XR_SESSION_STATE_IDLE;

void expect(bool condition) {
    if (!condition) currentFixture().invalidCall = true;
}

Eye& eye(XrSwapchain handle) {
    return eyes.at(static_cast<size_t>(handle - 1));
}
}

XRAPI_ATTR XrResult XRAPI_CALL xrCreateSession(XrInstance instance, const XrSessionCreateInfo* info, XrSession* out) {
    auto& fixture = currentFixture();
    const auto* binding = static_cast<const XrGraphicsBindingD3D11KHR*>(info->next);
    expect(instance == (XrInstance)42 && info->systemId == 84 && binding &&
           binding->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR && binding->device == fixture.expectedDevice);
    ++fixture.sessionCreates;
    if (std::strcmp(fixture.scenario, "device-match") == 0) return XR_ERROR_GRAPHICS_DEVICE_INVALID;
    *out = session;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrDestroySession(XrSession handle) {
    expect(handle == session && !frameOpen && !eyes[0].texture && !eyes[1].texture);
    ++currentFixture().sessionDestroys;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrCreateReferenceSpace(XrSession handle, const XrReferenceSpaceCreateInfo* info, XrSpace* out) {
    expect(handle == session && info->referenceSpaceType == XR_REFERENCE_SPACE_TYPE_LOCAL);
    *out = space;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrDestroySpace(XrSpace handle) {
    expect(handle == space);
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateViewConfigurationViews(XrInstance, XrSystemId, XrViewConfigurationType type,
    uint32_t capacity, uint32_t* count, XrViewConfigurationView* views) {
    *count = 2;
    expect(type == stereo);
    if (capacity == 0) return XR_SUCCESS;
    if (capacity < *count) return XR_ERROR_SIZE_INSUFFICIENT;
    for (unsigned i = 0; i < *count; ++i) {
        views[i].maxImageRectWidth = views[i].recommendedImageRectWidth = 2;
        views[i].maxImageRectHeight = views[i].recommendedImageRectHeight = 2;
        views[i].maxSwapchainSampleCount = views[i].recommendedSwapchainSampleCount = 1;
    }
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateEnvironmentBlendModes(XrInstance, XrSystemId, XrViewConfigurationType type,
    uint32_t capacity, uint32_t* count, XrEnvironmentBlendMode* modes) {
    expect(type == stereo);
    *count = 1;
    if (capacity) modes[0] = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateSwapchainFormats(XrSession handle, uint32_t capacity, uint32_t* count, int64_t* formats) {
    expect(handle == session);
    *count = 1;
    if (capacity) formats[0] = format;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrCreateSwapchain(XrSession handle, const XrSwapchainCreateInfo* info, XrSwapchain* out) {
    auto& fixture = currentFixture();
    expect(handle == session && info->usageFlags == XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT &&
           info->format == format && info->arraySize == 1 && info->sampleCount == 1);
    if (fixture.swapchainCreates >= eyes.size()) return XR_ERROR_LIMIT_REACHED;
    const unsigned i = fixture.swapchainCreates++;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = info->width;
    desc.Height = info->height;
    desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = format;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    auto* device = static_cast<ID3D11Device*>(fixture.expectedDevice);
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &eyes[i].texture))) return XR_ERROR_RUNTIME_FAILURE;
    *out = static_cast<XrSwapchain>(i + 1);
    fixture.eyeImages[i] = eyes[i].texture.Get();
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateSwapchainImages(XrSwapchain handle, uint32_t capacity, uint32_t* count,
    XrSwapchainImageBaseHeader* images) {
    *count = 1;
    if (capacity) {
        expect(images->type == XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR);
        reinterpret_cast<XrSwapchainImageD3D11KHR*>(images)->texture = eye(handle).texture.Get();
    }
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrDestroySwapchain(XrSwapchain handle) {
    auto& target = eye(handle);
    expect(!target.acquired && target.texture.Get());
    target.texture.Reset();
    currentFixture().eyeImages[handle - 1] = nullptr;
    ++currentFixture().swapchainDestroys;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrAcquireSwapchainImage(XrSwapchain handle, const XrSwapchainImageAcquireInfo*, uint32_t* out) {
    auto& target = eye(handle);
    expect(frameOpen && !target.acquired);
    target.acquired = true;
    *out = 0;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrWaitSwapchainImage(XrSwapchain handle, const XrSwapchainImageWaitInfo*) {
    auto& target = eye(handle);
    expect(target.acquired && !target.waited);
    target.waited = true;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrReleaseSwapchainImage(XrSwapchain handle, const XrSwapchainImageReleaseInfo*) {
    auto& target = eye(handle);
    expect(target.acquired && target.waited);
    target.acquired = target.waited = false;
    ++currentFixture().releases;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrPollEvent(XrInstance, XrEventDataBuffer* buffer) {
    auto& next = currentFixture().nextEvent;
    if (next == XR_SESSION_STATE_UNKNOWN) return XR_EVENT_UNAVAILABLE;
    sessionState = next;
    next = XR_SESSION_STATE_UNKNOWN;
    XrEventDataSessionStateChanged changed{XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED};
    changed.session = session;
    changed.state = sessionState;
    std::memcpy(buffer, &changed, sizeof(changed));
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrBeginSession(XrSession handle, const XrSessionBeginInfo* info) {
    expect(handle == session && sessionState == XR_SESSION_STATE_READY && !running && info->primaryViewConfigurationType == stereo);
    running = true;
    ++currentFixture().starts;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEndSession(XrSession handle) {
    expect(handle == session && sessionState == XR_SESSION_STATE_STOPPING && running && !frameOpen);
    running = false;
    ++currentFixture().stops;
    currentFixture().nextEvent = XR_SESSION_STATE_IDLE;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrRequestExitSession(XrSession handle) {
    expect(handle == session && running && sessionState != XR_SESSION_STATE_LOSS_PENDING);
    currentFixture().nextEvent = XR_SESSION_STATE_STOPPING;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrWaitFrame(XrSession handle, const XrFrameWaitInfo*, XrFrameState* frame) {
    expect(handle == session && running && !frameOpen);
    frame->predictedDisplayTime = ++currentFixture().frameWaits;
    frame->shouldRender = XR_TRUE;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrBeginFrame(XrSession handle, const XrFrameBeginInfo*) {
    expect(handle == session && running && !frameOpen);
    frameOpen = true;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrLocateViews(XrSession handle, const XrViewLocateInfo* info, XrViewState* tracking,
    uint32_t capacity, uint32_t* count, XrView* views) {
    expect(handle == session && frameOpen && info->space == space && info->displayTime == currentFixture().frameWaits);
    *count = 2;
    if (capacity < *count) return XR_ERROR_SIZE_INSUFFICIENT;
    tracking->viewStateFlags = XR_VIEW_STATE_POSITION_VALID_BIT | XR_VIEW_STATE_ORIENTATION_VALID_BIT;
    for (unsigned i = 0; i < *count; ++i) {
        views[i].pose.orientation.w = 1;
        views[i].pose.position.x = i ? 0.03f : -0.03f;
        views[i].fov = {-0.7f, 0.7f, 0.7f, -0.7f};
    }
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEndFrame(XrSession handle, const XrFrameEndInfo* info) {
    auto& fixture = currentFixture();
    expect(handle == session && frameOpen && info->displayTime == fixture.frameWaits);
    frameOpen = false;
    if (!info->layerCount) {
        ++fixture.emptyFrames;
        return XR_SUCCESS;
    }
    const auto* layer = reinterpret_cast<const XrCompositionLayerProjection*>(info->layers[0]);
    expect(info->layerCount == 1 && layer->type == XR_TYPE_COMPOSITION_LAYER_PROJECTION && layer->space == space);
    if (layer->viewCount != 2) return XR_ERROR_VALIDATION_FAILURE;
    for (unsigned i = 0; i < 2; ++i) {
        const auto& view = layer->views[i];
        expect(view.subImage.swapchain == i + 1 && !eyes[i].acquired &&
               view.subImage.imageRect.extent.width == 2 && view.subImage.imageRect.extent.height == 2 &&
               view.pose.position.x == (i ? 0.03f : -0.03f));
    }
    ++fixture.projections;
    return XR_SUCCESS;
}

PFN_xrVoidFunction stereoFunction(const char* name) {
#define MATCH(fn) if (std::strcmp(name, #fn) == 0) return reinterpret_cast<PFN_xrVoidFunction>(static_cast<PFN_##fn>(fn));
    MATCH(xrCreateSession)
    MATCH(xrDestroySession)
    MATCH(xrCreateReferenceSpace)
    MATCH(xrDestroySpace)
    MATCH(xrEnumerateViewConfigurationViews)
    MATCH(xrEnumerateEnvironmentBlendModes)
    MATCH(xrEnumerateSwapchainFormats)
    MATCH(xrCreateSwapchain)
    MATCH(xrEnumerateSwapchainImages)
    MATCH(xrDestroySwapchain)
    MATCH(xrAcquireSwapchainImage)
    MATCH(xrWaitSwapchainImage)
    MATCH(xrReleaseSwapchainImage)
    MATCH(xrPollEvent)
    MATCH(xrBeginSession)
    MATCH(xrEndSession)
    MATCH(xrRequestExitSession)
    MATCH(xrWaitFrame)
    MATCH(xrBeginFrame)
    MATCH(xrLocateViews)
    MATCH(xrEndFrame)
#undef MATCH
    return nullptr;
}
