#pragma once
#include "input_context.h"
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <array>
#include <cstdio>
#include <vector>

// Instance functions are resolved through the exact loader used for preflight.
#define AFTXR_STEREO_FUNCTIONS(X) \
    X(CreateSession) X(DestroySession) X(CreateReferenceSpace) X(DestroySpace) X(LocateSpace) \
    X(EnumerateViewConfigurationViews) X(EnumerateEnvironmentBlendModes) X(EnumerateSwapchainFormats) \
    X(CreateSwapchain) X(DestroySwapchain) X(EnumerateSwapchainImages) X(AcquireSwapchainImage) \
    X(WaitSwapchainImage) X(ReleaseSwapchainImage) X(PollEvent) X(BeginSession) X(EndSession) \
    X(StringToPath) X(CreateActionSet) X(DestroyActionSet) X(CreateAction) X(SuggestInteractionProfileBindings) \
    X(AttachSessionActionSets) X(GetCurrentInteractionProfile) X(CreateActionSpace) X(SyncActions) X(GetActionStateBoolean) X(GetActionStatePose) X(GetActionStateVector2f) \
    X(ApplyHapticFeedback) X(RequestExitSession) X(WaitFrame) X(BeginFrame) X(LocateViews) X(EndFrame)

class StereoSession {
public:
    void open(PFN_xrGetInstanceProcAddr getProc, XrInstance instance, XrSystemId system,
              const XrGraphicsBindingD3D11KHR& binding, FILE* log);
    bool begin(int unityFrame, InputContext context);
    void describe(int eye, float* desc) const;
    // OVREye.Center in the original game. Only pose queries accept this index.
    static constexpr int headAnchor = -1;
    void pose(int anchor, float* pose) const;
    int pointer(int hand, float* pose, uint32_t& generation) const;
    int getGameplayPresses(int unityFrame, InputContext context);
    bool mergeSticks(int unityFrame, InputContext context, XrVector2f& movement, XrVector2f& look);
    void projection(int eye, float nearClip, float farClip, float* matrix) const;
    void recenter() { recenterPending = true; }
    void renderedEye(unsigned eye, IUnknown* texture, int unityFrame);
    void submit();
    void close() noexcept;
    ~StereoSession() { close(); }
private:
    struct Eye {
        XrSwapchain chain = XR_NULL_HANDLE;
        XrExtent2Di size{};
        std::vector<XrSwapchainImageD3D11KHR> images;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> source;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        XrExtent2Di recommended{};
        std::vector<Microsoft::WRL::ComPtr<ID3D11CommandList>> copies;
    };
#define AFTXR_DECLARE(name) PFN_xr##name name = nullptr;
    AFTXR_STEREO_FUNCTIONS(AFTXR_DECLARE)
#undef AFTXR_DECLARE
    FILE* log = nullptr;
    XrInstance instance = XR_NULL_HANDLE;
    XrSession session = XR_NULL_HANDLE;
    XrSpace localSpace = XR_NULL_HANDLE, viewSpace = XR_NULL_HANDLE;
    InputContext inputContext = InputContext::Inactive;
    XrActionSet menuSet = XR_NULL_HANDLE, levelsSet = XR_NULL_HANDLE, gameSet = XR_NULL_HANDLE;
    XrAction move = XR_NULL_HANDLE, turn = XR_NULL_HANDLE, navigate = XR_NULL_HANDLE;
    XrVector2f movementStick{}, turnStick{};
    int inputFrame = -1;
    bool actionsActive = false;
    XrAction aim = XR_NULL_HANDLE, pointerSelect = XR_NULL_HANDLE, pointerHaptic = XR_NULL_HANDLE;
    XrAction deploy = XR_NULL_HANDLE;
    XrAction thumbsUp = XR_NULL_HANDLE, flipOff = XR_NULL_HANDLE, spray = XR_NULL_HANDLE, pressHaptic = XR_NULL_HANDLE;
    std::array<bool, 2> selectReleased{}, deployReleased{}, thumbsUpReleased{}, flipOffReleased{}, sprayReleased{};
    int gameplayPresses = 0;
    XrActionStateBoolean readButton(XrAction action, XrPath hand);
    void interruptButtonPresses();
    void tickHand(XrAction output, XrPath hand);
    struct Hand {
        XrPath path{}, lastProfile{};
        XrActionStateBoolean selectState{};
        uint32_t generation = 0;
        XrSpace space{};
        XrPosef pose{};
        bool hasTrackedPosition = false;
        bool usable = false, pressed = false;

        void interrupt() {
            ++generation;
            usable = pressed = false;
        }
    };
    std::array<Hand, 2> hands{};
    bool profilesDirty = true;
    void openInput();
    bool syncInput(int unityFrame, InputContext context);
    void samplePointer();
    bool readPointerHand(Hand& hand);
    void unityPose(const XrPosef& raw, float* p) const;
    XrPosef headPose{{0, 0, 0, 1}, {}};
    XrEnvironmentBlendMode blend{};
    XrSessionState state = XR_SESSION_STATE_UNKNOWN;
    std::array<Eye, 2> eyes{};
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    bool running = false, finished = false;
    unsigned frames = 0, submitted = 0;
    std::array<XrView,2> views{{{XR_TYPE_VIEW},{XR_TYPE_VIEW}}};
    std::vector<int64_t> formats;
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    unsigned renderedEyes = 0;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> deferred;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> fullscreen;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> flip;
    void recordCopies(Eye& eye, const D3D11_TEXTURE2D_DESC& sourceDesc);
    int frameNumber = -1;
    bool begun = false, valid = false, haveViews = false, recenterPending = true;
    XrTime displayTime = 0;
    XrVector3f center{};
    float centerYaw = 0;
    void makeEye(Eye& eye, const D3D11_TEXTURE2D_DESC& desc);
    void draw(Eye& eye);
    void poll(bool shuttingDown);
    void end(bool images);
};
