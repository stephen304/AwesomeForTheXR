#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_NO_PROTOTYPES
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_reflection.h>
#include <cstdio>
#include <share.h>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>

#include "headset_retry.h"
#include "stereo.h"

namespace {
using Log = std::unique_ptr<FILE, decltype(&fclose)>;
static_assert(sizeof(void*) == 4, "Awesome requires the Win32 OpenXR loader.");

struct Launch {
    bool attempted = false;
    bool prepared = false;
    HMODULE loader = nullptr;
    XrInstance instance = XR_NULL_HANDLE;
    XrSystemId system = XR_NULL_SYSTEM_ID;
    XrGraphicsRequirementsD3D11KHR graphics{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    ID3D11Device* const* unityDeviceSlot = nullptr; // Lives in the player image.
    bool deviceChecked = false;
    // Own the device named in the session binding after managed startup accepts it.
    Microsoft::WRL::ComPtr<ID3D11Device> unityDevice;
    XrGraphicsBindingD3D11KHR binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    PFN_xrGetInstanceProcAddr getProc = nullptr;
    Log stereoLog{nullptr, fclose};
    std::unique_ptr<StereoSession> stereo;
    bool stereoFailed = false;
    char stereoError[512]{};
    PFN_xrDestroyInstance destroy = nullptr;
    const char* status = "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.";
};

Launch launch;
std::mutex launchMutex;

std::wstring gameDirectory() {
    wchar_t path[32768];
    DWORD size = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    if (!size || size >= ARRAYSIZE(path)) return {};
    std::wstring name(path, size);
    auto slash = name.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : name.substr(0, slash + 1);
}

Log openLog(const std::wstring& directory, const wchar_t* mode) {
    FILE* file = nullptr;
    if (!directory.empty()) file = _wfsopen((directory + L"AwesomeForTheXR-launcher.log").c_str(), mode, _SH_DENYNO);
    if (file) setvbuf(file, nullptr, _IONBF, 0);
    return Log(file, fclose);
}

const char* resultName(XrResult result) {
    switch (result) {
#define AFTXR_RESULT(name, value) case name: return #name;
        XR_LIST_ENUM_XrResult(AFTXR_RESULT)
#undef AFTXR_RESULT
        default: return "Unknown XrResult";
    }
}

bool checked(FILE* log, const char* operation, XrResult result) {
    if (log) fprintf(log, "%s: %s (%d)\n", operation, resultName(result), static_cast<int>(result));
    if (XR_SUCCEEDED(result)) return true;
    switch (result) {
        case XR_ERROR_RUNTIME_UNAVAILABLE:
            launch.status = "The 32-bit OpenXR runtime is unavailable. Check your active OpenXR runtime.";
            break;
        case XR_ERROR_EXTENSION_NOT_PRESENT:
            launch.status = "The active OpenXR runtime does not support Direct3D 11.";
            break;
        case XR_ERROR_FORM_FACTOR_UNSUPPORTED:
        case XR_ERROR_FORM_FACTOR_UNAVAILABLE:
            launch.status = "No VR headset is available. Check that your headset is connected and ready.";
            break;
        case XR_ERROR_API_VERSION_UNSUPPORTED:
            launch.status = "The active runtime does not support OpenXR 1.0.";
            break;
        default: break;
    }
    return false;
}

// Called on a normal startup/managed thread, never while the Windows loader lock is held.
void release(FILE* log) {
    launch.stereo.reset();
    launch.stereoLog.reset();
    launch.unityDeviceSlot = nullptr;
    launch.binding.device = nullptr;
    launch.unityDevice.Reset();
    bool canUnload = true;
    if (launch.instance != XR_NULL_HANDLE) {
        XrResult result = launch.destroy(launch.instance);
        if (log) fprintf(log, "xrDestroyInstance: %s (%d)\n", resultName(result), static_cast<int>(result));
        // A failed destroy can leave runtime code live. Keep the library loaded
        // until process exit and never try to reuse the failed instance.
        canUnload = XR_SUCCEEDED(result);
        if (!canUnload && log) fprintf(log, "Keeping OpenXR loader loaded until process exit.\n");
        launch.instance = XR_NULL_HANDLE;
    }
    launch.system = XR_NULL_SYSTEM_ID;
    if (canUnload && launch.loader) FreeLibrary(launch.loader);
    launch.loader = nullptr;
    launch.destroy = nullptr;
}

template<typename Fn>
bool resolve(FILE* log, const char* name, Fn& function) {
    function = reinterpret_cast<Fn>(GetProcAddress(launch.loader, name));
    if (function) return true;
    if (log) fprintf(log, "Missing loader export: %s (Windows error %lu)\n", name, GetLastError());
    launch.status = "The OpenXR loader is incompatible. Reapply the game patch.";
    return false;
}

bool discover(const std::wstring& directory, FILE* log) {
    if (directory.empty()) return false;
    const auto path = directory + L"openxr_loader.dll";
    launch.loader = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!launch.loader) {
        DWORD error = GetLastError();
        if (log) fprintf(log, "Load openxr_loader.dll: Windows error %lu\n", error);
        launch.status = "The 32-bit OpenXR loader could not be loaded. Reapply the game patch.";
        return false;
    }

