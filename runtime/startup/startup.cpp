#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// No absolute pointers in this section. The native call sites supply the
// relocated player base and this literal table, so Windows can rebase the game.
struct LaunchText
{
    wchar_t vr[4], adapter[9], dx9[12], dx11[13], suffix[64];
    wchar_t otherRenderers[4][16];
    char narrowDx11[13], prepare[24], variable[32], pending[80], missing[80], incompatible[80];
};
extern "C" const LaunchText launchText = {
    L"-vr", L"-adapter", L"-force-d3d9", L"-force-d3d11", L"Awesome_Data\\Plugins\\AwesomeForTheXR32.dll",
    { L"-force-d3d9-ref", L"-force-opengl", L"-force-gles20", L"-force-gles30" },
    "-force-d3d11", "AFTXR_PrepareLaunch", "AFTXR_LAUNCH_STATUS",
    "AwesomeForTheXR | VR mode disabled (OpenXR renderer pending)",
    "AwesomeForTheXR | VR mode disabled (could not load XR plugin)",
    "AwesomeForTheXR | VR mode disabled (incompatible XR plugin)"
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
    // An accepted request replaces its own slot with D3D11. On early failure
    // Unity uses its ordinary desktop renderer and monitor defaults.
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
    if (!argv || *argc < 1) return;
    auto setStatus = gameImport<decltype(&SetEnvironmentVariableA)>(image, 0x8562c0);
    int request = 1;
    while (request < *argc && !sameFlag(argv[request], text.vr)) ++request;
    if (request == *argc)
    {
        setStatus(text.variable, nullptr);
        return;
    }

    const char* reason = text.missing;
    bool ready = false;
    if (HMODULE plugin = findPlugin(image, text))
    {
        using Prepare = int (__cdecl*)();
        auto prepare = reinterpret_cast<Prepare>(gameImport<decltype(&GetProcAddress)>(image, 0x856114)(
            plugin, text.prepare));
        reason = prepare ? text.pending : text.incompatible;
        ready = prepare && prepare() == 1;
    }
    setStatus(text.variable, ready ? nullptr : reason);

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
