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