    PFN_xrCreateInstance createInstance;
    PFN_xrGetInstanceProperties getInstanceProperties;
    PFN_xrGetSystem getSystem;
    PFN_xrGetSystemProperties getSystemProperties;

    if (!resolve(log, "xrCreateInstance", createInstance) || !resolve(log, "xrDestroyInstance", launch.destroy) ||
        !resolve(log, "xrGetInstanceProperties", getInstanceProperties) || !resolve(log, "xrGetSystem", getSystem) ||
        !resolve(log, "xrGetSystemProperties", getSystemProperties) || !resolve(log, "xrGetInstanceProcAddr", launch.getProc)) return false;

    const char* extension = XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
    XrInstanceCreateInfo info{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(info.applicationInfo.applicationName, "AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome");
    info.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    info.enabledExtensionCount = 1;
    info.enabledExtensionNames = &extension;
    XrInstance instance = XR_NULL_HANDLE;
    if (!checked(log, "xrCreateInstance", createInstance(&info, &instance))) return false;
    launch.instance = instance;

    XrInstanceProperties runtime{XR_TYPE_INSTANCE_PROPERTIES};
    if (!checked(log, "xrGetInstanceProperties", getInstanceProperties(instance, &runtime))) return false;
    if (log) fprintf(log, "Runtime: %s %u.%u.%u\n", runtime.runtimeName,
                     XR_VERSION_MAJOR(runtime.runtimeVersion), XR_VERSION_MINOR(runtime.runtimeVersion),
                     XR_VERSION_PATCH(runtime.runtimeVersion));

    XrSystemGetInfo systemInfo{XR_TYPE_SYSTEM_GET_INFO};
    systemInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    const XrResult result = waitForHeadset(
        [&] { return getSystem(instance, &systemInfo, &launch.system); }, GetTickCount64, Sleep);
    if (!checked(log, "xrGetSystem", result)) return false;
    XrSystemProperties headset{XR_TYPE_SYSTEM_PROPERTIES};
    if (!checked(log, "xrGetSystemProperties", getSystemProperties(instance, launch.system, &headset))) return false;
    if (log) fprintf(log, "Headset: %s\n", headset.systemName);

    PFN_xrVoidFunction entry = nullptr;
    if (!checked(log, "Resolve xrGetD3D11GraphicsRequirementsKHR",
                 launch.getProc(instance, "xrGetD3D11GraphicsRequirementsKHR", &entry))) return false;
    if (!entry) {
        if (log) fprintf(log, "xrGetInstanceProcAddr returned no function address for xrGetD3D11GraphicsRequirementsKHR.\n");
        return false;
    }
    auto requirements = reinterpret_cast<PFN_xrGetD3D11GraphicsRequirementsKHR>(entry);
    if (!checked(log, "xrGetD3D11GraphicsRequirementsKHR", requirements(instance, launch.system, &launch.graphics))) return false;
    if (log) fprintf(log, "Required adapter LUID: %08lX:%08lX\nMinimum D3D11 feature level: 0x%04X\n",
                     static_cast<DWORD>(launch.graphics.adapterLuid.HighPart), launch.graphics.adapterLuid.LowPart,
                     static_cast<unsigned>(launch.graphics.minFeatureLevel));
    launch.status = "OpenXR graphics requirements are ready.";
    return true;
}

using Microsoft::WRL::ComPtr;

bool sameAdapter(LUID left, LUID right) {
    return left.HighPart == right.HighPart && left.LowPart == right.LowPart;
}

bool graphicsCall(FILE* log, const char* operation, HRESULT result, const char* failure) {
    if (SUCCEEDED(result)) return true;
    if (log) fprintf(log, "%s: HRESULT 0x%08lX\n", operation, static_cast<DWORD>(result));
    launch.status = failure;
    return false;
}

int selectAdapter(FILE* log) {
    // Use the same DXGI 1.1 factory and zero-based EnumAdapters as the patched player.
    ComPtr<IDXGIFactory1> factory;
    if (!graphicsCall(log, "CreateDXGIFactory1", CreateDXGIFactory1(IID_PPV_ARGS(&factory)),
                      "Windows could not enumerate the graphics adapters for VR.")) return -1;
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter> adapter;
        HRESULT hr = factory->EnumAdapters(i, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        if (!graphicsCall(log, "EnumAdapters", hr, "Windows could not enumerate the graphics adapters for VR.")) return -1;
        DXGI_ADAPTER_DESC desc{};
        if (!graphicsCall(log, "Selected adapter GetDesc", adapter->GetDesc(&desc),
                          "Windows could not identify a graphics adapter for VR.")) return -1;
        if (sameAdapter(desc.AdapterLuid, launch.graphics.adapterLuid)) {
            if (log) fprintf(log, "Selected Unity DXGI adapter index: %u\n", i);
            return static_cast<int>(i);
        }
    }
    launch.status = "The graphics adapter required by the headset is unavailable to Unity.";
    return -1;
}

bool bindDevice(FILE* log, ID3D11Device* device) {
    if (!device) {
        launch.status = "Unity did not provide a Direct3D 11 device for the headset.";
        return false;
    }
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    const char* identify = "Unity's Direct3D 11 graphics adapter could not be identified.";
    if (!graphicsCall(log, "QueryInterface(IDXGIDevice)", device->QueryInterface(IID_PPV_ARGS(&dxgi)), identify) ||
        !graphicsCall(log, "GetAdapter", dxgi->GetAdapter(&adapter), identify) ||
        !graphicsCall(log, "Unity adapter GetDesc", adapter->GetDesc(&desc), identify)) return false;

    const auto level = device->GetFeatureLevel();
    char name[512]{};
    WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, name, sizeof(name), nullptr, nullptr);
    if (log) fprintf(log, "Unity D3D11 device: %s\nActual adapter LUID: %08lX:%08lX\nActual D3D11 feature level: 0x%04X\n",
                     name, static_cast<DWORD>(desc.AdapterLuid.HighPart), desc.AdapterLuid.LowPart, static_cast<unsigned>(level));
    UINT flags = device->GetCreationFlags();
    if (log) fprintf(log, "Unity D3D11 creation flags: 0x%08X\n", flags);
    if (flags & D3D11_CREATE_DEVICE_SINGLETHREADED) {
        launch.status = "Unity created a single-threaded D3D11 device that cannot be shared with OpenXR.";
        return false;
    }
    if (!sameAdapter(desc.AdapterLuid, launch.graphics.adapterLuid)) {
        launch.status = "Unity created its Direct3D 11 device on a different GPU than the headset requires.";
        return false;
    }
    if (level < launch.graphics.minFeatureLevel) {
        launch.status = "Unity's Direct3D 11 device does not meet the headset's minimum feature level.";
        return false;
    }
    if (!graphicsCall(log, "GetDeviceRemovedReason", device->GetDeviceRemovedReason(),
                      "Unity's Direct3D 11 device was lost during VR startup.")) return false;

