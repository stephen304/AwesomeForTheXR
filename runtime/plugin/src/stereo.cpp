#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include "stereo.h"
#include "Fullscreen.h"
#include "FlipEye.h"
#include <openxr/openxr_reflection.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <stdexcept>
#include <string>

namespace {
constexpr auto stereo = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
constexpr auto validPose = XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT;

void requireXR(const char* operation, XrResult result) {
    if (result == XR_SUCCESS) return;
    const char* name = "unknown XrResult";
    switch (result) {
#define AFTXR_RESULT(id, value) case id: name = #id; break;
        XR_LIST_ENUM_XrResult(AFTXR_RESULT)
#undef AFTXR_RESULT
        default: break;
    }
    throw std::runtime_error(std::string(operation) + ": " + name + " (" + std::to_string(result) + ")");
}

void requireHR(const char* operation, HRESULT result) {
    if (SUCCEEDED(result)) return;
    char code[16];
    std::snprintf(code, sizeof(code), "0x%08lX", static_cast<DWORD>(result));
    throw std::runtime_error(std::string(operation) + ": HRESULT " + code);
}

const char* stateName(XrSessionState state) {
    switch (state) {
#define AFTXR_STATE(id, value) case id: return #id;
        XR_LIST_ENUM_XrSessionState(AFTXR_STATE)
#undef AFTXR_STATE
        default: return "unknown";
    }
}

DXGI_FORMAT colorFormat(DXGI_FORMAT source, const std::vector<int64_t>& formats) {
    DXGI_FORMAT gamma, linear;
    switch (source) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        gamma = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB; linear = DXGI_FORMAT_R8G8B8A8_UNORM; break;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        gamma = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB; linear = DXGI_FORMAT_B8G8R8A8_UNORM; break;
    default: throw std::runtime_error("Unsupported Unity eye texture format " + std::to_string(source));
    }
    for (auto format : {gamma, linear})
        if (std::find(formats.begin(), formats.end(), format) != formats.end()) return format;
    throw std::runtime_error("OpenXR offers no swapchain format compatible with the Unity eye texture.");
}
}
void StereoSession::open(PFN_xrGetInstanceProcAddr getProc, XrInstance xr, XrSystemId system,
                         const XrGraphicsBindingD3D11KHR& binding, FILE* output) {
    instance = xr;
    log = output;
    auto resolve = [&](const char* name) {
        PFN_xrVoidFunction fn = nullptr;
        requireXR(name, getProc(instance, name, &fn));
        if (!fn) throw std::runtime_error(std::string("Missing OpenXR function: ") + name);
        return fn;
    };
#define AFTXR_LOAD(name) name = reinterpret_cast<PFN_xr##name>(resolve("xr" #name));
    AFTXR_STEREO_FUNCTIONS(AFTXR_LOAD)
#undef AFTXR_LOAD

    XrSessionCreateInfo info{XR_TYPE_SESSION_CREATE_INFO};
    info.next = &binding;
    info.systemId = system;
    if (log) fprintf(log, "xrCreateSession on Unity's validated D3D11 device...\n");
    requireXR("xrCreateSession", CreateSession(instance, &info, &session));
    if (log) fprintf(log, "xrCreateSession: XR_SUCCESS\n");
    XrReferenceSpaceCreateInfo ref{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    ref.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    ref.poseInReferenceSpace.orientation.w = 1;
    requireXR("xrCreateReferenceSpace(LOCAL)", CreateReferenceSpace(session, &ref, &localSpace));
    ref.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    requireXR("xrCreateReferenceSpace(VIEW)", CreateReferenceSpace(session, &ref, &viewSpace));
    openInput();
    uint32_t count = 0;
    std::array<XrViewConfigurationView,2> config{{{XR_TYPE_VIEW_CONFIGURATION_VIEW},{XR_TYPE_VIEW_CONFIGURATION_VIEW}}};
    requireXR("xrEnumerateViewConfigurationViews", EnumerateViewConfigurationViews(instance, system, stereo, 2, &count, config.data()));
    if (count != 2) throw std::runtime_error("The runtime did not provide two primary stereo views.");
    requireXR("xrEnumerateEnvironmentBlendModes", EnumerateEnvironmentBlendModes(instance, system, stereo, 0, &count, nullptr));
    std::vector<XrEnvironmentBlendMode> modes(count);
    requireXR("xrEnumerateEnvironmentBlendModes", EnumerateEnvironmentBlendModes(instance, system, stereo, count, &count, modes.data()));
    if (!count) throw std::runtime_error("No stereo environment blend mode.");
    blend = modes.front();
    requireXR("xrEnumerateSwapchainFormats", EnumerateSwapchainFormats(session, 0, &count, nullptr));
    formats.resize(count);
    requireXR("xrEnumerateSwapchainFormats", EnumerateSwapchainFormats(session, count, &count, formats.data()));
    device = binding.device;
    device->GetImmediateContext(&context);
    requireHR("Create eye copy context", device->CreateDeferredContext(0, &deferred));
    requireHR("Create eye vertex shader", device->CreateVertexShader(FullscreenCode, sizeof(FullscreenCode), nullptr, &fullscreen));
    requireHR("Create eye pixel shader", device->CreatePixelShader(FlipEyeCode, sizeof(FlipEyeCode), nullptr, &flip));
    for (unsigned i=0; i<2; ++i) {
        auto& v = config[i];
        // Bound the memory occupied by two Unity render targets in this Win32 player.
        float scale = std::min(1.f, 1600.f / std::max(v.recommendedImageRectWidth, v.recommendedImageRectHeight));
        eyes[i].recommended = {std::max(1, int(v.recommendedImageRectWidth * scale)),
                               std::max(1, int(v.recommendedImageRectHeight * scale))};
    }
    if (log) fprintf(log, "OpenXR session ready for Unity eye cameras.\n");
}

void StereoSession::openInput() {
    auto path = [&](const std::string& name) {
        XrPath value{};
        requireXR("xrStringToPath", StringToPath(instance, name.c_str(), &value));
        return value;
    };
    auto createSet = [&](const char* name, const char* label, XrActionSet& handle) {
        XrActionSetCreateInfo info{XR_TYPE_ACTION_SET_CREATE_INFO};
        strcpy_s(info.actionSetName, name);
        strcpy_s(info.localizedActionSetName, label);
        requireXR("xrCreateActionSet", CreateActionSet(instance, &info, &handle));
    };
    createSet("menus", "Menus", menuSet);
    createSet("game", "Game controls", gameSet);
    const char* users[] = {"/user/hand/left", "/user/hand/right"};
    XrPath paths[] = {path(users[0]), path(users[1])};
    auto action = [&](XrActionSet owner, XrActionType type, const char* name, const char* label, XrAction& handle, bool perHand = false) {
        XrActionCreateInfo info{XR_TYPE_ACTION_CREATE_INFO};
        info.actionType = type;
        strcpy_s(info.actionName, name);
        strcpy_s(info.localizedActionName, label);
        // Only the pointer queries each hand independently. Other actions allow rebinding to either hand.
        if (perHand) {
            info.countSubactionPaths = 2;
            info.subactionPaths = paths;
        }
        requireXR("xrCreateAction", CreateAction(owner, &info, &handle));
    };
    action(menuSet, XR_ACTION_TYPE_POSE_INPUT, "aim", "Point at menu", aim, true);
    action(menuSet, XR_ACTION_TYPE_BOOLEAN_INPUT, "pointer_select", "Select with pointer / switch hand", pointerSelect, true);
    action(gameSet, XR_ACTION_TYPE_BOOLEAN_INPUT, "deploy_parachute", "Deploy parachute", deploy);
    action(gameSet, XR_ACTION_TYPE_VECTOR2F_INPUT, "move", "Move or navigate levels", move);
    action(gameSet, XR_ACTION_TYPE_VECTOR2F_INPUT, "turn", "Turn", turn);
    struct Profile { const char *name, *button, *stick; bool hasRightA = false; };
    for (const Profile profile : {
        Profile{"khr/simple_controller", "select/click", nullptr}, {"oculus/touch_controller", "trigger/value", "thumbstick", true},
        {"valve/index_controller", "trigger/value", "thumbstick", true}, {"htc/vive_controller", "trigger/click", "trackpad"},
        {"microsoft/motion_controller", "trigger/value", "thumbstick"}}) {
        std::vector<XrActionSuggestedBinding> bindings;
        for (int i = 0; i < 2; ++i) {
            std::string input = std::string(users[i]) + "/input/";
            bindings.push_back({aim, path(input + "aim/pose")});
            bindings.push_back({pointerSelect, path(input + profile.button)});
            if (profile.stick) bindings.push_back({i == 0 ? move : turn, path(input + profile.stick)});
        }
        if (profile.hasRightA) {
            bindings.push_back({deploy, path("/user/hand/right/input/a/click")});
        }
        XrInteractionProfileSuggestedBinding info{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        info.interactionProfile = path(std::string("/interaction_profiles/") + profile.name);
        info.countSuggestedBindings = static_cast<uint32_t>(bindings.size());
        info.suggestedBindings = bindings.data();
        auto result = SuggestInteractionProfileBindings(instance, &info);
        if (result != XR_ERROR_PATH_UNSUPPORTED) requireXR("xrSuggestInteractionProfileBindings", result);
    }
    const XrActionSet sets[] = {menuSet, gameSet};
    XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
    attach.countActionSets = 2;
    attach.actionSets = sets;
    requireXR("xrAttachSessionActionSets", AttachSessionActionSets(session, &attach));
    for (int i = 0; i < 2; ++i) {
        hands[i].path = paths[i];
        XrActionSpaceCreateInfo info{XR_TYPE_ACTION_SPACE_CREATE_INFO};
        info.action = aim;
        info.subactionPath = paths[i];
        info.poseInActionSpace.orientation.w = 1;
        requireXR("xrCreateActionSpace", CreateActionSpace(session, &info, &hands[i].space));
    }
    if (log) fprintf(log, "OpenXR pointer, stick and parachute actions attached.\n");
}

bool StereoSession::syncInput(int unityFrame) {
    if (!running || finished || state != XR_SESSION_STATE_FOCUSED) return false;
    if (inputFrame == unityFrame) return actionsActive;
    // A press during a gap in sampling must not be carried into a later Unity frame.
    if (static_cast<int64_t>(unityFrame) - inputFrame != 1) interruptDeploy();
    inputFrame = unityFrame;
    actionsActive = false;
    const XrActiveActionSet active[] = {{menuSet, XR_NULL_PATH}, {gameSet, XR_NULL_PATH}};
    XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO};
    sync.countActiveActionSets = 2;
    sync.activeActionSets = active;
    const auto result = SyncActions(session, &sync);
    if (result == XR_SESSION_NOT_FOCUSED) {
        interruptDeploy();
        for (auto& hand : hands) hand.interrupt();
        return false;
    }
    requireXR("xrSyncActions", result);
    auto readStick = [&](XrAction handle) {
        XrActionStateGetInfo get{XR_TYPE_ACTION_STATE_GET_INFO};
        get.action = handle;
        XrActionStateVector2f value{XR_TYPE_ACTION_STATE_VECTOR2F};
        requireXR("xrGetActionStateVector2f", GetActionStateVector2f(session, &get, &value));
        return value.isActive ? value.currentState : XrVector2f{};
    };
    movementStick = readStick(move);
    turnStick = readStick(turn);
    XrActionStateGetInfo get{XR_TYPE_ACTION_STATE_GET_INFO};
    get.action = deploy;
    XrActionStateBoolean value{XR_TYPE_ACTION_STATE_BOOLEAN};
    requireXR("xrGetActionStateBoolean", GetActionStateBoolean(session, &get, &value));
    deployPressed = deployReleased && value.isActive && value.changedSinceLastSync && value.currentState;
    deployReleased = value.isActive && !value.currentState;
    actionsActive = true;
    return true;
}

void StereoSession::interruptDeploy() {
    deployPressed = deployReleased = false;
}

bool StereoSession::deployParachutePressed(int unityFrame) {
    return syncInput(unityFrame) && deployPressed;
}

bool StereoSession::mergeSticks(int unityFrame, XrVector2f& movement, XrVector2f& look) {
    if (!syncInput(unityFrame)) return false;
    auto largestAxis = [](XrVector2f v) { return std::max(std::abs(v.x), std::abs(v.y)); };
    auto lengthSquared = [](XrVector2f v) { return v.x*v.x + v.y*v.y; };
    // A physical stick inside the game's digital threshold contributes no movement.
    const float padStrength = largestAxis(movement);
    const bool useXrMovement = largestAxis(movementStick) > (padStrength > .5f ? padStrength : 0.f);
    if (useXrMovement) movement = movementStick;
    if (lengthSquared(turnStick) > lengthSquared(look)) look = turnStick;
    return useXrMovement;
}

void StereoSession::samplePointer() {
    for (auto& hand : hands) {
        if (profilesDirty) {
            XrInteractionProfileState current{XR_TYPE_INTERACTION_PROFILE_STATE};
            requireXR("xrGetCurrentInteractionProfile", GetCurrentInteractionProfile(session, hand.path, &current));
            // Keep tracking history across a temporary absence of bindings for this hand.
            if (current.interactionProfile && current.interactionProfile != hand.lastProfile) {
                if (hand.lastProfile) {
                    hand.hasTrackedPosition = false;
                    hand.interrupt();
                }
                hand.lastProfile = current.interactionProfile;
            }
        }
        if (readPointerHand(hand)) hand.usable = true;
        else if (hand.usable) hand.interrupt(); // Preserve the loss even if Unity skips this frame.
    }
    profilesDirty = false;
}

bool StereoSession::readPointerHand(Hand& hand) {
    XrActionStateGetInfo get{XR_TYPE_ACTION_STATE_GET_INFO};
    get.subactionPath = hand.path;
    get.action = aim;
    XrActionStatePose tracked{XR_TYPE_ACTION_STATE_POSE};
    requireXR("xrGetActionStatePose", GetActionStatePose(session, &get, &tracked));
    if (!tracked.isActive) return false;
    XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
    requireXR("xrLocateSpace(aim)", LocateSpace(hand.space, localSpace, displayTime, &loc));
    const bool positionTracked = (loc.locationFlags & XR_SPACE_LOCATION_POSITION_TRACKED_BIT) != 0;
    if (positionTracked) hand.hasTrackedPosition = true;
    // Rotation-only controllers can supply a valid estimated origin. If this active
    // source tracked its position, losing that tracking cancels selection until it returns.
    constexpr auto required = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT |
                              XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    if ((loc.locationFlags & required) != required || (hand.hasTrackedPosition && !positionTracked)) return false;
    get.action = pointerSelect;
    XrActionStateBoolean button{XR_TYPE_ACTION_STATE_BOOLEAN};
    requireXR("xrGetActionStateBoolean", GetActionStateBoolean(session, &get, &button));
    if (!button.isActive) return false;
    hand.pose = loc.pose;
    hand.pressed = button.currentState != XR_FALSE;
    return true;
}

int StereoSession::pointer(int index, float* p, uint32_t& generation) const {
    if (unsigned(index) >= hands.size()) return 0;
    generation = hands[index].generation;
    if (!hands[index].usable) return 0;
    unityPose(hands[index].pose, p);
    return hands[index].pressed ? 3 : 1;
}

void StereoSession::makeEye(Eye& eye, const D3D11_TEXTURE2D_DESC& desc) {
    eye.copies.clear();
    if (eye.chain) {
        requireXR("xrDestroySwapchain", DestroySwapchain(eye.chain));
        eye.chain = XR_NULL_HANDLE;
        eye.images.clear();
    }
    eye.format = colorFormat(desc.Format, formats);
    XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT;
    info.format = eye.format;
    info.width = desc.Width;
    info.height = desc.Height;
    info.faceCount = info.arraySize = info.mipCount = info.sampleCount = 1;
    requireXR("xrCreateSwapchain", CreateSwapchain(session, &info, &eye.chain));
    eye.size = {static_cast<int32_t>(info.width), static_cast<int32_t>(info.height)};
    uint32_t count = 0;
    requireXR("xrEnumerateSwapchainImages", EnumerateSwapchainImages(eye.chain, 0, &count, nullptr));
    if (!count) throw std::runtime_error("No images in the OpenXR eye swapchain.");
    eye.images.resize(count, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    requireXR("xrEnumerateSwapchainImages", EnumerateSwapchainImages(eye.chain, count, &count,
              reinterpret_cast<XrSwapchainImageBaseHeader*>(eye.images.data())));
}

// Record once per Unity target / swapchain image combination. The command list
// restores Unity's immediate-context state when executed at the end of a frame.
void StereoSession::recordCopies(Eye& eye, const D3D11_TEXTURE2D_DESC& sourceDesc) {
    eye.copies.clear();
    Microsoft::WRL::ComPtr<ID3D11Texture2D> intermediate;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> srv;
    D3D11_SHADER_RESOURCE_VIEW_DESC view{};
    view.Format = eye.format;
    view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    view.Texture2D.MipLevels = 1;
    if (sourceDesc.SampleDesc.Count != 1 || !(sourceDesc.BindFlags & D3D11_BIND_SHADER_RESOURCE) ||
        FAILED(device->CreateShaderResourceView(eye.source.Get(), &view, &srv))) {
        D3D11_TEXTURE2D_DESC single{};
        single.Width = sourceDesc.Width; single.Height = sourceDesc.Height;
        single.MipLevels = single.ArraySize = single.SampleDesc.Count = 1;
        single.Format = eye.format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || eye.format == DXGI_FORMAT_R8G8B8A8_UNORM
            ? DXGI_FORMAT_R8G8B8A8_TYPELESS : DXGI_FORMAT_B8G8R8A8_TYPELESS;
        single.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        requireHR("Create eye resolve target", device->CreateTexture2D(&single, nullptr, &intermediate));
        requireHR("Create resolved eye view", device->CreateShaderResourceView(intermediate.Get(), &view, &srv));
    }
    D3D11_VIEWPORT viewport{0, 0, float(eye.size.width), float(eye.size.height), 0, 1};
    for (const auto& image : eye.images) {
        D3D11_RENDER_TARGET_VIEW_DESC target{};
        target.Format = eye.format;
        target.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> rtv;
        requireHR("Create OpenXR render target", device->CreateRenderTargetView(image.texture, &target, &rtv));
        if (intermediate) {
            if (sourceDesc.SampleDesc.Count > 1) {
                auto fmt = sourceDesc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS || sourceDesc.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS
                    ? eye.format : sourceDesc.Format;
                deferred->ResolveSubresource(intermediate.Get(), 0, eye.source.Get(), 0, fmt);
            } else deferred->CopySubresourceRegion(intermediate.Get(), 0, 0, 0, 0, eye.source.Get(), 0, nullptr);
        }
        deferred->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        deferred->VSSetShader(fullscreen.Get(), nullptr, 0);
        deferred->PSSetShader(flip.Get(), nullptr, 0);
        deferred->PSSetShaderResources(0, 1, srv.GetAddressOf());
        deferred->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
        deferred->RSSetViewports(1, &viewport);
        deferred->Draw(3, 0);
        Microsoft::WRL::ComPtr<ID3D11CommandList> commands;
        requireHR("Record eye copy", deferred->FinishCommandList(FALSE, &commands));
        eye.copies.push_back(commands);
    }
}

void StereoSession::renderedEye(unsigned i, IUnknown* incoming, int unityFrame) {
    if (!begun || !valid || unityFrame != frameNumber || !incoming || i >= eyes.size()) return;
    auto& eye = eyes[i];
    if (incoming != eye.source.Get()) {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
        requireHR("Unity eye texture interface", incoming->QueryInterface(IID_PPV_ARGS(&texture)));
        Microsoft::WRL::ComPtr<ID3D11Device> owner;
        texture->GetDevice(&owner);
        if (owner.Get() != device.Get()) throw std::runtime_error("Unity eye texture belongs to another graphics device.");
        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        if (desc.ArraySize != 1) throw std::runtime_error("Unexpected Unity eye texture array.");
        if (!eye.chain || int(desc.Width) != eye.size.width || int(desc.Height) != eye.size.height ||
            colorFormat(desc.Format, formats) != eye.format) makeEye(eye, desc);
        eye.source = texture;
        recordCopies(eye, desc);
        if (log) fprintf(log, "Unity %s eye: %ux%u format %u, swapchain %u, samples %u.\n",
                         i ? "right" : "left", desc.Width, desc.Height, desc.Format, eye.format, desc.SampleDesc.Count);
    }
    renderedEyes |= 1u << i;
    if (renderedEyes == 3 && eyes[0].source.Get() == eyes[1].source.Get())
        throw std::runtime_error("Both Unity cameras rendered to the same texture.");
}

void StereoSession::draw(Eye& eye) {
    XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wait.timeout = 1000000000; // A stalled runtime must not hang Unity's renderer indefinitely.
    uint32_t index = 0;
    requireXR("xrAcquireSwapchainImage", AcquireSwapchainImage(eye.chain, &acquire, &index));
    requireXR("xrWaitSwapchainImage", WaitSwapchainImage(eye.chain, &wait));
    if (index >= eye.copies.size()) throw std::runtime_error("Invalid OpenXR eye image index.");
    context->ExecuteCommandList(eye.copies[index].Get(), TRUE);
    XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    requireXR("xrReleaseSwapchainImage", ReleaseSwapchainImage(eye.chain, &release));
}

bool StereoSession::begin(int unityFrame) {
    if (frameNumber == unityFrame) return !finished;
    if (begun) end(false); // A scene change can remove the end-of-frame coroutine.
    poll(false);
    frameNumber = unityFrame;
    valid = false;
    renderedEyes = 0;
    if (!running || finished) return !finished;
    XrFrameWaitInfo wait{XR_TYPE_FRAME_WAIT_INFO};
    XrFrameState timing{XR_TYPE_FRAME_STATE};
    requireXR("xrWaitFrame", WaitFrame(session, &wait, &timing));
    XrFrameBeginInfo info{XR_TYPE_FRAME_BEGIN_INFO};
    auto result = BeginFrame(session, &info);
    if (result != XR_FRAME_DISCARDED) requireXR("xrBeginFrame", result);
    begun = true;
    displayTime = timing.predictedDisplayTime;
    if (syncInput(unityFrame)) samplePointer();
    if (!timing.shouldRender) return true;
    XrViewLocateInfo locate{XR_TYPE_VIEW_LOCATE_INFO};
    locate.viewConfigurationType = stereo;
    locate.displayTime = displayTime;
    locate.space = localSpace;
    XrViewState tracking{XR_TYPE_VIEW_STATE};
    uint32_t count = 0;
    std::array<XrView,2> next{{{XR_TYPE_VIEW},{XR_TYPE_VIEW}}};
    requireXR("xrLocateViews", LocateViews(session, &locate, &tracking, 2, &count, next.data()));
    valid = count == 2 && (tracking.viewStateFlags & validPose) == validPose;
    if (valid) {
        views = next;
        haveViews = true;
    }

    XrSpaceLocation head{XR_TYPE_SPACE_LOCATION};
    requireXR("xrLocateSpace(VIEW in LOCAL)", LocateSpace(viewSpace, localSpace, displayTime, &head));
    constexpr auto validHead = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    // A runtime may infer either component. Keep its last usable value during tracking loss.
    if (head.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) headPose.orientation = head.pose.orientation;
    if (head.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) headPose.position = head.pose.position;
    if (recenterPending && (head.locationFlags & validHead) == validHead) {
        center = headPose.position;
        const auto& q = headPose.orientation;
        const float x = 2 * (q.x*q.z + q.w*q.y);
        const float z = 1 - 2 * (q.x*q.x + q.y*q.y);
        // The forward axis has no heading at a vertical gaze. Use the horizontal right axis there.
        centerYaw = x*x + z*z > 1.e-6f ? std::atan2(x, z)
            : std::atan2(2 * (q.w*q.y - q.x*q.z), 1 - 2 * (q.y*q.y + q.z*q.z));
        recenterPending = false;
    }
    return true;
}

void StereoSession::describe(int i, float* d) const {
    d[0] = float(eyes[i].recommended.width); d[1] = float(eyes[i].recommended.height);
    d[2] = d[3] = 90.f;
    if (haveViews) {
        auto f = views[i].fov;
        d[2] = (f.angleRight-f.angleLeft)*57.29578f; d[3] = (f.angleUp-f.angleDown)*57.29578f;
    }
}
void StereoSession::pose(int anchor, float* p) const {
    std::fill_n(p, 7, 0.f); p[6] = 1;
    if (anchor != headAnchor && !haveViews) return;
    unityPose(anchor == headAnchor ? headPose : views[anchor].pose, p);
}

void StereoSession::unityPose(const XrPosef& raw, float* p) const {
    float c = std::cos(centerYaw), s = std::sin(centerYaw);
    const float x = raw.position.x-center.x, z = raw.position.z-center.z;
    p[0] = c*x-s*z; p[1] = raw.position.y-center.y; p[2] = -(s*x+c*z);
    auto q = raw.orientation;
    c = std::cos(-centerYaw*.5f); s = std::sin(-centerYaw*.5f);
    p[3] = -(c*q.x+s*q.z); p[4] = -(c*q.y+s*q.w); p[5] = c*q.z-s*q.x; p[6] = c*q.w-s*q.y;
}
void StereoSession::projection(int i, float n, float f, float* m) const {
    std::fill_n(m, 16, 0.f);
    float l=-1,r=1,b=-1,t=1;
    if (haveViews) {
        auto v=views[i].fov;
        l=std::tan(v.angleLeft); r=std::tan(v.angleRight); b=std::tan(v.angleDown); t=std::tan(v.angleUp);
    }
    // Unity camera projection uses OpenGL-style clip depth and column-major Matrix4x4 storage.
    m[0]=2/(r-l); m[5]=2/(t-b); m[8]=(r+l)/(r-l); m[9]=(t+b)/(t-b);
    m[10]=-(f+n)/(f-n); m[11]=-1; m[14]=-2*f*n/(f-n);
}

void StereoSession::end(bool images) {
    if (!begun) return;
    XrFrameEndInfo info{XR_TYPE_FRAME_END_INFO};
    info.displayTime = displayTime;
    info.environmentBlendMode = blend;
    XrCompositionLayerProjection layer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    auto* header = reinterpret_cast<const XrCompositionLayerBaseHeader*>(&layer);
    std::array<XrCompositionLayerProjectionView,2> out{};
    if (images && valid && renderedEyes == 3) {
        for (unsigned i=0;i<2;++i) {
            draw(eyes[i]);
            out[i].type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
            out[i].pose = views[i].pose; out[i].fov = views[i].fov;
            out[i].subImage.swapchain = eyes[i].chain; out[i].subImage.imageRect.extent = eyes[i].size;
        }
        layer.space = localSpace; layer.viewCount = 2; layer.views = out.data();
        info.layerCount = 1; info.layers = &header;
    }
    begun = false;
    requireXR("xrEndFrame", EndFrame(session, &info));
    ++frames;
    if (info.layerCount) {
        ++submitted;
        if (submitted == 1 || submitted % 600 == 0)
            requireHR("Unity camera device", device->GetDeviceRemovedReason());
        if (submitted == 1 && log)
            fprintf(log, "Submitted camera projection 1 (Unity frame %d).\n", frameNumber);
    }
}
void StereoSession::submit() { end(true); }

void StereoSession::poll(bool shuttingDown) {
    for (;;) {
        XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
        XrResult result = PollEvent(instance, &event);
        if (result == XR_EVENT_UNAVAILABLE) return;
        requireXR("xrPollEvent", result);
        if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) {
            finished = true;
            throw std::runtime_error("OpenXR reported instance loss. Check SteamVR and restart the game.");
        }
        if (event.type == XR_TYPE_EVENT_DATA_INTERACTION_PROFILE_CHANGED &&
            reinterpret_cast<const XrEventDataInteractionProfileChanged&>(event).session == session) {
            profilesDirty = true;
            interruptDeploy();
        }
        if (event.type != XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) continue;
        auto& change = reinterpret_cast<const XrEventDataSessionStateChanged&>(event);
        if (change.session != session) continue;
        // poll() can collect both loss and regain before Unity reads either hand.
        if (state == XR_SESSION_STATE_FOCUSED && change.state != state) {
            actionsActive = false;
            interruptDeploy();
            for (auto& hand : hands) hand.interrupt();
        }
        state = change.state;
        if (log) fprintf(log, "Session state: %s\n", stateName(state));
        if (state == XR_SESSION_STATE_READY && !running && !shuttingDown) {
            XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO};
            begin.primaryViewConfigurationType = stereo;
            requireXR("xrBeginSession", BeginSession(session,&begin));
            running = true;
        } else if (state == XR_SESSION_STATE_STOPPING) {
            if (running) requireXR("xrEndSession", EndSession(session));
            // The runtime can resume this session with a later READY event.
            running = false;
        } else if (state == XR_SESSION_STATE_EXITING) {
            finished = true;
        } else if (state == XR_SESSION_STATE_LOSS_PENDING) {
            finished = true;
            throw std::runtime_error("The OpenXR session was lost. Check SteamVR and restart the game.");
        }
    }
}

