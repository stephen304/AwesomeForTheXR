#define WIN32_LEAN_AND_MEAN
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <openxr/openxr_platform.h>
#include <array>
#include <algorithm>
#include <map>
#include <set>
#include <string>
#include <cstring>
#include "fixture.h"

namespace {
const XrSession session = (XrSession)1;
const XrSpace localSpace = (XrSpace)2, viewSpace = (XrSpace)3;
bool localCreated = false, viewCreated = false;
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

namespace {
const XrActionSet menuSet = (XrActionSet)14;
const XrActionSet gameSet = (XrActionSet)17, levelsSet = (XrActionSet)26;
const XrAction navigate = (XrAction)27, pointerHaptic = (XrAction)28;
const XrAction aim = (XrAction)15, selectButton = (XrAction)16, move = (XrAction)18, turn = (XrAction)19;
const XrAction deploy = (XrAction)20, thumbsUp = (XrAction)21, flipOff = (XrAction)22, pressHaptic = (XrAction)25;
const char* const users[] = {"/user/hand/left", "/user/hand/right"};
const XrPath handPaths[] = {1, 2};
std::map<std::string, XrPath> paths{{users[0], handPaths[0]}, {users[1], handPaths[1]}};
XrPath pathId(const std::string& name) {
    return paths.emplace(name, paths.size() + 1).first->second;
}
int handIndex(XrPath path) {
    expect(path == handPaths[0] || path == handPaths[1]);
    return path == handPaths[1] ? 1 : 0;
}
const std::map<std::string, std::string> profiles = {
    {"khr/simple_controller", "select/click"}, {"htc/vive_controller", "trigger/click"},
    {"valve/index_controller", "trigger/value"}, {"oculus/touch_controller", "trigger/value"},
    {"microsoft/motion_controller", "trigger/value"}
};
std::set<XrPath> suggested;
const XrSpace handSpaces[] = {(XrSpace)23,(XrSpace)24};
bool attached = false;
}

XRAPI_ATTR XrResult XRAPI_CALL xrStringToPath(XrInstance, const char* name, XrPath* out) {
    *out = pathId(name);
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrCreateActionSet(XrInstance, const XrActionSetCreateInfo* info, XrActionSet* out) {
    const bool menus = !strcmp(info->actionSetName,"menus");
    const bool levels = !strcmp(info->actionSetName, "levels");
    expect(menus || levels || !strcmp(info->actionSetName, "game"));
    *out = menus ? menuSet : levels ? levelsSet : gameSet;
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrDestroyActionSet(XrActionSet set) {
    expect(set == menuSet || set == levelsSet || set == gameSet);
    attached = false;
    suggested.clear();
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrCreateAction(XrActionSet set, const XrActionCreateInfo* info, XrAction* out) {
    if (!strcmp(info->actionName, "deploy_parachute")) {
        expect(set == gameSet && info->actionType == XR_ACTION_TYPE_BOOLEAN_INPUT && info->countSubactionPaths == 0);
        *out = deploy;
        return XR_SUCCESS;
    }
    if (!strcmp(info->actionName, "navigate")) {
        expect(set == levelsSet && info->actionType == XR_ACTION_TYPE_VECTOR2F_INPUT && info->countSubactionPaths == 0);
        *out = navigate;
        return XR_SUCCESS;
    }
    const bool pointerFeedback = !strcmp(info->actionName, "pointer_haptic");
    if (pointerFeedback || !strcmp(info->actionName, "gesture_haptic")) {
        expect(set == (pointerFeedback ? menuSet : gameSet) && info->actionType == XR_ACTION_TYPE_VIBRATION_OUTPUT && info->countSubactionPaths == 2 &&
               info->subactionPaths[0] == handPaths[0] && info->subactionPaths[1] == handPaths[1]);
        *out = pointerFeedback ? pointerHaptic : pressHaptic;
        return XR_SUCCESS;
    }
    if (!strcmp(info->actionName, "thumbs_up") || !strcmp(info->actionName, "flip_off")) {
        expect(set == gameSet && info->actionType == XR_ACTION_TYPE_BOOLEAN_INPUT && info->countSubactionPaths == 2 &&
               info->subactionPaths[0] == handPaths[0] && info->subactionPaths[1] == handPaths[1]);
        *out = !strcmp(info->actionName, "thumbs_up") ? thumbsUp : flipOff;
        return XR_SUCCESS;
    }
    if (set == gameSet) {
        const bool movement = !strcmp(info->actionName, "move");
        expect((movement || !strcmp(info->actionName, "turn")) && info->actionType == XR_ACTION_TYPE_VECTOR2F_INPUT &&
               info->countSubactionPaths == 0);
        *out = movement ? move : turn;
        return XR_SUCCESS;
    }
    expect(set == menuSet && info->countSubactionPaths == 2 && info->subactionPaths[0] == handPaths[0] &&
           info->subactionPaths[1] == handPaths[1]);
    const bool isAim = info->actionType == XR_ACTION_TYPE_POSE_INPUT;
    expect((isAim || info->actionType == XR_ACTION_TYPE_BOOLEAN_INPUT) && !strcmp(info->actionName, isAim ? "aim" : "pointer_select"));
    *out = isAim ? aim : selectButton;
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrSuggestInteractionProfileBindings(XrInstance, const XrInteractionProfileSuggestedBinding* info) {
    expect(!attached && suggested.insert(info->interactionProfile).second);
    for (const auto& profile : profiles) {
        if (info->interactionProfile != pathId("/interaction_profiles/" + profile.first)) continue;
        const auto* first = info->suggestedBindings;
        const auto* end = first + info->countSuggestedBindings;
        const bool simple = profile.first == "khr/simple_controller";
        const bool hasA = profile.first == "valve/index_controller" || profile.first == "oculus/touch_controller";
        const bool index = profile.first == "valve/index_controller";
        const bool touch = profile.first == "oculus/touch_controller";
        expect(info->next == nullptr && info->countSuggestedBindings == (simple ? 8u : index ? 16u : touch ? 14u : 15u));
        auto has = [&](XrAction action, XrPath path) {
            return std::any_of(first, end, [&](const XrActionSuggestedBinding& b) {
                return b.action == action && b.binding == path;
            });
        };
        const auto rightA = pathId("/user/hand/right/input/a/click");
        expect(has(deploy, rightA) == hasA);
        for (int i = 0; i < 2; ++i) {
            const auto input = std::string(users[i]) + "/input/";
            expect(has(pointerHaptic, pathId(std::string(users[i]) + "/output/haptic")));
            expect(has(pressHaptic, pathId(std::string(users[i]) + "/output/haptic")));
            expect(has(aim, pathId(input + "aim/pose")) && has(selectButton, pathId(input + profile.second)));
            if (!simple) {
                const char* stick = profile.first == "htc/vive_controller" ? "trackpad" : "thumbstick";
                expect(has(navigate, pathId(input + stick)) == (i == 0));
                expect(has(i == 0 ? move : turn, pathId(input + stick)));
                expect(has(flipOff, pathId(input + (index ? "squeeze/force" : touch ? "squeeze/value" : "squeeze/click"))));
                if (!touch) expect(has(thumbsUp, pathId(input + (index ? "trackpad/force" : "trackpad/click"))));
            }
        }
        return XR_SUCCESS;
    }
    expect(false);
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrAttachSessionActionSets(XrSession s, const XrSessionActionSetsAttachInfo* info) {
    expect(s == session && !attached && info->countActionSets == 3 && info->actionSets[0] == menuSet &&
           info->actionSets[1] == levelsSet && info->actionSets[2] == gameSet);
    expect(suggested.size() == profiles.size());
    attached = true;
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrCreateActionSpace(XrSession s, const XrActionSpaceCreateInfo* info, XrSpace* out) {
    expect(s == session && attached && info->action == aim && info->poseInActionSpace.orientation.w == 1);
    *out = handSpaces[handIndex(info->subactionPath)];
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrSyncActions(XrSession s, const XrActionsSyncInfo* info) {
    expect(s == session && attached && sessionState == XR_SESSION_STATE_FOCUSED);
    auto& fixture = currentFixture();
    const auto count = info->countActiveActionSets;
    const auto* sets = info->activeActionSets;
    fixture.menusActive = count >= 1 && sets[0].actionSet == menuSet;
    fixture.gameActive = count == 1 && sets[0].actionSet == gameSet;
    fixture.levelsActive = count == 2 && sets[1].actionSet == levelsSet;
    expect(count == 0 || (count == 1 && (fixture.menusActive || fixture.gameActive)) ||
           (fixture.menusActive && fixture.levelsActive));
    for (unsigned i = 0; i < count; ++i) expect(sets[i].subactionPath == XR_NULL_PATH);
    ++fixture.actionSyncs;
    if (fixture.syncNotFocused) return XR_SESSION_NOT_FOCUSED;
    fixture.deployChanged = fixture.syncedDeploy != fixture.deployHeld;
    fixture.syncedDeploy = fixture.deployHeld;
    for (auto* buttons : {fixture.pointerSelect, fixture.thumbsUp, fixture.flipOff}) {
        for (int i = 0; i < 2; ++i) {
            auto& button = buttons[i];
            button.changed = button.held != button.synced;
            button.synced = button.held;
        }
    }
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrGetCurrentInteractionProfile(XrSession s, XrPath user, XrInteractionProfileState* state) {
    expect(s == session && attached && state->type == XR_TYPE_INTERACTION_PROFILE_STATE);
    const char* profile = currentFixture().handProfiles[handIndex(user)];
    state->interactionProfile = profile ? pathId(std::string("/interaction_profiles/") + profile) : XR_NULL_PATH;
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrGetActionStatePose(XrSession, const XrActionStateGetInfo* get, XrActionStatePose* state) {
    expect(currentFixture().menusActive && get->action == aim);
    state->isActive = currentFixture().handActive[handIndex(get->subactionPath)];
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrGetActionStateVector2f(XrSession s, const XrActionStateGetInfo* get, XrActionStateVector2f* state) {
    const bool gallery = get->action == navigate;
    expect(s == session && (gallery ? currentFixture().levelsActive :
           currentFixture().gameActive && (get->action == move || get->action == turn)) && get->subactionPath == XR_NULL_PATH &&
           state->type == XR_TYPE_ACTION_STATE_VECTOR2F);
    const auto& fixture = currentFixture();
    if (gallery) {
        state->isActive = fixture.levelStickActive;
        state->currentState = fixture.levelStick;
    } else {
        const int i = get->action == move ? 0 : 1;
        state->isActive = fixture.stickActive[i];
        state->currentState = fixture.sticks[i];
    }
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrGetActionStateBoolean(XrSession, const XrActionStateGetInfo* get, XrActionStateBoolean* state) {
    expect(get->action == selectButton ? currentFixture().menusActive : currentFixture().gameActive);
    if (get->action == deploy) {
        expect(get->subactionPath == XR_NULL_PATH && state->type == XR_TYPE_ACTION_STATE_BOOLEAN);
        const auto& fixture = currentFixture();
        state->isActive = fixture.deployActive;
        state->currentState = state->isActive && fixture.syncedDeploy;
        state->changedSinceLastSync = state->isActive && fixture.deployChanged;
        return XR_SUCCESS;
    }
    expect(get->action == selectButton || get->action == thumbsUp || get->action == flipOff);
    const auto& fixture = currentFixture();
    const auto* buttons = get->action == selectButton ? fixture.pointerSelect :
                          get->action == thumbsUp ? fixture.thumbsUp : fixture.flipOff;
    const auto& button = buttons[handIndex(get->subactionPath)];
    state->isActive = button.active;
    state->currentState = button.active && button.synced;
    state->changedSinceLastSync = button.active && button.changed;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrApplyHapticFeedback(XrSession s, const XrHapticActionInfo* info, const XrHapticBaseHeader* feedback) {
    expect(s == session && attached && sessionState == XR_SESSION_STATE_FOCUSED &&
           info->type == XR_TYPE_HAPTIC_ACTION_INFO && ((currentFixture().gameActive && info->action == pressHaptic) ||
           (currentFixture().menusActive && info->action == pointerHaptic)) && feedback->type == XR_TYPE_HAPTIC_VIBRATION);
    const auto* tick = reinterpret_cast<const XrHapticVibration*>(feedback);
    expect(tick->duration == 15'000'000 && tick->frequency == XR_FREQUENCY_UNSPECIFIED && tick->amplitude == 0.35f);
    auto& fixture = currentFixture();
    ++fixture.handTicks[handIndex(info->subactionPath)];
    return fixture.hapticResult;
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
    expect(handle == session && !frameOpen && !eyes[0].texture && !eyes[1].texture && !localCreated && !viewCreated);
    ++currentFixture().sessionDestroys;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrCreateReferenceSpace(XrSession handle, const XrReferenceSpaceCreateInfo* info, XrSpace* out) {
    const XrPosef identity{{0, 0, 0, 1}, {}};
    expect(handle == session && info->type == XR_TYPE_REFERENCE_SPACE_CREATE_INFO &&
           std::memcmp(&info->poseInReferenceSpace, &identity, sizeof(identity)) == 0);
    const bool local = info->referenceSpaceType == XR_REFERENCE_SPACE_TYPE_LOCAL;
    if (!local && info->referenceSpaceType != XR_REFERENCE_SPACE_TYPE_VIEW) return XR_ERROR_REFERENCE_SPACE_UNSUPPORTED;
    bool& created = local ? localCreated : viewCreated;
    expect(!created);
    created = true;
    *out = local ? localSpace : viewSpace;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrDestroySpace(XrSpace handle) {
    if (handle == handSpaces[0] || handle == handSpaces[1]) return XR_SUCCESS;
    bool& created = handle == localSpace ? localCreated : viewCreated;
    expect((handle == localSpace || handle == viewSpace) && created);
    created = false;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateViewConfigurationViews(XrInstance, XrSystemId, XrViewConfigurationType type,
    uint32_t capacity, uint32_t* count, XrViewConfigurationView* views) {
    *count = 2;
    expect(type == stereo);
    if (capacity == 0) return XR_SUCCESS;
    if (capacity < *count) return XR_ERROR_SIZE_INSUFFICIENT;
    for (unsigned i = 0; i < *count; ++i) {
        views[i].maxImageRectWidth = views[i].recommendedImageRectWidth = i ? 1100 : 1200;
        views[i].maxImageRectHeight = views[i].recommendedImageRectHeight = i ? 1300 : 1250;
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
    auto& fixture = currentFixture();
    if (fixture.profileChanged) {
        fixture.profileChanged = false;
        XrEventDataInteractionProfileChanged changed{XR_TYPE_EVENT_DATA_INTERACTION_PROFILE_CHANGED};
        changed.session = session;
        std::memcpy(buffer, &changed, sizeof(changed));
        return XR_SUCCESS;
    }
    auto& next = fixture.nextEvent;
    if (next == XR_SESSION_STATE_UNKNOWN) return XR_EVENT_UNAVAILABLE;
    sessionState = next;
    next = fixture.followingEvent;
    fixture.followingEvent = XR_SESSION_STATE_UNKNOWN;
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

XRAPI_ATTR XrResult XRAPI_CALL xrLocateSpace(XrSpace target, XrSpace base, XrTime time, XrSpaceLocation* out) {
    const auto& fixture = currentFixture();
    if (target == handSpaces[0] || target == handSpaces[1]) {
        expect(base == localSpace && frameOpen && time == fixture.frameWaits);
        out->locationFlags = fixture.handFlags[target == handSpaces[1]];
        out->pose = fixture.eyePoses[target == handSpaces[1]];
        return XR_SUCCESS;
    }
    expect(viewCreated && localCreated && target == viewSpace && base == localSpace &&
           frameOpen && time == fixture.frameWaits && out->type == XR_TYPE_SPACE_LOCATION);
    out->locationFlags = fixture.headFlags;
    out->pose = fixture.head;
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL xrLocateViews(XrSession handle, const XrViewLocateInfo* info, XrViewState* tracking,
    uint32_t capacity, uint32_t* count, XrView* views) {
    expect(handle == session && frameOpen && info->space == localSpace && info->displayTime == currentFixture().frameWaits);
    *count = 2;
    if (capacity < *count) return XR_ERROR_SIZE_INSUFFICIENT;
    const auto& fixture = currentFixture();
    tracking->viewStateFlags = fixture.eyeFlags;
    for (unsigned i = 0; i < *count; ++i) {
        views[i].pose = fixture.eyePoses[i];
        views[i].fov = fixture.eyeFovs[i];
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
    expect(info->layerCount == 1 && layer->type == XR_TYPE_COMPOSITION_LAYER_PROJECTION && layer->space == localSpace);
    if (layer->viewCount != 2) return XR_ERROR_VALIDATION_FAILURE;
    for (unsigned i = 0; i < 2; ++i) {
        const auto& view = layer->views[i];
        expect(view.subImage.swapchain == i + 1 && !eyes[i].acquired &&
               view.subImage.imageRect.extent.width == 2 && view.subImage.imageRect.extent.height == 2 &&
               std::memcmp(&view.pose, &fixture.eyePoses[i], sizeof(view.pose)) == 0 &&
               std::memcmp(&view.fov, &fixture.eyeFovs[i], sizeof(view.fov)) == 0);
    }
    ++fixture.projections;
    return XR_SUCCESS;
}

PFN_xrVoidFunction stereoFunction(const char* name) {
#define MATCH(fn) if (std::strcmp(name, #fn) == 0) return reinterpret_cast<PFN_xrVoidFunction>(static_cast<PFN_##fn>(fn));
    MATCH(xrStringToPath)
    MATCH(xrCreateActionSet)
    MATCH(xrDestroyActionSet)
    MATCH(xrCreateAction)
    MATCH(xrSuggestInteractionProfileBindings)
    MATCH(xrAttachSessionActionSets)
    MATCH(xrCreateActionSpace)
    MATCH(xrSyncActions)
    MATCH(xrGetCurrentInteractionProfile)
    MATCH(xrGetActionStatePose)
    MATCH(xrGetActionStateBoolean)
    MATCH(xrGetActionStateVector2f)
    MATCH(xrApplyHapticFeedback)
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
    MATCH(xrLocateSpace)
    MATCH(xrLocateViews)
    MATCH(xrEndFrame)
#undef MATCH
    return nullptr;
}
