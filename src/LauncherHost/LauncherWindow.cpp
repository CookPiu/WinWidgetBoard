#include "LauncherWindow.h"

#include <windowsx.h>

#include <algorithm>
#include <string>
#include <string_view>

namespace winwidgetboard::launcher
{
namespace
{
constexpr wchar_t kWindowClassName[] = L"WinWidgetBoard.LauncherHost.EntryWindow";
constexpr UINT kRepositionMessage = WM_APP + 1;
// Posted from the foreground WinEvent hook. The hook runs on this thread's message queue, but
// it must not do work inline: a hook callback that blocks delays every window activation on
// the desktop.
constexpr UINT kEnsureTopmostMessage = WM_APP + 2;
constexpr UINT_PTR kVisibilityTimerId = 1;
constexpr UINT kVisibilityPollMilliseconds = 500;
// Explorer re-stacks Shell_TrayWnd while it handles a window activation, and it can do so
// either side of our own re-assert. Reacting to the activation alone therefore still loses
// the race about a third of the time, so the reaction is followed by a few short re-asserts
// that cover the window in which Explorer might still raise the taskbar. Measured on the
// reference machine: the entry stayed covered ~357 ms with the visibility poll alone, and
// 62-156 ms with a single 120 ms follow-up.
constexpr UINT_PTR kTopmostTimerId = 4;
constexpr UINT kTopmostSettleMilliseconds = 40;
constexpr int kTopmostSettleTicks = 3;
constexpr UINT_PTR kContentTimerId = 2;
constexpr UINT kContentPollMilliseconds = 15000;
// Hardware readings go stale in seconds, so that mode repaints on the provider's own cadence
// instead of the clock's.
constexpr UINT kMonitorContentPollMilliseconds = 2000;
// Runs only while a state change is in flight, and is killed the moment it settles, so a
// resting entry costs no timer wake-ups at all.
constexpr UINT_PTR kAnimationTimerId = 3;
constexpr UINT kAnimationIntervalMilliseconds = 16;


enum ContextMenuCommand : UINT
{
    MenuTogglePanel = 1001,
    MenuEditLayout = 1002,
    MenuPauseRefresh = 1003,
    MenuSettings = 1004,
    MenuDiagnostics = 1005,
    MenuExit = 1006,
    MenuContentDateTime = 1010,
    MenuContentWeather = 1011,
    // The hardware readings are their own capsule, so this is a switch rather than a third
    // content choice: weather and readings can be on at the same time.
    MenuToggleSystemMonitor = 1012,
    MenuPlacementEmbeddedLeft = 1020,
    MenuPlacementEmbeddedCenter = 1021,
    MenuPlacementEmbeddedRight = 1022,
    MenuPlacementFloating = 1023,
    MenuFallbackFloating = 1030,
    MenuFallbackCenter = 1031,
    MenuFallbackRight = 1032,
};

int ScaleLogical(const int logicalPixels, const UINT dpi)
{
    const UINT effectiveDpi = dpi == 0 ? 96 : dpi;
    const int scaled = MulDiv(logicalPixels, static_cast<int>(effectiveDpi), 96);
    return scaled <= 0 ? 1 : scaled;
}

int ToLogical(const int physicalPixels, const UINT dpi)
{
    const UINT effectiveDpi = dpi == 0 ? 96 : dpi;
    const int logical = MulDiv(physicalPixels, 96, static_cast<int>(effectiveDpi));
    return logical <= 0 ? 1 : logical;
}

std::wstring RectToString(const RECT& rectangle)
{
    return L"[" + std::to_wstring(rectangle.left) + L"," +
        std::to_wstring(rectangle.top) + L" - " +
        std::to_wstring(rectangle.right) + L"," +
        std::to_wstring(rectangle.bottom) + L"]";
}

void AppendRadioItem(
    const HMENU menu,
    const UINT command,
    const wchar_t* text,
    const bool checked)
{
    AppendMenuW(menu, MF_STRING | (checked ? MF_CHECKED : MF_UNCHECKED), command, text);
}

bool IsWindowCoveringMonitor(const HWND window, const RECT& monitorRect)
{
    RECT windowRect{};
    if (GetWindowRect(window, &windowRect) == FALSE)
    {
        return false;
    }

    return windowRect.left <= monitorRect.left &&
        windowRect.top <= monitorRect.top &&
        windowRect.right >= monitorRect.right &&
        windowRect.bottom >= monitorRect.bottom;
}

bool IsDesktopShellWindow(const HWND window)
{
    if (window == GetDesktopWindow() || window == GetShellWindow())
    {
        return true;
    }

    wchar_t className[128]{};
    const int length = GetClassNameW(window, className, _countof(className));
    if (length == 0)
    {
        return false;
    }

    const std::wstring_view name(className, static_cast<size_t>(length));
    return name == L"Progman" ||
        name == L"WorkerW" ||
        name == L"SHELLDLL_DefView";
}

// The one entry window in this process, for the WinEvent hook. The hook signature carries no
// user data, and the alternative - a map keyed by thread - would be machinery for a case that
// cannot happen: LauncherHost is single-instance and creates exactly one entry.
HWND g_entryWindowForHook = nullptr;

void CALLBACK ForegroundEventProc(
    HWINEVENTHOOK,
    const DWORD event,
    const HWND,
    const LONG objectId,
    const LONG,
    const DWORD,
    const DWORD)
{
    if (event != EVENT_SYSTEM_FOREGROUND || objectId != OBJID_WINDOW)
    {
        return;
    }

    if (g_entryWindowForHook != nullptr)
    {
        PostMessageW(g_entryWindowForHook, kEnsureTopmostMessage, 0, 0);
    }
}
}

LauncherWindow::~LauncherWindow()
{
    Destroy();
}

bool LauncherWindow::Create(
    const HINSTANCE instance,
    const HMONITOR initialMonitor,
    const UINT taskbarCreatedMessage,
    const ChildProcessJob* const childProcessJob,
    std::wstring& error)
{
    error.clear();
    _panelProcess.SetChildProcessJob(childProcessJob);
    _instance = instance;
    _monitor = initialMonitor != nullptr
        ? initialMonitor
        : MonitorFromWindow(nullptr, MONITOR_DEFAULTTOPRIMARY);
    _taskbarCreatedMessage = taskbarCreatedMessage;

    const WNDCLASSEXW windowClass{
        .cbSize = sizeof(WNDCLASSEXW),
        .style = CS_HREDRAW | CS_VREDRAW,
        .lpfnWndProc = &LauncherWindow::WindowProcedure,
        .cbClsExtra = 0,
        .cbWndExtra = 0,
        .hInstance = instance,
        .hIcon = nullptr,
        .hCursor = LoadCursorW(nullptr, IDC_HAND),
        .hbrBackground = nullptr,
        .lpszMenuName = nullptr,
        .lpszClassName = kWindowClassName,
        .hIconSm = nullptr,
    };

    const ATOM classAtom = RegisterClassExW(&windowClass);
    if (classAtom == 0)
    {
        if (GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            error = L"RegisterClassExW failed";
            return false;
        }
    }
    _classRegistered = true;
    _preferences = LoadLauncherPreferences();
    _theme = QueryEntryTheme();
    _reducedMotion = IsReducedMotionPreferred();
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    _performanceFrequency = frequency.QuadPart;
    _content = ComposeContent();
    _contentWidthLogical = MeasureContentWidthLogical();

    if (!InitializePlacement(error))
    {
        Destroy();
        return false;
    }

    _window = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
        kWindowClassName,
        L"WinWidgetBoard Launcher",
        WS_POPUP,
        _placement.windowRect.left,
        _placement.windowRect.top,
        _placement.windowRect.right - _placement.windowRect.left,
        _placement.windowRect.bottom - _placement.windowRect.top,
        nullptr,
        nullptr,
        instance,
        this);

