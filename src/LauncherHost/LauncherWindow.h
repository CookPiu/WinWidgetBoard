#pragma once

#include "CoreBrokerClient.h"
#include "TaskbarGeometry.h"
#include "WorkspacePanelProcess.h"

#include <windows.h>

#include <string>

namespace winwidgetboard::launcher
{
class LauncherWindow final
{
public:
    LauncherWindow() = default;
    LauncherWindow(const LauncherWindow&) = delete;
    LauncherWindow& operator=(const LauncherWindow&) = delete;

    ~LauncherWindow();

    bool Create(
        HINSTANCE instance,
        HMONITOR initialMonitor,
        UINT taskbarCreatedMessage,
        std::wstring& error);
    void Destroy();

    [[nodiscard]] HWND GetHandle() const noexcept
    {
        return _window;
    }

private:
    static LRESULT CALLBACK WindowProcedure(
        HWND window,
        UINT message,
        WPARAM wParam,
        LPARAM lParam);

    bool InitializePlacement(std::wstring& error);
    void Reposition();
    void ApplyPlacement();
    void Paint(HDC deviceContext);
    void ShowContextMenu(POINT screenPoint);
    void HandleMenuCommand(UINT command);
    void TogglePanelRequested();
    void LogPlacement(const MonitorSnapshot& snapshot) const;
    void Log(const std::wstring& message) const;
    void EnsureTopmost();
    void UpdateFullscreenVisibility();
    void PollPanelProcess();
    [[nodiscard]] bool IsFullscreenForeground() const;
    [[nodiscard]] bool IsPointInHitRect(POINT clientPoint) const noexcept;
    void SetPressed(bool pressed, bool pointerInside);
    void ScheduleReposition();

    HINSTANCE _instance{};
    HWND _window{};
    HMONITOR _monitor{};
    UINT _taskbarCreatedMessage{};
    UINT _dpi{96};
    bool _classRegistered{};
    bool _repositionPosted{};
    bool _pressed{};
    bool _pressInside{};
    bool _panelOpen{};
    bool _hiddenForFullscreen{};
    RECT _localHitRect{};
    LauncherPlacement _placement{};
    CoreBrokerClient _coreBroker;
    WorkspacePanelProcess _panelProcess;
};
}
