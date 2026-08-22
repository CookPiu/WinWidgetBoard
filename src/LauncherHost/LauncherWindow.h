#pragma once

#include "ChildProcessJob.h"
#include "CoreBrokerClient.h"
#include "EntryVisual.h"
#include "LauncherPreferences.h"
#include "TaskbarGeometry.h"
#include "WorkspacePanelProcess.h"

#include <windows.h>

#include <string>
#include <vector>

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
        const ChildProcessJob* childProcessJob,
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
    void Render();
    void ApplyPreferences(const LauncherEntryPreferences& preferences);
    bool RefreshContent();
    struct EntryContent
    {
        std::wstring text;
        EntryIcon icon{EntryIcon::None};
    };
    [[nodiscard]] EntryContent ComposeContent() const;
    // The hardware readings, for the second capsule. Independent of ComposeContent: the two
    // capsules are separate, so weather and readings can both be on at once.
    [[nodiscard]] std::vector<EntrySegment> ComposeMonitorSegments() const;
    [[nodiscard]] int MeasureContentWidthLogical() const;
    [[nodiscard]] int MeasureMonitorWidthLogical() const;
    void ApplyContentMode();
    [[nodiscard]] bool IsEmbedded() const noexcept;
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
    void SetHovered(bool hovered);
    void ScheduleReposition();
    void RefreshTheme();
    void SyncVisualTarget();
    void AdvanceAnimation();
    void StopAnimation();

    HINSTANCE _instance{};
    HWND _window{};
    HMONITOR _monitor{};
    UINT _taskbarCreatedMessage{};
    UINT _dpi{96};
    bool _classRegistered{};
    bool _repositionPosted{};
    bool _pressed{};
    bool _pressInside{};
    bool _hovered{};
    bool _panelOpen{};
    bool _hiddenForFullscreen{};
    bool _animating{};
    bool _reducedMotion{};
    LONGLONG _animationTick{};
    LONGLONG _performanceFrequency{};
    RECT _localHitRect{};
    RECT _localMonitorRect{};
    bool _hasLocalMonitorRect{};
    EntryContent _content;
    std::vector<EntrySegment> _monitorSegments;
    int _contentWidthLogical{};
    int _monitorWidthLogical{};
    EntryTheme _theme{};
    EntryVisualAnimator _animator;
    LauncherEntryPreferences _preferences{};
    LauncherPlacement _placement{};
    CoreBrokerClient _coreBroker;
    WorkspacePanelProcess _panelProcess;
};
}