    if (_window == nullptr)
    {
        error = L"CreateWindowExW for launcher entry failed";
        Destroy();
        return false;
    }

    _dpi = GetDpiForWindow(_window);
    if (_dpi == 0)
    {
        _dpi = 96;
    }
    _contentWidthLogical = 0;
    TryRefitEntryWidth(MeasureContentWidthLogical(), _contentWidthLogical);
    if (!InitializePlacement(error))
    {
        Destroy();
        return false;
    }

    SyncVisualTarget();
    _animator.SnapToTarget();
    ApplyPlacement();
    SetTimer(_window, kVisibilityTimerId, kVisibilityPollMilliseconds, nullptr);

    // The taskbar and this entry share the topmost band, and Explorer raises Shell_TrayWnd
    // whenever it handles a window activation. Waiting for the next visibility poll left the
    // capsule covered for up to half a second every time - the blink this hook removes.
    // Out-of-context so the callback is delivered to this message queue rather than injected
    // into every process on the desktop.
    g_entryWindowForHook = _window;
    _foregroundHook = SetWinEventHook(
        EVENT_SYSTEM_FOREGROUND,
        EVENT_SYSTEM_FOREGROUND,
        nullptr,
        ForegroundEventProc,
        0,
        0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    if (_foregroundHook == nullptr)
    {
        // Not fatal: the visibility poll still recovers the z-order, just less promptly.
        Log(L"SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed; "
            L"topmost recovery falls back to the visibility poll");
    }

    ApplyContentMode();
    _coreBroker.Poll();
    return true;
}

void LauncherWindow::Destroy()
{
    // The panel is resident, so it outlives its own close; take it down with the launcher.
    _panelProcess.Shutdown();
    _coreBroker.Close();
    if (_foregroundHook != nullptr)
    {
        UnhookWinEvent(_foregroundHook);
        _foregroundHook = nullptr;
    }
    g_entryWindowForHook = nullptr;
    if (_window != nullptr)
    {
        KillTimer(_window, kVisibilityTimerId);
        KillTimer(_window, kContentTimerId);
        KillTimer(_window, kAnimationTimerId);
        KillTimer(_window, kTopmostTimerId);
        DestroyWindow(_window);
        _window = nullptr;
    }

    if (_classRegistered && _instance != nullptr)
    {
        UnregisterClassW(kWindowClassName, _instance);
        _classRegistered = false;
    }
}

bool LauncherWindow::InitializePlacement(std::wstring& error)
{
    MonitorSnapshot snapshot{};
    if (!QueryMonitorSnapshot(_monitor, snapshot, error))
    {
        return false;
    }

    _placement = ResolveLauncherPlacement(
        snapshot,
        _dpi,
        _preferences,
        _contentWidthLogical,
        _monitorWidthLogical);
    if (_placement.mode == LauncherPlacementMode::Unavailable)
    {
        error = L"launcher placement unavailable: " + _placement.reason;
        return false;
    }

    LogPlacement(snapshot);
    return true;
}

void LauncherWindow::Reposition()
{
    _repositionPosted = false;

    if (_window == nullptr)
    {
        return;
    }

    HMONITOR monitor = MonitorFromWindow(_window, MONITOR_DEFAULTTONULL);
    if (monitor == nullptr)
    {
        monitor = MonitorFromWindow(nullptr, MONITOR_DEFAULTTOPRIMARY);
    }
    if (monitor == nullptr)
    {
        _hiddenForFullscreen = false;
        ShowWindow(_window, SW_HIDE);
        Log(L"no monitor is available; launcher entry is hidden");
        return;
    }

    _monitor = monitor;
    std::wstring error;
    if (!InitializePlacement(error))
    {
        _hiddenForFullscreen = false;
        ShowWindow(_window, SW_HIDE);
        Log(error);
        return;
    }

    ApplyPlacement();
    UpdateFullscreenVisibility();
}

void LauncherWindow::ApplyPlacement()
{
    if (_window == nullptr)
    {
        return;
    }

    _localHitRect = RECT{
        _placement.hitRect.left - _placement.windowRect.left,
        _placement.hitRect.top - _placement.windowRect.top,
        _placement.hitRect.right - _placement.windowRect.left,
        _placement.hitRect.bottom - _placement.windowRect.top,
    };
    _hasLocalMonitorRect = _placement.hasMonitorRect;
    _localMonitorRect = _hasLocalMonitorRect
        ? RECT{
            _placement.monitorRect.left - _placement.windowRect.left,
            _placement.monitorRect.top - _placement.windowRect.top,
            _placement.monitorRect.right - _placement.windowRect.left,
            _placement.monitorRect.bottom - _placement.windowRect.top,
        }
        : RECT{};

    // No window region any more: the capsule is shaped by per-pixel alpha, and a region
    // would clip exactly the antialiased edge that alpha buys us.
    if (SetWindowPos(
        _window,
        HWND_TOPMOST,
        _placement.windowRect.left,
        _placement.windowRect.top,
        _placement.windowRect.right - _placement.windowRect.left,
        _placement.windowRect.bottom - _placement.windowRect.top,
        SWP_NOACTIVATE | SWP_SHOWWINDOW) == FALSE)
    {
        Log(L"SetWindowPos(HWND_TOPMOST) failed: " +
            std::to_wstring(GetLastError()));
    }

    Render();
}

bool LauncherWindow::IsEmbedded() const noexcept
{
    return _placement.mode == LauncherPlacementMode::ExactWidgetReplacement;
}

// The entry has two content shapes and therefore two measurements. Keeping the choice in one
// place stops a caller from measuring a segmented strip as if it were a single label.
int LauncherWindow::MeasureContentWidthLogical() const
{
    return MeasureEntryContentWidthLogical(
        _content.text,
        _dpi,
        false,
        _content.icon);
}

int LauncherWindow::MeasureMonitorWidthLogical() const
{
    return _monitorSegments.empty()
        ? 0
        : MeasureEntrySegmentsWidthLogical(_monitorSegments, _dpi);
}

std::vector<EntrySegment> LauncherWindow::ComposeMonitorSegments() const
{
    std::vector<EntrySegment> segments;
    if (!_preferences.showSystemMonitor)
    {
        return segments;
    }

    // Nothing sampled yet leaves this empty, and an empty list leaves the capsule undrawn
    // rather than showing a shell with placeholders that look like readings.
    const std::vector<CoreBrokerClient::MonitorSegment> readings =
        _coreBroker.GetSystemMonitorSegments();
    segments.reserve(readings.size());
    for (const CoreBrokerClient::MonitorSegment& reading : readings)
    {
        segments.push_back(
            EntrySegment{
                ParseEntryIcon(reading.iconId),
                reading.text,
                reading.history});
    }

    return segments;
}

LauncherWindow::EntryContent LauncherWindow::ComposeContent() const
{
    if (_preferences.content == LauncherContentMode::Weather)
    {
        // Until the first successful refresh reaches the broker there is nothing to show;
        // fall back to the clock rather than inventing a reading or blanking the entry.
        const CoreBrokerClient::WeatherSummary weather = _coreBroker.GetWeatherSummary();
        if (weather.HasReading())
        {
            // No location: the user picked it, so repeating it every minute spends the
            // entry's width on something they already know. The glyph carries the
            // condition instead, which also keeps the entry free of any wording.
            return EntryContent{
                weather.temperature + L"\u00B0",
                ParseEntryIcon(weather.conditionIconId),
            };
        }
    }

    wchar_t time[64]{};
    wchar_t date[64]{};
    const int timeLength = GetTimeFormatEx(
        LOCALE_NAME_USER_DEFAULT,
        TIME_NOSECONDS,
        nullptr,
        nullptr,
        time,
        static_cast<int>(std::size(time)));
    const int dateLength = GetDateFormatEx(
        LOCALE_NAME_USER_DEFAULT,
        DATE_SHORTDATE,
        nullptr,
        nullptr,
        date,
        static_cast<int>(std::size(date)),
        nullptr);
    if (timeLength <= 0 || dateLength <= 0)
    {
        return EntryContent{L"WinWidgetBoard", EntryIcon::None};
    }

    return EntryContent{
        std::wstring{time} + L"   " + std::wstring{date},
        EntryIcon::None,
    };
}

bool LauncherWindow::RefreshContent()
{
    EntryContent content = ComposeContent();
    std::vector<EntrySegment> segments = ComposeMonitorSegments();

    bool textChanged =
        content.text != _content.text || content.icon != _content.icon;
    if (!textChanged && segments.size() != _monitorSegments.size())
    {
        textChanged = true;
    }
    else if (!textChanged)
    {
        for (size_t index = 0; index < segments.size(); ++index)
        {
            const EntrySegment& next = segments[index];
            const EntrySegment& previous = _monitorSegments[index];
            // The history moves on every tick even when the rounded number does not, and the
            // graph is drawn from it, so it counts as a change worth repainting for.
            if (next.text != previous.text ||
                next.icon != previous.icon ||
                next.history != previous.history)
            {
                textChanged = true;
                break;
            }
        }
    }

    _content = std::move(content);
    _monitorSegments = std::move(segments);

    // Both fields hold the width currently reserved, not the last measurement; TryRefit*
    // updates them in place and reports whether the window has to move. A capsule appearing or
    // disappearing needs no special case - it is a refit from or to zero.
    const bool mainRefit =
        TryRefitEntryWidth(MeasureContentWidthLogical(), _contentWidthLogical);
    const bool monitorRefit =
        TryRefitMonitorWidth(MeasureMonitorWidthLogical(), _monitorWidthLogical);
    const bool widthChanged = mainRefit || monitorRefit;

    if (textChanged && !widthChanged)
    {
        // A new string on its own is a repaint; only a width change past the hysteresis
        // band is allowed to move the window.
        Render();
    }

    return widthChanged;
}

void LauncherWindow::ApplyPreferences(const LauncherEntryPreferences& preferences)
{
    _preferences = preferences;
    if (!SaveLauncherPreferences(_preferences))
    {
        Log(L"failed to persist launcher preferences");
    }

    ApplyContentMode();
    RefreshContent();
    Reposition();
}

// Asking the broker for a hardware summary is also what keeps it sampling, so the request and
// the repaint cadence are turned on together and off together.
void LauncherWindow::ApplyContentMode()
{
    const bool monitor = _preferences.showSystemMonitor;
    _coreBroker.SetSystemMonitorEnabled(monitor);
    if (_window != nullptr)
    {
        SetTimer(
            _window,
            kContentTimerId,
            monitor ? kMonitorContentPollMilliseconds : kContentPollMilliseconds,
            nullptr);
    }
}

void LauncherWindow::Render()
{
    if (_window == nullptr)
    {
        return;
    }

    EntryRenderRequest request{};
    request.windowSize = SIZE{
        _placement.windowRect.right - _placement.windowRect.left,
        _placement.windowRect.bottom - _placement.windowRect.top,
    };
    request.capsule = _localHitRect;
    request.dpi = _dpi;
    // The floating badge stays a circular mark; only the embedded strip carries content.
    request.compact = !IsEmbedded();
    request.text = request.compact ? std::wstring(L"W") : _content.text;
    request.icon = request.compact ? EntryIcon::None : _content.icon;
    if (!request.compact && _hasLocalMonitorRect)
    {
        request.monitorCapsule = _localMonitorRect;
        request.hasMonitorCapsule = true;
        request.segments = _monitorSegments;
    }

    std::wstring error;
    if (!RenderEntry(_window, request, _theme, _animator.Value(), error))
    {
        Log(L"entry render failed: " + error);
    }
}

void LauncherWindow::RefreshTheme()
{
    _theme = QueryEntryTheme();
    _reducedMotion = IsReducedMotionPreferred();
    if (_reducedMotion)
    {
        _animator.SnapToTarget();
        StopAnimation();
    }

    Render();
}

void LauncherWindow::SyncVisualTarget()
{
    const EntryVisualState target{
        _hovered ? 1.0 : 0.0,
        _pressed && _pressInside ? 1.0 : 0.0,
        _panelOpen ? 1.0 : 0.0,
    };
    _animator.SetTarget(target);

    if (_window == nullptr || _animator.IsSettled())
    {
        return;
    }

    if (!_animating)
    {
        _animating = true;
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        _animationTick = now.QuadPart;
        SetTimer(
            _window,
            kAnimationTimerId,
            kAnimationIntervalMilliseconds,
            nullptr);
    }
}

void LauncherWindow::AdvanceAnimation()
{
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    const double seconds = _performanceFrequency > 0
        ? static_cast<double>(now.QuadPart - _animationTick) /
            static_cast<double>(_performanceFrequency)
        : static_cast<double>(kAnimationIntervalMilliseconds) / 1000.0;
    _animationTick = now.QuadPart;

    const bool moving = _animator.Advance(seconds, _reducedMotion);
    Render();
    if (!moving)
    {
        StopAnimation();
    }
}

void LauncherWindow::StopAnimation()
{
    if (_window != nullptr && _animating)
    {
        KillTimer(_window, kAnimationTimerId);
    }

    _animating = false;
}

void LauncherWindow::ShowContextMenu(const POINT screenPoint)
{
    HMENU menu = CreatePopupMenu();
    if (menu == nullptr)
    {
        Log(L"CreatePopupMenu failed");
        return;
    }

    AppendMenuW(menu, MF_STRING, MenuTogglePanel, L"打开/关闭面板");
    AppendMenuW(menu, MF_STRING, MenuEditLayout, L"编辑布局");
    AppendMenuW(menu, MF_STRING, MenuPauseRefresh, L"暂停后台刷新");
    AppendMenuW(menu, MF_STRING, MenuSettings, L"打开设置");
    AppendMenuW(menu, MF_STRING, MenuDiagnostics, L"诊断");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

    HMENU contentMenu = CreatePopupMenu();
    if (contentMenu != nullptr)
    {
        AppendRadioItem(
            contentMenu,
            MenuContentDateTime,
            L"日期与时间",
            _preferences.content == LauncherContentMode::DateTime);
        AppendRadioItem(
            contentMenu,
            MenuContentWeather,
            L"天气",
            _preferences.content == LauncherContentMode::Weather);
        AppendMenuW(
            menu,
            MF_STRING | MF_POPUP,
            reinterpret_cast<UINT_PTR>(contentMenu),
            L"显示内容");
    }

    AppendMenuW(
        menu,
        MF_STRING | (_preferences.showSystemMonitor ? MF_CHECKED : MF_UNCHECKED),
        MenuToggleSystemMonitor,
        L"显示硬件监控");

    HMENU placementMenu = CreatePopupMenu();
    if (placementMenu != nullptr)
    {
        AppendRadioItem(
            placementMenu,
            MenuPlacementEmbeddedLeft,
            L"嵌入任务栏 · 靠左",
            _preferences.placement ==
                LauncherEntryPlacementPreference::EmbeddedLeft);
        AppendRadioItem(
            placementMenu,
            MenuPlacementEmbeddedCenter,
            L"嵌入任务栏 · 居中",
            _preferences.placement ==
                LauncherEntryPlacementPreference::EmbeddedCenter);
        AppendRadioItem(
            placementMenu,
            MenuPlacementEmbeddedRight,
            L"嵌入任务栏 · 靠右",
            _preferences.placement ==
                LauncherEntryPlacementPreference::EmbeddedRight);
        AppendRadioItem(
            placementMenu,
            MenuPlacementFloating,
            L"悬浮在任务栏上方",
            _preferences.placement == LauncherEntryPlacementPreference::Floating);
        AppendMenuW(
            menu,
            MF_STRING | MF_POPUP,
            reinterpret_cast<UINT_PTR>(placementMenu),
            L"入口位置");
    }

    HMENU fallbackMenu = CreatePopupMenu();
    if (fallbackMenu != nullptr)
    {
        AppendRadioItem(
            fallbackMenu,
            MenuFallbackFloating,
            L"改为悬浮",
            _preferences.leftAlignFallback == LauncherLeftAlignFallback::Floating);
        AppendRadioItem(
            fallbackMenu,
            MenuFallbackCenter,
            L"改为居中",
            _preferences.leftAlignFallback ==
                LauncherLeftAlignFallback::EmbeddedCenter);
        AppendRadioItem(
            fallbackMenu,
            MenuFallbackRight,
            L"改为靠右",
            _preferences.leftAlignFallback ==
                LauncherLeftAlignFallback::EmbeddedRight);
        AppendMenuW(
            menu,
            MF_STRING | MF_POPUP,
            reinterpret_cast<UINT_PTR>(fallbackMenu),
            L"任务栏左对齐时");
    }

    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, MenuExit, L"退出");

