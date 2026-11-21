#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    std::vector<wchar_t> path(32768);
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length == path.size()) return ERROR_BAD_PATHNAME;
    const std::wstring self(path.data(), length);
    const std::wstring directory = self.substr(0, self.find_last_of(L"\\/") + 1);
    const std::wstring player = directory + L"Awesome.exe";
    std::wstring command = L"\"" + player + L"\" -vr";

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child = {};
    if (!CreateProcessW(player.c_str(), &command[0], nullptr, nullptr, FALSE, 0,
                        nullptr, directory.c_str(), &startup, &child))
    {
        const DWORD error = GetLastError();
        const std::wstring message = L"Could not start " + player + L" (Windows error " +
                                     std::to_wstring(error) + L").";
        MessageBoxW(nullptr, message.c_str(), L"AwesomeForTheXR", MB_OK | MB_ICONERROR);
        return static_cast<int>(error);
    }
    CloseHandle(child.hThread);
    DWORD code = ERROR_GEN_FAILURE;
    if (WaitForSingleObject(child.hProcess, INFINITE) == WAIT_OBJECT_0)
        GetExitCodeProcess(child.hProcess, &code);
    CloseHandle(child.hProcess);
    return static_cast<int>(code);
}