    // DXGI 1.0 devices can pass xrEndFrame even when SteamVR cannot synchronize
    // either eye. Exercise keyed sharing on Unity's actual device before opening XR.
    D3D11_TEXTURE2D_DESC sync{};
    sync.Width = sync.Height = sync.ArraySize = sync.MipLevels = sync.SampleDesc.Count = 1;
    sync.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sync.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    sync.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<IDXGIResource> shared;
    HANDLE handle = nullptr; // Legacy shared handle is owned by the resource.
    const char* shareError = "Unity's D3D11 device cannot share eye textures with the VR compositor.";
    if (!graphicsCall(log, "Create keyed shared texture", device->CreateTexture2D(&sync, nullptr, &texture), shareError) ||
        !graphicsCall(log, "QueryInterface(IDXGIResource)", texture.As(&shared), shareError) ||
        !graphicsCall(log, "GetSharedHandle", shared->GetSharedHandle(&handle), shareError)) return false;
    if (!handle) {
        launch.status = shareError;
        return false;
    }
    if (log) fprintf(log, "Unity D3D11 keyed texture sharing verified.\n");

    launch.unityDevice = device; // One owned reference, independent of Unity's reference.
    launch.binding.device = launch.unityDevice.Get();
    launch.status = "Unity's Direct3D 11 device is ready for an OpenXR session.";
    if (log) fprintf(log, "%s\n", launch.status);
    return true;
}
void failStereo(const char* detail) noexcept {
    std::snprintf(launch.stereoError, sizeof(launch.stereoError), "OpenXR camera rendering failed. %s", detail);
    launch.status = launch.stereoError;
    launch.stereoFailed = true;
    if (launch.stereoLog) fprintf(launch.stereoLog.get(), "%s\n", launch.status);
}
} // namespace