    SetForegroundWindow(_window);
    const UINT command = TrackPopupMenuEx(
        menu,
        TPM_RETURNCMD | TPM_RIGHTBUTTON,
        screenPoint.x,
        screenPoint.y,
        _window,
        nullptr);
    DestroyMenu(menu);

    if (command != 0)
    {
        HandleMenuCommand(command);
    }

    PostMessageW(_window, WM_NULL, 0, 0);
}

void LauncherWindow::HandleMenuCommand(const UINT command)
{
    switch (command)
    {
    case MenuTogglePanel:
        TogglePanelRequested();
        break;
    case MenuEditLayout:
        Log(L"edit-layout requested; layout mode is deferred to M1.3");
        break;
    case MenuPauseRefresh:
        Log(L"pause-refresh requested; CoreBroker handoff is deferred to M2");
        break;
    case MenuSettings:
        Log(L"settings requested; settings mode is deferred to a later work package");
        break;
    case MenuDiagnostics:
        Log(L"diagnostics requested");
        break;
    case MenuContentDateTime:
    case MenuContentWeather:
    {
        LauncherEntryPreferences updated = _preferences;
        updated.content = command == MenuContentWeather
            ? LauncherContentMode::Weather
            : LauncherContentMode::DateTime;
        ApplyPreferences(updated);
        break;
    }
    case MenuToggleSystemMonitor:
    {
        LauncherEntryPreferences updated = _preferences;
        updated.showSystemMonitor = !updated.showSystemMonitor;
        ApplyPreferences(updated);
        break;
    }
    case MenuPlacementEmbeddedLeft:
    case MenuPlacementEmbeddedCenter:
    case MenuPlacementEmbeddedRight:
    case MenuPlacementFloating:
    {
        LauncherEntryPreferences updated = _preferences;
        updated.placement = command == MenuPlacementEmbeddedLeft
            ? LauncherEntryPlacementPreference::EmbeddedLeft
            : command == MenuPlacementEmbeddedCenter
                ? LauncherEntryPlacementPreference::EmbeddedCenter
                : command == MenuPlacementEmbeddedRight
                    ? LauncherEntryPlacementPreference::EmbeddedRight
                    : LauncherEntryPlacementPreference::Floating;
        ApplyPreferences(updated);
        break;
    }
    case MenuFallbackFloating:
    case MenuFallbackCenter:
    case MenuFallbackRight:
    {
        LauncherEntryPreferences updated = _preferences;
        updated.leftAlignFallback = command == MenuFallbackCenter
            ? LauncherLeftAlignFallback::EmbeddedCenter
            : command == MenuFallbackRight
                ? LauncherLeftAlignFallback::EmbeddedRight
                : LauncherLeftAlignFallback::Floating;
        ApplyPreferences(updated);
        break;
    }
    case MenuExit:
        DestroyWindow(_window);
        break;
    default:
        break;
    }
}

