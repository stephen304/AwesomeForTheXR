#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// No absolute pointers in this section. The native call sites supply the
// relocated player base and this literal table, so Windows can rebase the game.
struct LaunchText
{
    wchar_t vr[4], adapter[9], dx9[12], dx11[13], suffix[64];
    wchar_t otherRenderers[4][16];
    char narrowDx11[13], prepare[24], getStatus[24], abortLaunch[24], title[36], preparedVariable[32], one[2];
    char selectAdapter[32], setDeviceSlot[32], sceneReady[32];
    char missing[80], incompatible[80], stateError[80], creationError[80];
};
extern "C" const LaunchText launchText = {
    L"-vr", L"-adapter", L"-force-d3d9", L"-force-d3d11", L"Awesome_Data\\Plugins\\AwesomeForTheXR32.dll",
    { L"-force-d3d9-ref", L"-force-opengl", L"-force-gles20", L"-force-gles30" },
    "-force-d3d11", "AFTXR_PrepareLaunch", "AFTXR_GetLaunchStatus", "AFTXR_AbortVrLaunch", "AwesomeForTheXR - VR startup failed", "AFTXR_GRAPHICS_PREPARED", "1",
    "AFTXR_SelectUnityAdapter", "AFTXR_SetUnityDeviceSlot", "AFTXR_IsOpenXRSceneReady",
    "The XR plugin could not be loaded. Reapply the game patch.",
    "The XR plugin is incompatible. Reapply the game patch.",
    "The game could not pass its VR startup state to Unity.",
    "Unity could not create a Direct3D 11 device for the headset."
};

template <typename T> static T gameImport(const BYTE* image, DWORD rva)
{
    return *reinterpret_cast<const T*>(image + rva);
}

template <typename Char> static bool sameFlag(const Char* value, const wchar_t* lower)
{
    while (*value && *lower)
    {
        wchar_t c = *value++;
        if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
        if (c != *lower++) return false;
    }
    return *value == *lower;
}

template <typename Char> static bool adapterIndex(const Char* value)
{
    if (*value == L'-' || *value == L'+') ++value;
    if (!*value) return false;
    while (*value >= L'0' && *value <= L'9') ++value;
    return !*value;
}

static HMODULE findPlugin(const BYTE* image, const LaunchText& text)
{
    const DWORD capacity = 32768;
    auto heap = gameImport<decltype(&GetProcessHeap)>(image, 0x8562cc)();
    auto path = static_cast<wchar_t*>(gameImport<decltype(&HeapAlloc)>(image, 0x856210)(
        heap, 0, capacity * sizeof(wchar_t)));
    if (!path) return nullptr;
    DWORD length = gameImport<decltype(&GetModuleFileNameW)>(image, 0x8560fc)(nullptr, path, capacity);
    HMODULE plugin = nullptr;
    if (length && length < capacity)
    {
        while (length && path[length - 1] != L'\\' && path[length - 1] != L'/') --length;
        if (length && length + sizeof(text.suffix) / sizeof(wchar_t) <= capacity)
        {
            for (unsigned i = 0; ; ++i)
            {
                path[length + i] = text.suffix[i];
                if (!text.suffix[i]) break;
            }
            plugin = gameImport<decltype(&GetModuleHandleW)>(image, 0x85615c)(path);
            if (!plugin) plugin = gameImport<decltype(&LoadLibraryW)>(image, 0x856118)(path);
        }
    }
    gameImport<decltype(&HeapFree)>(image, 0x856218)(heap, 0, path);
    return plugin;
}

template <typename Char> static int filterArgs(Char** argv, int count, int request, bool ready,
    const LaunchText& text, const Char* dx11)
{
    // Compact the pointer array while preserving ownership of its strings.
    // Only a prepared request reaches Unity and replaces its own slot with D3D11.
    int out = 1;
    for (int i = 1; i < count; ++i)
    {
        if (sameFlag(argv[i], text.vr))
        {
            if (i == request && ready) argv[out++] = const_cast<Char*>(dx11);
        }
        else if (sameFlag(argv[i], text.adapter))
        {
            if (i + 1 < count && adapterIndex(argv[i + 1])) ++i;
        }
        else if (!sameFlag(argv[i], text.dx9) && !sameFlag(argv[i], text.dx11) &&
                 !sameFlag(argv[i], text.otherRenderers[0]) && !sameFlag(argv[i], text.otherRenderers[1]) &&
                 !sameFlag(argv[i], text.otherRenderers[2]) && !sameFlag(argv[i], text.otherRenderers[3]))
            argv[out++] = argv[i];
    }
    if (out < count) argv[out] = nullptr;
    return out;
}

