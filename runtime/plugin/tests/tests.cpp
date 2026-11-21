#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <array>
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
#include "../src/headset_retry.h"

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
    unsigned systemQueries;
    unsigned graphicsQueries;
    const char* reason;
    const char* diagnostic;
};

const Expected cases[] = {
    {"success",            true,  true,   1, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_SUCCESS"},
    {"late-headset",       true,  true,   3, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_SUCCESS"},
    {"destroy-failure",    true,  true,   1, 1, "OpenXR graphics requirements are ready.",             "xrDestroyInstance: XR_ERROR_RUNTIME_FAILURE"},
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

void testHeadsetTimeout() {
    unsigned elapsed = 0, queries = 0;
    const auto result = waitForHeadset(
        [&] { ++queries; return XR_ERROR_FORM_FACTOR_UNAVAILABLE; },
        [&] { return 1000u + elapsed; },
        [&](unsigned delay) {
            check(delay == 250 && elapsed < 10000, "incorrect headset retry interval or deadline");
            elapsed += delay;
        });
    check(result == XR_ERROR_FORM_FACTOR_UNAVAILABLE && elapsed == 10000 && queries == 41, "incorrect headset timeout");
}

// All channels are either zero or full intensity so comparison is exact in sRGB.
using Pixels = std::array<UINT, 4>;

void checkEyePixels(ID3D11Device* device, ID3D11Texture2D* output, const Pixels& source) {
    check(output != nullptr, "missing eye swapchain");
    D3D11_TEXTURE2D_DESC desc{};
    output->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> copy;
    check(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &copy)), "staging eye texture");
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    device->GetImmediateContext(&context);
    context->CopyResource(copy.Get(), output);
    D3D11_MAPPED_SUBRESOURCE read{};
    check(SUCCEEDED(context->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &read)), "read submitted eye");
    const auto* pixels = static_cast<const BYTE*>(read.pData);
    const bool flipped = std::memcmp(pixels, source.data() + 2, 2 * sizeof(UINT)) == 0 &&
                         std::memcmp(pixels + read.RowPitch, source.data(), 2 * sizeof(UINT)) == 0;
    context->Unmap(copy.Get(), 0);
    check(flipped, "wrong eye or vertical orientation in submitted texture");
}

void testFrames(HMODULE plugin, ID3D11Device* device, Fixture& state, bool loseSession) {
    auto begin = entry<int (__cdecl*)(int)>(plugin, "AFTXR_BeginCameraFrame");
    auto submit = entry<void (__cdecl*)(int)>(plugin, "UnityRenderEvent");
    constexpr int endFrameEvent = 0x41585234;
    state.nextEvent = XR_SESSION_STATE_READY;
    if (loseSession) {
        check(begin(1) == 1 && state.starts == 1, "session never started");
        submit(endFrameEvent);
        state.nextEvent = XR_SESSION_STATE_LOSS_PENDING;
        check(begin(2) == -1, "session loss must use the fatal path");
        auto status = entry<const char* (__cdecl*)()>(plugin, "AFTXR_GetLaunchStatus");
        check(std::strstr(status(), "session was lost") != nullptr, "missing session loss diagnostic");
        return;
    }

    auto rendered = entry<void (__cdecl*)(int, IUnknown*, int)>(plugin, "AFTXR_RenderedEye");
    const Pixels colors[] = {{0xff0000ff, 0xff00ffff, 0xffff0000, 0xffff00ff},
                             {0xff00ff00, 0xffffff00, 0xffffffff, 0xff000000}};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> eyes[2];
    for (unsigned i = 0; i < 2; ++i) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = desc.Height = 2;
        desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{colors[i].data(), 2 * sizeof(UINT)};
        check(SUCCEEDED(device->CreateTexture2D(&desc, &data, &eyes[i])), "Unity eye texture");
    }
    auto stereoFrame = [&](int frame) {
        check(begin(frame) == 1, "failed to begin stereo frame");
        for (int i = 0; i < 2; ++i) rendered(i, eyes[i].Get(), frame);
        submit(endFrameEvent);
        for (int i = 0; i < 2; ++i) checkEyePixels(device, state.eyeImages[i], colors[i]);
    };

    stereoFrame(1);
    check(state.starts == 1 && state.projections == 1 && state.releases == 2, "first stereo projection");
    check(begin(2) == 1, "begin incomplete frame");
    rendered(0, eyes[0].Get(), 2);
    submit(endFrameEvent);
    check(state.emptyFrames == 1 && state.projections == 1 && state.releases == 2, "submitted a stale eye");

    state.nextEvent = XR_SESSION_STATE_STOPPING;
    check(begin(3) == 1 && begin(4) == 1, "temporary stop quit the game");
    submit(endFrameEvent);
    check(state.stops == 1 && state.frameWaits == 2 && state.emptyFrames == 1, "rendered while idle");
    state.nextEvent = XR_SESSION_STATE_READY;
    stereoFrame(5);
    check(state.starts == 2 && state.frameWaits == 3 && state.projections == 2 && state.releases == 4, "resume failed");

    state.nextEvent = XR_SESSION_STATE_STOPPING;
    check(begin(6) == 1, "quit before EXITING");
    state.nextEvent = XR_SESSION_STATE_EXITING;
    check(begin(7) == 0 && state.stops == 2 && state.frameWaits == 3, "runtime exit did not quit cleanly");
}

