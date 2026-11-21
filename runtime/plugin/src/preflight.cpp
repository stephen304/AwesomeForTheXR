#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_NO_PROTOTYPES
#include <windows.h>
#include <d3d11.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_reflection.h>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>

namespace {
static_assert(sizeof(void*) == 4, "Awesome requires the Win32 OpenXR loader.");

struct Launch {
    bool attempted = false;
    bool prepared = false;
    HMODULE loader = nullptr;
    XrInstance instance = XR_NULL_HANDLE;
    XrSystemId system = XR_NULL_SYSTEM_ID;
    XrGraphicsRequirementsD3D11KHR graphics{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
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

using Log = std::unique_ptr<FILE, decltype(&fclose)>;
Log openLog(const std::wstring& directory, const wchar_t* mode) {
    FILE* file = nullptr;
    if (!directory.empty()) _wfopen_s(&file, (directory + L"AwesomeForTheXR-launcher.log").c_str(), mode);
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
    PFN_xrGetInstanceProcAddr getProc;
    if (!resolve(log, "xrCreateInstance", createInstance) || !resolve(log, "xrDestroyInstance", launch.destroy) ||
        !resolve(log, "xrGetInstanceProperties", getInstanceProperties) || !resolve(log, "xrGetSystem", getSystem) ||
        !resolve(log, "xrGetSystemProperties", getSystemProperties) || !resolve(log, "xrGetInstanceProcAddr", getProc)) return false;

    const char* extension = XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
    XrInstanceCreateInfo info{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(info.applicationInfo.applicationName, "AwesomeForTheXR");
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
    const ULONGLONG deadline = GetTickCount64() + 10000;
    XrResult result;
    for (;;) {
        result = getSystem(instance, &systemInfo, &launch.system);
        if (result != XR_ERROR_FORM_FACTOR_UNAVAILABLE || GetTickCount64() >= deadline) break;
        Sleep(250);
    }
    if (!checked(log, "xrGetSystem", result)) return false;
    XrSystemProperties headset{XR_TYPE_SYSTEM_PROPERTIES};
    if (!checked(log, "xrGetSystemProperties", getSystemProperties(instance, launch.system, &headset))) return false;
    if (log) fprintf(log, "Headset: %s\n", headset.systemName);

    PFN_xrVoidFunction entry = nullptr;
    if (!checked(log, "Resolve xrGetD3D11GraphicsRequirementsKHR",
                 getProc(instance, "xrGetD3D11GraphicsRequirementsKHR", &entry))) return false;
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