extern "C" int __cdecl AFTXR_PrepareLaunch() {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.attempted) {
        launch.attempted = true;
        try {
            const auto directory = gameDirectory();
            auto log = openLog(directory, L"w");
            if (log) fprintf(log.get(), "AwesomeForTheXR OpenXR 1.0 startup | x86 | PID %lu\n", GetCurrentProcessId());
            launch.prepared = discover(directory, log.get());
            if (!launch.prepared) release(log.get());
            if (log) fprintf(log.get(), "%s\n", launch.status);
        } catch (...) {
            release(nullptr);
        }
    }
    return launch.prepared ? 1 : 0;
}

extern "C" int __cdecl AFTXR_SelectUnityAdapter() {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.prepared) return -1;
    try {
        auto log = openLog(gameDirectory(), L"a");
        return selectAdapter(log.get());
    } catch (...) {
        launch.status = "Windows could not enumerate the graphics adapters for VR.";
        return -1;
    }
}

extern "C" void __cdecl AFTXR_SetUnityDeviceSlot(ID3D11Device* const* slot) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (launch.prepared && !launch.deviceChecked) launch.unityDeviceSlot = slot;
}

extern "C" const char* __cdecl AFTXR_GetLaunchStatus() {
    std::lock_guard<std::mutex> lock(launchMutex);
    return launch.status;
}

extern "C" void __cdecl AFTXR_ReleaseLaunch() {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.loader) return;
    launch.prepared = false;
    try {
        auto log = openLog(gameDirectory(), L"a");
        release(log.get());
    } catch (...) {
        release(nullptr);
    }
}