void LauncherWindow::TogglePanelRequested()
{
    MonitorSnapshot snapshot{};
    std::wstring contextError;
    if (!QueryMonitorSnapshot(_monitor, snapshot, contextError))
    {
        Log(L"panel launch context unavailable: " + contextError);
        return;
    }

    // Ask the panel what it is actually doing rather than trusting our own bookkeeping:
    // a resident panel hides itself on focus loss without telling us, and the old reset
    // path relied on the process exiting.
    const bool opening = !_panelProcess.IsPanelVisible();
    std::wstring processError;
    if (!_panelProcess.Toggle(
            opening,
            snapshot,
            _placement.hitRect,
            _dpi,
            processError))
    {
        Log(L"panel process handoff failed: " + processError);
        return;
    }

    _panelOpen = opening;
    // Start the indicator moving on the click rather than on the next visibility poll, so
    // it grows alongside the panel's own opening motion instead of half a second later.
    SyncVisualTarget();
    Log(_panelOpen
        ? L"panel process started or activated"
        : L"panel close requested");
}

void LauncherWindow::PollPanelProcess()
{
    _panelProcess.Poll();
    const bool panelVisible = _panelProcess.IsPanelVisible();
    if (_panelOpen != panelVisible)
    {
        _panelOpen = panelVisible;
        SyncVisualTarget();
    }
}