extern "C" void __cdecl NormalizeAwesomeArgs(wchar_t** argv, int* argc, const BYTE* image, const LaunchText& text)
{
    auto setVariable = gameImport<decltype(&SetEnvironmentVariableA)>(image, 0x8562c0);
    // Do not inherit a prepared VR request from the parent process.
    setVariable(text.preparedVariable, nullptr);
    if (!argv || *argc < 1) return;
    int request = 1;
    while (request < *argc && !sameFlag(argv[request], text.vr)) ++request;
    if (request == *argc) return;

    const char* reason = text.missing;
    if (HMODULE plugin = findPlugin(image, text))
    {
        using Prepare = int (__cdecl*)();
        using Status = const char* (__cdecl*)();
        using Abort = void (__cdecl*)(const char*);
        auto getProc = gameImport<decltype(&GetProcAddress)>(image, 0x856114);
        auto prepare = reinterpret_cast<Prepare>(getProc(plugin, text.prepare));
        auto status = reinterpret_cast<Status>(getProc(plugin, text.getStatus));
        auto abortLaunch = reinterpret_cast<Abort>(getProc(plugin, text.abortLaunch));
        reason = text.incompatible;
        if (prepare && status && abortLaunch && getProc(plugin, text.selectAdapter) &&
            getProc(plugin, text.setDeviceSlot) && getProc(plugin, text.sceneReady))
        {
            if (prepare() == 1)
            {
                if (setVariable(text.preparedVariable, text.one))
                {
                    *argc = filterArgs(argv, *argc, request, true, text, text.dx11);
                    return;
                }
                reason = text.stateError;
            }
            else
            {
                const char* error = status();
                if (error && *error) reason = error;
            }
            abortLaunch(reason);
        }
    }
    // The stock player already imports both calls. This also works without our DLL.
    gameImport<decltype(&MessageBoxA)>(image, 0x8566e4)(nullptr, reason, text.title,
        MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    gameImport<decltype(&ExitProcess)>(image, 0x856228)(1);
}

// The engine reader has already made the startup decision. Mono needs a
// normalized view of the arguments without starting OpenXR a second time.
extern "C" void __cdecl NormalizeAwesomeMonoArgs(wchar_t** argv, int* argc, const BYTE* image, const LaunchText& text)
{
    if (!argv || *argc < 1) return;
    int request = 1;
    while (request < *argc && !sameFlag(argv[request], text.vr)) ++request;
    if (request == *argc) return;
    char value[2] = {};
    const bool ready = gameImport<decltype(&GetEnvironmentVariableA)>(image, 0x856178)(
        text.preparedVariable, value, sizeof(value)) == 1 && value[0] == '1';
    *argc = filterArgs(argv, *argc, request, ready, text, text.dx11);
}

// Unity retains this separate narrow vector for the process lifetime. Keep the
// CRT's own array intact and share its immutable argument strings.
extern "C" void __cdecl NormalizeAwesomeEngineArgs(int* argc, char*** values, wchar_t** wide,
    int wideCount, const BYTE* image, const LaunchText& text)
{
    char** argv = *values;
    if (!argv || *argc < 1) return;
    int request = 1;
    while (request < *argc && !sameFlag(argv[request], text.vr)) ++request;
    if (request == *argc) return;

    bool ready = false;
    for (int i = 1; wide && i < wideCount; ++i)
        if (sameFlag(wide[i], text.dx11)) ready = true;
    auto heap = gameImport<decltype(&GetProcessHeap)>(image, 0x8562cc)();
    auto copy = static_cast<char**>(gameImport<decltype(&HeapAlloc)>(image, 0x856210)(
        heap, 0, (*argc + 1) * sizeof(char*)));
    if (copy)
    {
        for (int i = 0; i < *argc; ++i) copy[i] = argv[i];
        copy[*argc] = nullptr;
        *values = copy;
    }
    // If the pointer array allocation fails, compact the existing array.
    // No argument string is allocated or freed by this operation.
    *argc = filterArgs(*values, *argc, request, ready, text, text.narrowDx11);
}

struct ID3D11Device;

// The fingerprinted player uses a zero-based IDXGIFactory::EnumAdapters index,
// and stores the device returned by D3D11CreateDevice in this process-wide slot.
extern "C" bool __cdecl InitializeAwesomeD3D11(BYTE* image, const LaunchText& text)
{
    auto initialize = reinterpret_cast<bool (__cdecl*)()>(image + 0x407540);
    char value[2] = {};
    if (gameImport<decltype(&GetEnvironmentVariableA)>(image, 0x856178)(
            text.preparedVariable, value, sizeof(value)) != 1 || value[0] != '1')
        return initialize();

    auto plugin = findPlugin(image, text);
    auto getProc = gameImport<decltype(&GetProcAddress)>(image, 0x856114);
    if (plugin)
    {
        auto selectAdapter = reinterpret_cast<int (__cdecl*)()>(getProc(plugin, text.selectAdapter));
        auto setDeviceSlot = reinterpret_cast<void (__cdecl*)(ID3D11Device* const*)>(getProc(plugin, text.setDeviceSlot));
        auto status = reinterpret_cast<const char* (__cdecl*)()>(getProc(plugin, text.getStatus));
        auto abortLaunch = reinterpret_cast<void (__cdecl*)(const char*)>(getProc(plugin, text.abortLaunch));
        if (selectAdapter && setDeviceSlot && status && abortLaunch)
        {
            int index = selectAdapter();
            const char* reason = text.creationError;
            if (index < 0) reason = status();
            else
            {
                *reinterpret_cast<int*>(image + 0xa2b498) = index;
                if (initialize())
                {
                    // Pass the location only. Managed scene startup reads it after the selector.
                    setDeviceSlot(reinterpret_cast<ID3D11Device* const*>(image + 0xa2b4b0));
                    return true;
                }
            }
            abortLaunch(reason);
        }
    }
    gameImport<decltype(&MessageBoxA)>(image, 0x8566e4)(nullptr, text.incompatible, text.title,
        MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    gameImport<decltype(&ExitProcess)>(image, 0x856228)(1);
    return false;
}
