#include <windows.h>
#include <shellapi.h>

#include "ChildProcessJob.h"
#include "CoreBrokerClient.h"
#include "CoreBrokerProcess.h"
#include "EntryVisual.h"
#include "LauncherWindow.h"
#include "TaskbarGeometry.h"
#include "WorkspacePanelProcess.h"

#include <cstdio>
#include <string>
#include <string_view>

namespace
{
constexpr wchar_t kWindowClassName[] = L"WinWidgetBoard.LauncherHost.MessageWindow";

enum class ExitCode : int
{
    Success = 0,
    DpiAwarenessFailed = 10,
    CommandLineFailed = 11,
    WindowClassRegistrationFailed = 12,
    WindowCreationFailed = 13,
    WindowDestructionFailed = 14,
    MessageLoopFailed = 15,
    GeometrySmokeTestFailed = 16,
    LauncherWindowCreationFailed = 17,
    PanelLaunchSmokeTestFailed = 18,
    CoreBrokerSmokeTestFailed = 19,
    EntryVisualSmokeTestFailed = 20,
};

std::wstring FormatWin32Error(const DWORD error)
{
    wchar_t* messageBuffer = nullptr;
    constexpr DWORD flags =
        FORMAT_MESSAGE_ALLOCATE_BUFFER |
        FORMAT_MESSAGE_FROM_SYSTEM |
        FORMAT_MESSAGE_IGNORE_INSERTS;

    const DWORD length = FormatMessageW(
        flags,
        nullptr,
        error,
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<wchar_t*>(&messageBuffer),
        0,
        nullptr);

    std::wstring message;
    if (length != 0 && messageBuffer != nullptr)
    {
        message.assign(messageBuffer, length);
        while (!message.empty() && (message.back() == L'\r' || message.back() == L'\n'))
        {
            message.pop_back();
        }
    }
    else
    {
        message = L"Unknown Win32 error";
    }

    if (messageBuffer != nullptr)
    {
        LocalFree(messageBuffer);
    }

    return message;
}

int FailWin32(
    const ExitCode exitCode,
    const std::wstring_view context,
    const DWORD error)
{
    const std::wstring message = FormatWin32Error(error);
    wchar_t diagnostic[1024]{};
    _snwprintf_s(
        diagnostic,
        _countof(diagnostic),
        _TRUNCATE,
        L"WinWidgetBoard.LauncherHost: %.*s failed (GetLastError=%lu): %s\n",
        static_cast<int>(context.size()),
        context.data(),
        error,
        message.c_str());

    OutputDebugStringW(diagnostic);
    fputws(diagnostic, stderr);
    return static_cast<int>(exitCode);
}

int FailDiagnostic(
    const ExitCode exitCode,
    const std::wstring_view context,
    const std::wstring_view detail)
{
    wchar_t diagnostic[1024]{};
    _snwprintf_s(
        diagnostic,
        _countof(diagnostic),
        _TRUNCATE,
        L"WinWidgetBoard.LauncherHost: %.*s: %.*s\n",
        static_cast<int>(context.size()),
        context.data(),
        static_cast<int>(detail.size()),
        detail.data());

    OutputDebugStringW(diagnostic);
    fputws(diagnostic, stderr);
    return static_cast<int>(exitCode);
}

LRESULT CALLBACK MessageWindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam)
{
    switch (message)
    {
    case WM_CLOSE:
        DestroyWindow(window);
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    default:
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

struct CommandLineOptions
{
    bool smokeTest{};
    bool geometrySmokeTest{};
    bool panelLaunchSmokeTest{};
    bool panelLifecycleSmokeTest{};
    bool coreBrokerSmokeTest{};
    bool entryVisualSmokeTest{};
    bool noBroker{};
};

bool ReadCommandLineOptions(CommandLineOptions& options, DWORD& error)
{
    int argumentCount = 0;
    wchar_t** arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    if (arguments == nullptr)
    {
        error = GetLastError();
        return false;
    }

    for (int index = 1; index < argumentCount; ++index)
    {
        if (std::wstring_view(arguments[index]) == L"--smoke-test")
        {
            options.smokeTest = true;
        }
        else if (std::wstring_view(arguments[index]) == L"--geometry-smoke-test")
        {
            options.geometrySmokeTest = true;
        }
        else if (std::wstring_view(arguments[index]) == L"--panel-launch-smoke-test")
        {
            options.panelLaunchSmokeTest = true;
        }
        else if (std::wstring_view(arguments[index]) == L"--panel-lifecycle-smoke-test")
        {
            options.panelLifecycleSmokeTest = true;
        }
        else if (std::wstring_view(arguments[index]) == L"--corebroker-smoke-test")
        {
            options.coreBrokerSmokeTest = true;
        }
        else if (std::wstring_view(arguments[index]) == L"--entry-visual-smoke-test")
        {
            options.entryVisualSmokeTest = true;
        }
        else if (std::wstring_view(arguments[index]) == L"--no-broker")
        {
            options.noBroker = true;
        }
    }

    LocalFree(arguments);
    error = ERROR_SUCCESS;
    return true;
}
}

int WINAPI wWinMain(
    const HINSTANCE instance,
    HINSTANCE,
    PWSTR,
    int)
{
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    {
        const DWORD error = GetLastError();
        return FailWin32(ExitCode::DpiAwarenessFailed, L"SetProcessDpiAwarenessContext", error);
    }

    DWORD commandLineError = ERROR_SUCCESS;
    CommandLineOptions commandLineOptions{};
    if (!ReadCommandLineOptions(commandLineOptions, commandLineError) ||
        commandLineError != ERROR_SUCCESS)
    {
        return FailWin32(ExitCode::CommandLineFailed, L"CommandLineToArgvW", commandLineError);
    }

    if (commandLineOptions.smokeTest || commandLineOptions.geometrySmokeTest)
    {
        std::wstring geometryFailure;
        if (!winwidgetboard::launcher::RunGeometryContractSmokeTest(geometryFailure))
        {
            return FailDiagnostic(
                ExitCode::GeometrySmokeTestFailed,
                L"geometry smoke test failed",
                geometryFailure);
        }

        if (commandLineOptions.geometrySmokeTest && !commandLineOptions.smokeTest)
        {
            return static_cast<int>(ExitCode::Success);
        }
    }

    if (commandLineOptions.smokeTest || commandLineOptions.entryVisualSmokeTest)
    {
        std::wstring visualFailure;
        if (!winwidgetboard::launcher::RunEntryVisualSmokeTest(visualFailure))
        {
            return FailDiagnostic(
                ExitCode::EntryVisualSmokeTestFailed,
                L"entry visual smoke test failed",
                visualFailure);
        }

        if (commandLineOptions.entryVisualSmokeTest && !commandLineOptions.smokeTest)
        {
            return static_cast<int>(ExitCode::Success);
        }
    }

    if (commandLineOptions.panelLaunchSmokeTest ||
        commandLineOptions.panelLifecycleSmokeTest)
    {
        const HMONITOR monitor =
            MonitorFromWindow(nullptr, MONITOR_DEFAULTTOPRIMARY);
        winwidgetboard::launcher::MonitorSnapshot snapshot{};
        std::wstring snapshotError;
        if (!winwidgetboard::launcher::QueryMonitorSnapshot(
                monitor,
                snapshot,
                snapshotError))
        {
            return FailDiagnostic(
                ExitCode::PanelLaunchSmokeTestFailed,
                L"panel launch smoke context failed",
                snapshotError);
        }

        const winwidgetboard::launcher::LauncherPlacement placement =
            winwidgetboard::launcher::ResolveLauncherPlacement(
            snapshot,
            GetDpiForSystem(),
            winwidgetboard::launcher::LauncherEntryPreferences{
                .placement = winwidgetboard::launcher::
                    LauncherEntryPlacementPreference::Floating,
            },
            0);
        if (placement.mode ==
            winwidgetboard::launcher::LauncherPlacementMode::Unavailable)
        {
            return FailDiagnostic(
                ExitCode::PanelLaunchSmokeTestFailed,
                L"panel launch smoke placement failed",
                placement.reason);
        }

        winwidgetboard::launcher::ChildProcessJob smokeJob;
        std::wstring smokeJobError;
        if (!smokeJob.Create(smokeJobError))
        {
            OutputDebugStringW(
                (L"WinWidgetBoard.LauncherHost: " + smokeJobError + L"\n").c_str());
        }

        winwidgetboard::launcher::WorkspacePanelProcess panelProcess;
        panelProcess.SetChildProcessJob(&smokeJob);
        std::wstring panelError;
        const bool panelSmokePassed = commandLineOptions.panelLifecycleSmokeTest
            ? panelProcess.RunLifecycleSmokeTest(
                snapshot,
                placement.hitRect,
                placement.dpi,
                panelError)
            : panelProcess.RunSmokeTest(
                snapshot,
                placement.hitRect,
                placement.dpi,
                panelError);
        if (!panelSmokePassed)
        {
            return FailDiagnostic(
                ExitCode::PanelLaunchSmokeTestFailed,
                L"panel launch smoke failed",
                panelError);
        }

        return static_cast<int>(ExitCode::Success);
    }

    if (commandLineOptions.coreBrokerSmokeTest)
    {
        winwidgetboard::launcher::CoreBrokerClient client;
        std::wstring brokerError;
        if (!client.RunSmokeTest(brokerError))
        {
            return FailDiagnostic(
                ExitCode::CoreBrokerSmokeTestFailed,
                L"CoreBroker smoke failed",
                brokerError);
        }

        return static_cast<int>(ExitCode::Success);
    }

    const WNDCLASSEXW windowClass{
        .cbSize = sizeof(WNDCLASSEXW),
        .style = 0,
        .lpfnWndProc = MessageWindowProcedure,
        .cbClsExtra = 0,
        .cbWndExtra = 0,
        .hInstance = instance,
        .hIcon = nullptr,
        .hCursor = nullptr,
        .hbrBackground = nullptr,
        .lpszMenuName = nullptr,
        .lpszClassName = kWindowClassName,
        .hIconSm = nullptr,
    };

    const ATOM windowClassAtom = RegisterClassExW(&windowClass);
    if (windowClassAtom == 0)
    {
        const DWORD error = GetLastError();
        return FailWin32(ExitCode::WindowClassRegistrationFailed, L"RegisterClassExW", error);
    }

    const HWND messageWindow = CreateWindowExW(
        0,
        MAKEINTATOM(windowClassAtom),
        L"WinWidgetBoard LauncherHost",
        0,
        0,
        0,
        0,
        0,
        HWND_MESSAGE,
        nullptr,
        instance,
        nullptr);

    if (messageWindow == nullptr)
    {
        const DWORD error = GetLastError();
        UnregisterClassW(kWindowClassName, instance);
        return FailWin32(ExitCode::WindowCreationFailed, L"CreateWindowExW(HWND_MESSAGE)", error);
    }

    if (commandLineOptions.smokeTest)
    {
        if (!DestroyWindow(messageWindow))
        {
            const DWORD error = GetLastError();
            UnregisterClassW(kWindowClassName, instance);
            return FailWin32(ExitCode::WindowDestructionFailed, L"DestroyWindow", error);
        }

        UnregisterClassW(kWindowClassName, instance);
        return static_cast<int>(ExitCode::Success);
    }

    const UINT taskbarCreatedMessage =
        RegisterWindowMessageW(L"TaskbarCreated");
    if (taskbarCreatedMessage == 0)
    {
        const DWORD error = GetLastError();
        DestroyWindow(messageWindow);
        UnregisterClassW(kWindowClassName, instance);
        return FailWin32(
            ExitCode::LauncherWindowCreationFailed,
            L"RegisterWindowMessageW(TaskbarCreated)",
            error);
    }

    // Every child the launcher starts lives inside this job, so closing the launcher -
    // including a force kill, which runs none of our cleanup - takes them down with it.
    winwidgetboard::launcher::ChildProcessJob childProcessJob;
    std::wstring childProcessJobError;
    if (!childProcessJob.Create(childProcessJobError))
    {
        OutputDebugStringW(
            (L"WinWidgetBoard.LauncherHost: " + childProcessJobError + L"\n").c_str());
    }

    // Bring the broker up ourselves so a plain shortcut to this executable is the whole
    // product. Sessions provisioned from outside - the development scripts - are left
    // alone, and --no-broker opts out entirely so a test can drive the entry without ever
    // touching the production database.
    winwidgetboard::launcher::CoreBrokerProcess coreBrokerProcess;
    std::wstring coreBrokerDiagnostic =
        L"CoreBroker auto-start was disabled by --no-broker";
    if (!commandLineOptions.noBroker)
    {
        coreBrokerProcess.EnsureStarted(childProcessJob, coreBrokerDiagnostic);
    }
    OutputDebugStringW(
        (L"WinWidgetBoard.LauncherHost: " + coreBrokerDiagnostic + L"\n").c_str());

    winwidgetboard::launcher::LauncherWindow launcherWindow;
    std::wstring launcherError;
    if (!launcherWindow.Create(
            instance,
            MonitorFromWindow(nullptr, MONITOR_DEFAULTTOPRIMARY),
            taskbarCreatedMessage,
            &childProcessJob,
            launcherError))
    {
        DestroyWindow(messageWindow);
        UnregisterClassW(kWindowClassName, instance);
        return FailDiagnostic(
            ExitCode::LauncherWindowCreationFailed,
            L"LauncherWindow::Create failed",
            launcherError);
    }

    MSG message{};
    BOOL messageResult = 0;
    while ((messageResult = GetMessageW(&message, nullptr, 0, 0)) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    if (messageResult == -1)
    {
        const DWORD error = GetLastError();
        launcherWindow.Destroy();
        DestroyWindow(messageWindow);
        UnregisterClassW(kWindowClassName, instance);
        return FailWin32(ExitCode::MessageLoopFailed, L"GetMessageW", error);
    }

    launcherWindow.Destroy();
    DestroyWindow(messageWindow);
    UnregisterClassW(kWindowClassName, instance);
    return static_cast<int>(ExitCode::Success);
}