void LauncherWindow::LogPlacement(const MonitorSnapshot& snapshot) const
{
    Log(
        L"placement mode=" + std::wstring(ToString(_placement.mode)) +
        L", edge=" + ToString(_placement.edge) +
        L", dpi=" + std::to_wstring(_placement.dpi) +
        L", monitor=" + RectToString(snapshot.monitorRect) +
        L", workArea=" + RectToString(snapshot.workArea) +
        L", window=" + RectToString(_placement.windowRect) +
        L", hit=" + RectToString(_placement.hitRect) +
        L", reason=" + _placement.reason);
}

void LauncherWindow::Log(const std::wstring& message) const
{
    const std::wstring diagnostic = L"WinWidgetBoard.LauncherHost: " + message + L"\n";
    OutputDebugStringW(diagnostic.c_str());
}

void LauncherWindow::EnsureTopmost()
{
    if (_window == nullptr || IsWindowVisible(_window) == FALSE)
    {
        return;
    }

    if (SetWindowPos(
            _window,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE |
                SWP_NOSIZE |
                SWP_NOACTIVATE |
                SWP_NOOWNERZORDER) == FALSE)
    {
        Log(L"SetWindowPos(HWND_TOPMOST) reassertion failed: " +
            std::to_wstring(GetLastError()));
    }
}

