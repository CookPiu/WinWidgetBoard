#include "WorkspacePanelProcess.h"

#include <array>
#include <vector>

namespace winwidgetboard::launcher
{
namespace
{
constexpr wchar_t kPanelExecutableName[] = L"WinWidgetBoard.WorkspacePanel.exe";
constexpr wchar_t kPanelPathEnvironmentVariable[] =
    L"WINWIDGETBOARD_WORKSPACE_PANEL";
constexpr DWORD kPanelSmokeTimeoutMilliseconds = 15000;

struct PanelWindowSearchContext
{
    DWORD processId;
    HWND window;
};

bool IsUsableExecutablePath(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool GetEnvironmentValue(const wchar_t* name, std::wstring& value)
{
    const DWORD requiredLength = GetEnvironmentVariableW(name, nullptr, 0);
    if (requiredLength == 0)
    {
        value.clear();
        return false;
    }

    std::vector<wchar_t> buffer(requiredLength);
    const DWORD copiedLength = GetEnvironmentVariableW(
        name,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (copiedLength == 0 || copiedLength >= buffer.size())
    {
        value.clear();
        return false;
    }

    value.assign(buffer.data(), copiedLength);
    return true;
}

bool GetModuleDirectory(std::wstring& directory, std::wstring& error)
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetModuleFileNameW(
        nullptr,
        buffer.data(),
        static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
    {
        error = L"GetModuleFileNameW failed";
        return false;
    }

    const std::wstring modulePath(buffer.data(), length);
    const std::wstring::size_type separator = modulePath.find_last_of(L"\\/");
    if (separator == std::wstring::npos)
    {
        error = L"LauncherHost module directory is unavailable";
        return false;
    }

    directory = modulePath.substr(0, separator);
    return true;
}

std::wstring QuoteExecutablePath(const std::wstring& path)
{
    return L"\"" + path + L"\"";
}
}

WorkspacePanelProcess::~WorkspacePanelProcess()
{
    Shutdown();
}

// A resident panel never exits on its own, so the launcher must take it down with itself
// rather than leaving an orphan behind.
void WorkspacePanelProcess::Shutdown()
{
    Poll();
    if (!IsRunning())
    {
        Reap();
        return;
    }

    // There is no separate quit protocol: WM_CLOSE now hides the panel rather than ending
    // it. Post it anyway so the panel runs its close path and settles, then terminate. The
    // panel holds no unflushed storage of its own - CoreBroker owns the database - so the
    // worst case is one debounced note autosave that had not been sent yet.
    std::wstring error;
    RequestClose(error);
    WaitForSingleObject(_process, 500);
    if (IsRunning())
    {
        TerminateProcess(_process, 0);
        WaitForSingleObject(_process, 2000);
    }

    Reap();
}

bool WorkspacePanelProcess::Toggle(
    const bool open,
    const MonitorSnapshot& snapshot,
    const RECT& launcherRect,
    const UINT dpi,
    std::wstring& error)
{
    error.clear();
    Poll();

    if (open)
    {
        if (IsRunning())
        {
            return ActivatePanelWindow(error);
        }

        return Launch(snapshot, launcherRect, dpi, false, error);
    }

    return RequestClose(error);
}

bool WorkspacePanelProcess::RunSmokeTest(
    const MonitorSnapshot& snapshot,
    const RECT& launcherRect,
    const UINT dpi,
    std::wstring& error)
{
    error.clear();
    Poll();
    if (IsRunning())
    {
        error = L"WorkspacePanel is already running";
        return false;
    }

    if (!Launch(snapshot, launcherRect, dpi, true, error))
    {
        return false;
    }

    const DWORD waitResult = WaitForSingleObject(
        _process,
        kPanelSmokeTimeoutMilliseconds);
    if (waitResult == WAIT_TIMEOUT)
    {
        // The child was created by this smoke test and is safe to clean up here.
        TerminateProcess(_process, 124);
        Reap();
        error = L"WorkspacePanel smoke process did not exit within 15 seconds";
        return false;
    }

    if (waitResult == WAIT_FAILED)
    {
        const DWORD lastError = GetLastError();
        Reap();
        error = L"WaitForSingleObject failed: " + std::to_wstring(lastError);
        return false;
    }

    DWORD exitCode = STILL_ACTIVE;
    if (GetExitCodeProcess(_process, &exitCode) == FALSE)
    {
        const DWORD lastError = GetLastError();
        Reap();
        error = L"GetExitCodeProcess failed: " + std::to_wstring(lastError);
        return false;
    }

    Reap();
    if (exitCode != 0)
    {
        error = L"WorkspacePanel smoke process exited with code " +
            std::to_wstring(exitCode);
        return false;
    }

    return true;
}

bool WorkspacePanelProcess::RunLifecycleSmokeTest(
    const MonitorSnapshot& snapshot,
    const RECT& launcherRect,
    const UINT dpi,
    std::wstring& error)
{
    error.clear();
    Poll();
    if (IsRunning())
    {
        error = L"WorkspacePanel is already running";
        return false;
    }

    if (!Launch(snapshot, launcherRect, dpi, false, error))
    {
        return false;
    }

    const auto CleanupSmokeProcess = [this]()
    {
        // This process was created exclusively by the lifecycle smoke test.
        TerminateProcess(_process, 124);
        Reap();
    };

    HWND window = nullptr;
    for (int attempt = 0; attempt < 150; ++attempt)
    {
        const DWORD waitResult = WaitForSingleObject(_process, 100);
        if (waitResult == WAIT_OBJECT_0)
        {
            DWORD exitCode = STILL_ACTIVE;
            if (GetExitCodeProcess(_process, &exitCode) == FALSE)
            {
                const DWORD lastError = GetLastError();
                Reap();
                error = L"GetExitCodeProcess failed: " +
                    std::to_wstring(lastError);
                return false;
            }

            Reap();
            error = L"WorkspacePanel exited before showing a window with code " +
                std::to_wstring(exitCode);
            return false;
        }

        if (waitResult == WAIT_FAILED)
        {
            const DWORD lastError = GetLastError();
            CleanupSmokeProcess();
            error = L"WaitForSingleObject failed: " +
                std::to_wstring(lastError);
            return false;
        }

        window = FindPanelWindow();
        if (window != nullptr)
        {
            break;
        }
    }

    if (window == nullptr)
    {
        CleanupSmokeProcess();
        error = L"WorkspacePanel lifecycle smoke did not create a top-level window";
        return false;
    }

    // A non-interactive test session may not own the foreground window. The
    // production toggle path still exercises ActivatePanelWindow when a panel
    // process is already running; this smoke path verifies the normal window
    // lifetime without treating foreground activation as an automated pass.
    ShowWindow(window, SW_SHOWNORMAL);
    UpdateWindow(window);

    if (!RequestClose(error))
    {
        CleanupSmokeProcess();
        return false;
    }

    // The panel is resident: closing hides the window and keeps the process warm, so this
    // asserts hidden-but-alive rather than an exit. See ADR-0025.
    bool hidden = false;
    for (int attempt = 0; attempt < 150; ++attempt)
    {
        if (WaitForSingleObject(_process, 100) == WAIT_OBJECT_0)
        {
            Reap();
            error = L"WorkspacePanel lifecycle smoke exited instead of staying resident";
            return false;
        }

        if (!IsPanelVisible())
        {
            hidden = true;
            break;
        }
    }

    if (!hidden)
    {
        CleanupSmokeProcess();
        error = L"WorkspacePanel lifecycle smoke did not hide within 15 seconds";
        return false;
    }

    // Re-showing must bring the same process back rather than launching a new one.
    const DWORD residentProcessId = _processId;
    if (!ActivatePanelWindow(error) && !IsPanelVisible())
    {
        CleanupSmokeProcess();
        return false;
    }

    bool shown = false;
    for (int attempt = 0; attempt < 100; ++attempt)
    {
        if (IsPanelVisible())
        {
            shown = true;
            break;
        }

        Sleep(50);
    }

    if (!shown || _processId != residentProcessId)
    {
        CleanupSmokeProcess();
        error = L"WorkspacePanel lifecycle smoke did not re-show the resident process";
        return false;
    }

    CleanupSmokeProcess();
    return true;
}

void WorkspacePanelProcess::Poll()
{
    if (_process != nullptr &&
        WaitForSingleObject(_process, 0) == WAIT_OBJECT_0)
    {
        Reap();
    }
}

bool WorkspacePanelProcess::Launch(
    const MonitorSnapshot& snapshot,
    const RECT& launcherRect,
    const UINT dpi,
    const bool smokeTest,
    std::wstring& error)
{
    if (!IsValidRect(snapshot.monitorRect) ||
        !IsRectWithin(snapshot.monitorRect, snapshot.workArea) ||
        !IsRectWithin(snapshot.monitorRect, launcherRect))
    {
        error = L"panel launch context contains invalid monitor, work-area or launcher geometry";
        return false;
    }

    std::wstring executablePath;
    if (!ResolveExecutablePath(executablePath, error))
    {
        return false;
    }

    std::wstring commandLine = QuoteExecutablePath(executablePath);
    commandLine += L" ";
    commandLine += RectArgument(L"--monitor-rect", snapshot.monitorRect);
    commandLine += L" ";
    commandLine += RectArgument(L"--work-area", snapshot.workArea);
    commandLine += L" ";
    commandLine += RectArgument(L"--launcher-rect", launcherRect);
    commandLine += L" --dpi=" + std::to_wstring(dpi == 0 ? 96 : dpi);
    if (smokeTest)
    {
        commandLine += L" --smoke-test";
    }

    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    startupInfo.dwFlags = STARTF_USESHOWWINDOW;
    startupInfo.wShowWindow = SW_SHOWNORMAL;

    PROCESS_INFORMATION processInfo{};
    if (CreateProcessW(
            executablePath.c_str(),
            commandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_UNICODE_ENVIRONMENT,
            nullptr,
            nullptr,
            &startupInfo,
            &processInfo) == FALSE)
    {
        const DWORD lastError = GetLastError();
        error = L"CreateProcessW failed: " + std::to_wstring(lastError);
        return false;
    }

    CloseHandle(processInfo.hThread);
    _process = processInfo.hProcess;
    _processId = processInfo.dwProcessId;
    return true;
}

bool WorkspacePanelProcess::ResolveExecutablePath(
    std::wstring& path,
    std::wstring& error) const
{
    std::wstring overridePath;
    if (GetEnvironmentValue(kPanelPathEnvironmentVariable, overridePath))
    {
        if (!IsUsableExecutablePath(overridePath))
        {
            error = L"WINWIDGETBOARD_WORKSPACE_PANEL does not point to a file";
            return false;
        }

        path = overridePath;
        return true;
    }

    std::wstring moduleDirectory;
    if (!GetModuleDirectory(moduleDirectory, error))
    {
        return false;
    }

    path = moduleDirectory + L"\\" + kPanelExecutableName;
    if (!IsUsableExecutablePath(path))
    {
        error = L"WorkspacePanel executable was not found beside LauncherHost; set " +
            std::wstring(kPanelPathEnvironmentVariable) + L" for a development build";
        return false;
    }

    return true;
}

bool WorkspacePanelProcess::IsPanelVisible() const
{
    const HWND window = FindPanelWindow();
    return window != nullptr && IsWindowVisible(window) != FALSE;
}

bool WorkspacePanelProcess::ActivatePanelWindow(std::wstring& error) const
{
    const HWND window = FindPanelWindow();
    if (window == nullptr)
    {
        error = L"WorkspacePanel process is running but has no top-level window";
        return false;
    }

    if (IsIconic(window))
    {
        ShowWindow(window, SW_RESTORE);
    }
    else if (IsWindowVisible(window) == FALSE)
    {
        // The panel hides itself instead of exiting, so re-opening is a show, not a launch.
        // The panel notices the activation below and runs its opening motion. See ADR-0025.
        ShowWindow(window, SW_SHOW);
    }

    if (SetForegroundWindow(window) == FALSE)
    {
        error = L"SetForegroundWindow failed";
        return false;
    }

    return true;
}

bool WorkspacePanelProcess::RequestClose(std::wstring& error) const
{
    if (!IsRunning())
    {
        return true;
    }

    const HWND window = FindPanelWindow();
    if (window == nullptr)
    {
        error = L"WorkspacePanel process is running but has no top-level window";
        return false;
    }

    if (PostMessageW(window, WM_CLOSE, 0, 0) == FALSE)
    {
        error = L"PostMessageW(WM_CLOSE) failed";
        return false;
    }

    return true;
}

HWND WorkspacePanelProcess::FindPanelWindow() const
{
    if (_processId == 0)
    {
        return nullptr;
    }

    PanelWindowSearchContext context{_processId, nullptr};

    EnumWindows(&FindPanelWindowCallback, reinterpret_cast<LPARAM>(&context));
    return context.window;
}

BOOL CALLBACK WorkspacePanelProcess::FindPanelWindowCallback(
    const HWND window,
    const LPARAM parameter)
{
    auto* context = reinterpret_cast<PanelWindowSearchContext*>(parameter);
    DWORD processId = 0;
    GetWindowThreadProcessId(window, &processId);
    if (processId != context->processId || GetWindow(window, GW_OWNER) != nullptr)
    {
        return TRUE;
    }

    context->window = window;
    return FALSE;
}

void WorkspacePanelProcess::Reap()
{
    if (_process != nullptr)
    {
        CloseHandle(_process);
        _process = nullptr;
    }

    _processId = 0;
}

std::wstring WorkspacePanelProcess::RectArgument(
    const wchar_t* name,
    const RECT& rectangle)
{
    return std::wstring(name) + L"=" + std::to_wstring(rectangle.left) + L"," +
        std::to_wstring(rectangle.top) + L"," +
        std::to_wstring(rectangle.right) + L"," +
        std::to_wstring(rectangle.bottom);
}
}
