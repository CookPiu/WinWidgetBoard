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
    // Registers, re-registers or drops the global shortcut to match the preference. Failure
    // is a state the entry carries rather than an error it swallows: another application
    // holding the chord is ordinary, and the registry state it writes back is how the
    // panel's settings page says so.
    void ApplyHotkey();
    // The preferences now live behind the panel's settings page, which writes the same HKCU
    // key this entry reads. The watcher wakes on any change to that key and re-applies; its
    // own write-backs (the hotkey state) reload as an equal value and are skipped.
    void StartPreferencesWatch();
    void StopPreferencesWatch();
    void HandlePreferencesChanged();
    [[nodiscard]] bool IsEmbedded() const noexcept;
    void ShowContextMenu(POINT screenPoint);
    void HandleMenuCommand(UINT command);
    void TogglePanelRequested();
    void LogPlacement(const MonitorSnapshot& snapshot) const;
    void Log(const std::wstring& message) const;
    void EnsureTopmost();
    // Hangs the entry off the taskbar as an owned window, so Explorer raising the taskbar
    // can no longer cover it. Degrades to the topmost re-assertion when no taskbar window
    // resolves.
    void EnsureTaskbarOwner();
    [[nodiscard]] HWND ResolveTaskbarWindow() const;
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
    // Re-checks full-screen visibility and, only when visible, re-asserts the entry's
    // topmost position the moment Explorer re-stacks the taskbar.
    HWINEVENTHOOK _foregroundHook{};
    // Short re-asserts still owed after the current activation. Zero when none is in flight,
    // which is also when the settle timer is not running at all.
    int _topmostSettleTicksLeft{};
    // The taskbar the entry currently hangs from, or null when none resolved. Explorer
    // restarts replace that window, so this is re-resolved rather than cached for the
    // process lifetime.
    HWND _taskbarOwner{};
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
    // True only while a chord is actually registered with Windows.
    bool _hotkeyRegistered{};
    HKEY _preferencesWatchKey{};
    HANDLE _preferencesWatchStop{};
    HANDLE _preferencesWatchChange{};
    HANDLE _preferencesWatchThread{};
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