void testDevice(const wchar_t* pluginPath, const std::filesystem::path& folder, const std::string& scenario) {
    using Microsoft::WRL::ComPtr;
    ComPtr<IDXGIFactory1> factory;
    check(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))), "DXGI factory");
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
        UINT flags = scenario == "device-single-thread" ? D3D11_CREATE_DEVICE_SINGLETHREADED : 0;
        if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, nullptr, 0,
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
    state.expectedDevice = device.Get();
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
    if (scenario == "stereo-resume-exit" || scenario == "stereo-loss") {
        const int accepted = ready();
        check(accepted == 1, status());
        testFrames(plugin, device.Get(), state, scenario == "stereo-loss");
        finish();
        check(state.swapchainDestroys == state.swapchainCreates && state.sessionDestroys == 1 && state.destroys == 1 &&
              !state.invalidCall, "incorrect stereo lifetime or OpenXR call order");
        check(refs() == before, "device reference survived stereo cleanup");
        FreeLibrary(plugin);
        FreeLibrary(fixture);
        return;
    }
    check(ready() == 0, "scene opened before session creation");
    const bool valid = scenario == "device-match";
    const char* expected = "OpenXR camera rendering failed. xrCreateSession: XR_ERROR_GRAPHICS_DEVICE_INVALID (-38)";
    if (scenario == "device-single-thread") expected = "Unity created a single-threaded D3D11 device that cannot be shared with OpenXR.";
    if (missing) expected = "Unity did not provide a Direct3D 11 device for the headset.";
    if (scenario == "device-wrong-adapter") expected = "Unity created its Direct3D 11 device on a different GPU than the headset requires.";
    if (scenario == "device-low-level") expected = "Unity's Direct3D 11 device does not meet the headset's minimum feature level.";
    check(std::string(status()) == expected, "wrong managed startup diagnostic");
    check(state.sessionCreates == (valid ? 1u : 0u), "incorrect number of OpenXR session creations");
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
    for (int i = 0; i < 8; ++i) {
        callers.emplace_back([&] {
            while (!start.load()) std::this_thread::yield();
            if (prepare() == static_cast<int>(test.accepted)) ++matches;
        });
    }
    start = true;
    for (auto& caller : callers) caller.join();
    check(matches == 8 && state.creates == 1, "callers disagreed or created more than one instance");
    check(!state.invalidCall, "unexpected API arguments");
    check(state.destroys == static_cast<unsigned>(test.createdInstance && !test.accepted), "wrong instance lifetime after preparation");
    check(state.systemQueries == test.systemQueries, "wrong headset retry count");
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
        if (scenario == "no-headset") {
            testHeadsetTimeout();
            return 0;
        }
        if (scenario.find("device-") == 0 || scenario.find("stereo-") == 0) {
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