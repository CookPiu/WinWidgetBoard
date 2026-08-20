#pragma once

#include "TaskbarGeometry.h"

#include <windows.h>

#include <string>

namespace winwidgetboard::launcher
{
class WorkspacePanelProcess final
{
public:
    WorkspacePanelProcess() = default;
    WorkspacePanelProcess(const WorkspacePanelProcess&) = delete;
    WorkspacePanelProcess& operator=(const WorkspacePanelProcess&) = delete;

    ~WorkspacePanelProcess();

    bool Toggle(
        bool open,
        const MonitorSnapshot& snapshot,
        const RECT& launcherRect,
        UINT dpi,
        std::wstring& error);

    bool RunSmokeTest(
        const MonitorSnapshot& snapshot,
        const RECT& launcherRect,
        UINT dpi,
        std::wstring& error);

    bool RunLifecycleSmokeTest(
        const MonitorSnapshot& snapshot,
        const RECT& launcherRect,
        UINT dpi,
        std::wstring& error);

    void Poll();
    void Shutdown();

    // True when the resident panel currently has a visible window. The panel can hide
    // itself on focus loss without telling the launcher, so this is the only trustworthy
    // source for the toggle state.
    [[nodiscard]] bool IsPanelVisible() const;

    [[nodiscard]] bool IsRunning() const noexcept
    {
        return _process != nullptr;
    }

private:
    bool Launch(
        const MonitorSnapshot& snapshot,
        const RECT& launcherRect,
        UINT dpi,
        bool smokeTest,
        std::wstring& error);
    bool ResolveExecutablePath(std::wstring& path, std::wstring& error) const;
    bool ActivatePanelWindow(std::wstring& error) const;
    bool RequestClose(std::wstring& error) const;
    [[nodiscard]] HWND FindPanelWindow() const;
    void Reap();

    static BOOL CALLBACK FindPanelWindowCallback(HWND window, LPARAM parameter);
    static std::wstring RectArgument(const wchar_t* name, const RECT& rectangle);

    HANDLE _process{};
    DWORD _processId{};
};
}