void StereoSession::close() noexcept {
    if (!session) return;
    if (running && !finished) {
        try {
            if (begun) end(false);
            requireXR("xrRequestExitSession", RequestExitSession(session));
            auto deadline = GetTickCount64() + 2000;
            while (running && !finished && GetTickCount64() < deadline) {
                poll(true);
                if (running && !finished) Sleep(5);
            }
        } catch (const std::exception& e) {
            if (log) fprintf(log, "Session shutdown: %s\n", e.what());
        }
    }
    for (auto& eye : eyes) {
        eye.copies.clear();
        eye.source.Reset();
        eye.images.clear();
        if (eye.chain) DestroySwapchain(eye.chain);
        eye.chain = XR_NULL_HANDLE;
    }
    context.Reset();
    for (auto& hand : hands) if (hand.space) DestroySpace(hand.space);
    if (viewSpace) DestroySpace(viewSpace);
    if (localSpace) DestroySpace(localSpace);
    viewSpace = localSpace = XR_NULL_HANDLE;
    XrResult result = DestroySession(session);
    if (gameSet) DestroyActionSet(gameSet);
    if (menuSet) DestroyActionSet(menuSet);
    if (log) fprintf(log, "xrDestroySession: %d; %u stereo projections in %u frames.\n",
                     static_cast<int>(result), submitted, frames);
    session = XR_NULL_HANDLE;
}