extern "C" [[noreturn]] void __cdecl AFTXR_AbortVrLaunch(const char* reason) {
    // Managed scene readiness leaves its specific failure in the plugin.
    if (!reason) reason = AFTXR_GetLaunchStatus();
    AFTXR_ReleaseLaunch();
    try {
        auto log = openLog(gameDirectory(), L"a");
        if (log) fprintf(log.get(), "VR launch stopped: %s\n", reason);
    } catch (...) {
        // A log write is optional. The error must still be shown to the player.
    }
    MessageBoxA(GetActiveWindow(), reason, "AwesomeForTheXR - VR startup failed",
                MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    ExitProcess(1);
}

extern "C" int __cdecl AFTXR_IsOpenXRSceneReady() {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.prepared || launch.deviceChecked) return 0;
    launch.deviceChecked = true;
    try {
        auto log = openLog(gameDirectory(), L"a");
        if (log) fprintf(log.get(), "Managed startup: validating Unity's D3D11 device.\n");
        ID3D11Device* device = launch.unityDeviceSlot ? *launch.unityDeviceSlot : nullptr;
        launch.unityDeviceSlot = nullptr;
        if (!bindDevice(log.get(), device)) return 0;
        launch.stereoLog = std::move(log);
        launch.stereo = std::make_unique<StereoSession>();
        launch.stereo->open(launch.getProc, launch.instance, launch.system, launch.binding, launch.stereoLog.get());
        return 1; // Session created. The managed loader may now enter the game VR scene.
    } catch (const std::exception& e) {
        failStereo(e.what());
    } catch (...) {
        launch.status = "Unity's Direct3D 11 device could not be validated for VR.";
    }
    return 0;
}


// Called from Unity's main thread. The VR-only player option -force-gfx-direct
// also executes the ordered end-of-frame render event on that thread.
extern "C" int __cdecl AFTXR_BeginCameraFrame(int frame, InputContext context) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (launch.stereoFailed || !launch.stereo) return -1;
    try {
        return launch.stereo->begin(frame, context) ? 1 : 0;
    } catch (const std::exception& e) { failStereo(e.what()); return -1; }
}

extern "C" int __cdecl AFTXR_MergeGamepadSticks(int frame, InputContext context, XrVector2f* movement, XrVector2f* look) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.stereo || launch.stereoFailed || !movement || !look) return 0;
    try { return launch.stereo->mergeSticks(frame, context, *movement, *look) ? 1 : 0; }
    catch (const std::exception& e) { failStereo(e.what()); return 0; }
}

extern "C" int __cdecl AFTXR_GetButtonPresses(int frame, InputContext context) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.stereo || launch.stereoFailed) return 0;
    try { return launch.stereo->getButtonPresses(frame, context); }
    catch (const std::exception& e) { failStereo(e.what()); return 0; }
}

extern "C" int __cdecl AFTXR_PointerHand(int hand, float* pose, uint32_t* generation) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!generation) return 0;
    *generation = 0;
    return launch.stereo && pose ? launch.stereo->pointer(hand, pose, *generation) : 0;
}

extern "C" void __cdecl AFTXR_EyeDescription(int eye, float* desc) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (launch.stereo && unsigned(eye)<2 && desc) launch.stereo->describe(eye,desc);
}
extern "C" void __cdecl AFTXR_EyePose(int eye, float* pose) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (launch.stereo && eye >= StereoSession::headAnchor && eye < 2 && pose) launch.stereo->pose(eye,pose);
}
extern "C" void __cdecl AFTXR_EyeProjection(int eye, float nearClip, float farClip, float* matrix) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (launch.stereo && unsigned(eye)<2 && matrix) launch.stereo->projection(eye,nearClip,farClip,matrix);
}
extern "C" int __cdecl AFTXR_ManualRecenterInput() {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.stereo || launch.stereoFailed) return 0;
    try { return static_cast<int>(launch.stereo->manualRecenter()); }
    catch (const std::exception& e) { failStereo(e.what()); return 0; }
}

extern "C" void __cdecl AFTXR_Recenter() {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (launch.stereo) launch.stereo->recenter();
}
extern "C" void __cdecl AFTXR_RenderedEye(int eye, IUnknown* texture, int frame) {
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.stereo || launch.stereoFailed) return;
    try { launch.stereo->renderedEye(eye,texture,frame); }
    catch (const std::exception& e) { failStereo(e.what()); }
}

extern "C" void __cdecl UnityRenderEvent(int eventId) {
    if (eventId != 0x41585234) return;
    std::lock_guard<std::mutex> lock(launchMutex);
    if (!launch.stereo || launch.stereoFailed) return;
    try { launch.stereo->submit(); }
    catch (const std::exception& e) { failStereo(e.what()); }
}