void LauncherWindow::UpdateFullscreenVisibility()
{
    if (_window == nullptr)
    {
        return;
    }

    const bool shouldHide = IsFullscreenForeground();
    if (shouldHide == _hiddenForFullscreen)
    {
        return;
    }

    _hiddenForFullscreen = shouldHide;
    ShowWindow(_window, shouldHide ? SW_HIDE : SW_SHOWNOACTIVATE);
    Log(shouldHide
        ? L"full-screen foreground detected; launcher entry hidden"
        : L"full-screen foreground cleared; launcher entry shown");
}

bool LauncherWindow::IsFullscreenForeground() const
{
    const HWND foreground = GetForegroundWindow();
    if (foreground == nullptr || foreground == _window ||
        IsWindowVisible(foreground) == FALSE ||
        IsDesktopShellWindow(foreground))
    {
        return false;
    }

    const HMONITOR foregroundMonitor =
        MonitorFromWindow(foreground, MONITOR_DEFAULTTONULL);
    if (foregroundMonitor == nullptr || foregroundMonitor != _monitor)
    {
        return false;
    }

    MONITORINFO monitorInfo{};
    monitorInfo.cbSize = sizeof(monitorInfo);
    if (GetMonitorInfoW(foregroundMonitor, &monitorInfo) == FALSE)
    {
        return false;
    }

    return IsWindowCoveringMonitor(foreground, monitorInfo.rcMonitor);
}

