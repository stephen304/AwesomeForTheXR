#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "fixture.h"

namespace {
void check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

template<class Fn> Fn entry(HMODULE module, const char* name) {
    auto address = GetProcAddress(module, name);
    check(address != nullptr, name);
    return reinterpret_cast<Fn>(address);
}

struct Expected {
    const char* scenario;
    bool accepted, createdInstance;
    int systemQueries; // -1 means poll until the real deadline.
    unsigned graphicsQueries;
    const char* reason;
    const char* diagnostic;
};

const Expected cases[] = {
    {"success",            true,  true,   1, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_SUCCESS"},
    {"late-headset",       true,  true,   3, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_SUCCESS"},
    {"destroy-failure",    true,  true,   1, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_ERROR_RUNTIME_FAILURE"},
    {"no-headset",         false, true,  -1, 0, "No VR headset is available. Check that your headset is connected and ready.",                 "xrGetSystem: XR_ERROR_FORM_FACTOR_UNAVAILABLE"},
    {"unsupported-headset",false, true,   1, 0, "No VR headset is available. Check that your headset is connected and ready.",                 "xrGetSystem: XR_ERROR_FORM_FACTOR_UNSUPPORTED"},
    {"no-runtime",         false, false,  0, 0, "The 32-bit OpenXR runtime is unavailable. Check your active OpenXR runtime.",   "xrCreateInstance: XR_ERROR_RUNTIME_UNAVAILABLE"},
    {"no-api",             false, false,  0, 0, "The active runtime does not support OpenXR 1.0.",              "xrCreateInstance: XR_ERROR_API_VERSION_UNSUPPORTED"},
    {"no-extension",       false, false,  0, 0, "The active OpenXR runtime does not support Direct3D 11.", "xrCreateInstance: XR_ERROR_EXTENSION_NOT_PRESENT"},
    {"runtime-properties", false, true,   0, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "xrGetInstanceProperties: XR_ERROR_RUNTIME_FAILURE"},
    {"system-properties",  false, true,   1, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "xrGetSystemProperties: XR_ERROR_SYSTEM_INVALID"},
    {"lookup",             false, true,   1, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "Resolve xrGetD3D11GraphicsRequirementsKHR: XR_ERROR_FUNCTION_UNSUPPORTED"},
    {"null-entry",         false, true,   1, 0, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "no function address for xrGetD3D11GraphicsRequirementsKHR"},
    {"requirements",       false, true,   1, 1, "OpenXR startup failed. See AwesomeForTheXR-launcher.log for details.",               "xrGetD3D11GraphicsRequirementsKHR: XR_ERROR_RUNTIME_FAILURE"},
};

void testDevice(const wchar_t* pluginPath, const std::filesystem::path& folder, const std::string& scenario) {
    using Microsoft::WRL::ComPtr;
    ComPtr<IDXGIFactory> factory;
    check(SUCCEEDED(CreateDXGIFactory(IID_PPV_ARGS(&factory))), "DXGI factory");
    // Prefer a non-default adapter if it can create a device. This also covers
    // the Basic Render Driver on machines exposing it through this DXGI interface.
    UINT index = 0;
    ComPtr<ID3D11Device> device;
    DXGI_ADAPTER_DESC desc{};
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter> adapter;
        HRESULT hr = factory->EnumAdapters(i, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        check(SUCCEEDED(hr), "DXGI enumeration");
        ComPtr<ID3D11Device> candidate;
        if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
                                     D3D11_SDK_VERSION, &candidate, nullptr, nullptr))) continue;
        check(SUCCEEDED(adapter->GetDesc(&desc)), "adapter description");
        device = candidate;
        index = i;
        if (i > 0) break;
    }
    check(device.Get() != nullptr, "no test D3D11 device");

    auto fixture = LoadLibraryW((folder / "openxr_loader.dll").c_str());
    auto plugin = LoadLibraryW(pluginPath);
    check(fixture && plugin, "graphics test DLLs");
    Fixture state{scenario.c_str()};
    state.adapterLuid = desc.AdapterLuid;
    state.minimumLevel = device->GetFeatureLevel();
    if (scenario == "device-wrong-adapter") state.adapterLuid.HighPart ^= 0x40000000;
    if (scenario == "device-low-level") state.minimumLevel = D3D_FEATURE_LEVEL_12_2;
    entry<void (__cdecl*)(Fixture*)>(fixture, "BindPreflightFixture")(&state);
    auto select = entry<int (__cdecl*)()>(plugin, "AFTXR_SelectUnityAdapter");
    auto setSlot = entry<void (__cdecl*)(ID3D11Device* const*)>(plugin, "AFTXR_SetUnityDeviceSlot");
    auto ready = entry<int (__cdecl*)()>(plugin, "AFTXR_IsOpenXRSceneReady");
    auto status = entry<const char* (__cdecl*)()>(plugin, "AFTXR_GetLaunchStatus");
    auto finish = entry<void (__cdecl*)()>(plugin, "AFTXR_ReleaseLaunch");
    auto refs = [&] { device->AddRef(); return device->Release(); };
    auto before = refs();
    ID3D11Device* current = nullptr;
    check(select() == -1 && ready() == 0, "graphics accepted before preparation");
    check(entry<int (__cdecl*)()>(plugin, "AFTXR_PrepareLaunch")() == 1, "prepare graphics fixture");
    if (scenario == "device-wrong-adapter") {
        check(select() == -1 && strstr(status(), "unavailable"), "could not reject missing adapter");
        // Independently exercise the scene check with an actual device on the wrong GPU.
    } else {
        check(select() == static_cast<int>(index), "incorrect Unity adapter index");
    }

    // Unity owns the slot. Its contents at registration must never be acquired or cached.
    const bool missing = scenario == "device-null";
    current = missing ? device.Get() : nullptr;
    setSlot(&current);
    check(refs() == before, "took a Unity device reference before managed startup");
    current = missing ? nullptr : device.Get();
    check(ready() == 0, "scene opened before session creation");
    const bool valid = scenario == "device-match";
    const char* expected = "Headset rendering is not implemented in this build.";
    if (missing) expected = "Unity did not provide a Direct3D 11 device for the headset.";
    if (scenario == "device-wrong-adapter") expected = "Unity created its Direct3D 11 device on a different GPU than the headset requires.";
    if (scenario == "device-low-level") expected = "Unity's Direct3D 11 device does not meet the headset's minimum feature level.";
    check(std::string(status()) == expected, "wrong managed startup diagnostic");
    check(refs() == before + (valid ? 1 : 0), "incorrect device ownership after validation");

    current = missing ? device.Get() : nullptr;
    check(ready() == 0 && std::string(status()) == expected, "repeated scene check changed its result");
    std::ifstream input(folder / "AwesomeForTheXR-launcher.log");
    std::string log{std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
    const char* marker = "Managed startup: validating Unity's D3D11 device.";
    check(log.find(marker) != log.npos && log.find(marker) == log.rfind(marker), "expected exactly one device check");
    finish();
    check(refs() == before, "device reference survived cleanup");
    check(state.destroys == 1 && !state.invalidCall && select() == -1, "graphics cleanup");
    FreeLibrary(plugin);
    FreeLibrary(fixture);
}

void run(const wchar_t* pluginPath, const std::filesystem::path& folder, const Expected& test) {
    // Hold the fixture while reading counters, then drop our reference to check
    // that the production plugin really released its own after startup cleanup.
    auto fixture = LoadLibraryW((folder / "openxr_loader.dll").c_str());
    auto plugin = LoadLibraryW(pluginPath);
    check(fixture && plugin, "could not load test fixture or production plugin");
    Fixture state{test.scenario};
    entry<void (__cdecl*)(Fixture*)>(fixture, "BindPreflightFixture")(&state);
    auto prepare = entry<int (__cdecl*)()>(plugin, "AFTXR_PrepareLaunch");
    auto finish = entry<void (__cdecl*)()>(plugin, "AFTXR_ReleaseLaunch");
    auto status = entry<const char* (__cdecl*)()>(plugin, "AFTXR_GetLaunchStatus");

    finish(); // Cleanup is safe even before any request.
    std::atomic<bool> start{false};
    std::atomic<int> matches{0};
    std::vector<std::thread> callers;
    auto begin = GetTickCount64();
    for (int i = 0; i < 8; ++i) {
        callers.emplace_back([&] {
            while (!start.load()) std::this_thread::yield();
            if (prepare() == static_cast<int>(test.accepted)) ++matches;
        });
    }
    start = true;
    for (auto& caller : callers) caller.join();
    auto elapsed = GetTickCount64() - begin;
    check(matches == 8 && state.creates == 1, "callers disagreed or created more than one instance");
    check(!state.invalidCall, "unexpected API arguments");
    check(state.destroys == static_cast<unsigned>(test.createdInstance && !test.accepted), "wrong instance lifetime after preparation");
    if (test.systemQueries < 0) {
        check(elapsed >= 10000 && elapsed < 25000 && state.systemQueries > 1 && state.systemQueries <= 42,
              "headset retry did not respect the deadline and polling interval");
    } else {
        check(state.systemQueries == static_cast<unsigned>(test.systemQueries), "wrong headset retry count");
    }
    check(state.graphicsQueries == test.graphicsQueries, "wrong graphics query count");
    check(std::string(status()) == test.reason, "unexpected launch status");

    finish();
    finish();
    check(prepare() == 0 && state.creates == 1 && state.destroys == static_cast<unsigned>(test.createdInstance),
          "cleanup was repeated or preparation restarted after cleanup");
    check(!state.invalidCall, "invalid instance passed to cleanup");
    bool destroyFailed = std::string(test.scenario) == "destroy-failure";
    FreeLibrary(fixture);
    check((GetModuleHandleW(L"openxr_loader.dll") != nullptr) == destroyFailed, "unexpected loader lifetime");
    FreeLibrary(plugin);

    std::ifstream logFile(folder / "AwesomeForTheXR-launcher.log");
    std::string log{std::istreambuf_iterator<char>(logFile), std::istreambuf_iterator<char>()};
    check(log.find("PID " + std::to_string(GetCurrentProcessId()) + "\n") != std::string::npos, "missing fresh log");
    check(log.find(test.diagnostic) != log.npos, "missing expected OpenXR diagnostic");
    if (test.accepted)
        check(log.find("00001234:ABCD1234") != log.npos && log.find("0xB000") != log.npos, "wrong graphics data in log");
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        check(argc == 3, "expected plugin path and scenario");
        auto folder = std::filesystem::absolute(argv[0]).parent_path();
        auto scenario = std::filesystem::path(argv[2]).string();
        if (scenario.find("device-") == 0) {
            testDevice(argv[1], folder, scenario);
            return 0;
        }
        for (const auto& test : cases) {
            if (scenario == test.scenario) {
                run(argv[1], folder, test);
                return 0;
            }
        }
        throw std::runtime_error("unknown preflight test case");
    } catch (const std::exception& e) {
        std::fprintf(stderr, "%s\n", e.what());
        return 1;
    }
}