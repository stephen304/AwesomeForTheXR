#pragma once
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
    X(CreateSession) X(DestroySession) X(CreateReferenceSpace) X(DestroySpace) \
    X(EnumerateViewConfigurationViews) X(EnumerateEnvironmentBlendModes) X(EnumerateSwapchainFormats) \
    X(CreateSwapchain) X(DestroySwapchain) X(EnumerateSwapchainImages) X(AcquireSwapchainImage) \
    X(WaitSwapchainImage) X(ReleaseSwapchainImage) X(PollEvent) X(BeginSession) X(EndSession) \
    X(RequestExitSession) X(WaitFrame) X(BeginFrame) X(LocateViews) X(EndFrame)

class StereoSession {
public:
    void open(PFN_xrGetInstanceProcAddr getProc, XrInstance instance, XrSystemId system,
              const XrGraphicsBindingD3D11KHR& binding, FILE* log);
    bool begin(int unityFrame);
    void describe(int eye, float* desc) const;
    void pose(int eye, float* pose) const;
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
    XrSpace space = XR_NULL_HANDLE;
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