bool LauncherWindow::IsPointInHitRect(const POINT clientPoint) const noexcept
{
    // The capsule itself, not its bounding box: without a window region the rounded
    // corners are transparent, and transparent pixels must not swallow clicks. The gap
    // between the two capsules is outside both, so it passes through like any other point
    // off the entry.
    return IsPointInCapsule(_localHitRect, clientPoint) ||
        (_hasLocalMonitorRect &&
            !_monitorSegments.empty() &&
            IsPointInCapsule(_localMonitorRect, clientPoint));
}

void LauncherWindow::SetPressed(const bool pressed, const bool pointerInside)
{
    _pressed = pressed;
    _pressInside = pointerInside;
    SyncVisualTarget();
}

void LauncherWindow::SetHovered(const bool hovered)
{
    if (_hovered == hovered)
    {
        return;
    }

    _hovered = hovered;
    SyncVisualTarget();
}

void LauncherWindow::ScheduleReposition()
{
    if (_window != nullptr && !_repositionPosted)
    {
        _repositionPosted = true;
        PostMessageW(_window, kRepositionMessage, 0, 0);
    }
}

LRESULT CALLBACK LauncherWindow::WindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wParam,
    const LPARAM lParam)
{
    auto* self = reinterpret_cast<LauncherWindow*>(
        GetWindowLongPtrW(window, GWLP_USERDATA));

    if (message == WM_NCCREATE)
    {
        const auto* createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
        self = static_cast<LauncherWindow*>(createStruct->lpCreateParams);
        SetWindowLongPtrW(
            window,
            GWLP_USERDATA,
            reinterpret_cast<LONG_PTR>(self));
    }

    if (self == nullptr)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }

    switch (message)
    {
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;

    case WM_NCHITTEST:
    {
        POINT screenPoint{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
        ScreenToClient(window, &screenPoint);
        return self->IsPointInHitRect(screenPoint) ? HTCLIENT : HTTRANSPARENT;
    }

    case WM_PAINT:
    {
        // The content is pushed with UpdateLayeredWindow, so there is nothing to paint
        // here; the region still has to be validated or Windows keeps asking.
        PAINTSTRUCT paintStruct{};
        BeginPaint(window, &paintStruct);
        EndPaint(window, &paintStruct);
        return 0;
    }

    case WM_ERASEBKGND:
        return 1;

    case WM_LBUTTONDOWN:
        self->SetPressed(true, true);
        SetCapture(window);
        return 0;

    case WM_MOUSEMOVE:
    {
        const POINT clientPoint{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
        const bool inside = self->IsPointInHitRect(clientPoint);
        self->SetHovered(inside);
        if (inside)
        {
            // WM_NCHITTEST makes everything outside the capsule transparent, so the only
            // way to learn the pointer left is to subscribe to WM_MOUSELEAVE.
            TRACKMOUSEEVENT track{};
            track.cbSize = sizeof(track);
            track.dwFlags = TME_LEAVE;
            track.hwndTrack = window;
            TrackMouseEvent(&track);
        }

        if (GetCapture() == window && self->_pressed)
        {
            self->SetPressed(true, inside);
        }
        return 0;
    }

    case WM_MOUSELEAVE:
        self->SetHovered(false);
        return 0;

    case WM_LBUTTONUP:
    {
        const POINT clientPoint{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
        const bool activate = self->_pressed &&
            self->_pressInside &&
            self->IsPointInHitRect(clientPoint);
        self->SetPressed(false, false);
        if (GetCapture() == window)
        {
            ReleaseCapture();
        }
        if (activate)
        {
            self->TogglePanelRequested();
        }
        return 0;
    }

    case WM_CAPTURECHANGED:
        if (self->_pressed)
        {
            self->SetPressed(false, false);
        }
        return 0;

    case WM_RBUTTONUP:
    case WM_CONTEXTMENU:
    {
        POINT screenPoint{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
        if (message == WM_RBUTTONUP)
        {
            ClientToScreen(window, &screenPoint);
        }
        if (message == WM_CONTEXTMENU && screenPoint.x == -1 && screenPoint.y == -1)
        {
            screenPoint = POINT{
                self->_placement.windowRect.left,
                self->_placement.windowRect.top,
            };
        }
        self->ShowContextMenu(screenPoint);
        return 0;
    }

    case WM_DISPLAYCHANGE:
    case WM_DPICHANGED:
        self->ScheduleReposition();
        return 0;

    case WM_SETTINGCHANGE:
        // Theme, high contrast and reduce-motion all arrive here.
        self->RefreshTheme();
        self->ScheduleReposition();
        return 0;

    case WM_THEMECHANGED:
    case WM_SYSCOLORCHANGE:
        self->RefreshTheme();
        return 0;

    case WM_POWERBROADCAST:
        if (wParam == PBT_APMRESUMEAUTOMATIC || wParam == PBT_APMRESUMESUSPEND)
        {
            self->ScheduleReposition();
        }
        return TRUE;

    case WM_TIMER:
        if (wParam == kVisibilityTimerId)
        {
            self->_coreBroker.Poll();
            self->PollPanelProcess();
            self->UpdateFullscreenVisibility();
            self->EnsureTopmost();
        }
        else if (wParam == kContentTimerId)
        {
            if (self->RefreshContent())
            {
                self->Reposition();
            }
        }
        else if (wParam == kAnimationTimerId)
        {
            self->AdvanceAnimation();
        }
        else if (wParam == kTopmostTimerId)
        {
            self->EnsureTopmost();
            if (--self->_topmostSettleTicksLeft <= 0)
            {
                // The timer exists only for the length of one activation; a resting entry
                // keeps the visibility poll as its only wake-up.
                KillTimer(window, kTopmostTimerId);
                self->_topmostSettleTicksLeft = 0;
            }
        }
        return 0;

    case WM_CLOSE:
        DestroyWindow(window);
        return 0;

    case WM_DESTROY:
        KillTimer(window, kVisibilityTimerId);
        KillTimer(window, kContentTimerId);
        KillTimer(window, kAnimationTimerId);
        KillTimer(window, kTopmostTimerId);
        PostQuitMessage(0);
        return 0;

    default:
        if (message == self->_taskbarCreatedMessage &&
            self->_taskbarCreatedMessage != 0)
        {
            self->ScheduleReposition();
            return 0;
        }
        if (message == kEnsureTopmostMessage)
        {
            self->EnsureTopmost();
            self->_topmostSettleTicksLeft = kTopmostSettleTicks;
            SetTimer(
                window,
                kTopmostTimerId,
                kTopmostSettleMilliseconds,
                nullptr);
            return 0;
        }

        if (message == kRepositionMessage)
        {
            self->Reposition();
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }
}
}
