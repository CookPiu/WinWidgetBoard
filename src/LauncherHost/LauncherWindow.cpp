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
constexpr UINT_PTR kVisibilityTimerId = 1;
constexpr UINT kVisibilityPollMilliseconds = 500;
constexpr UINT_PTR kContentTimerId = 2;
constexpr UINT kContentPollMilliseconds = 15000;
constexpr int kContentFontLogical = 14;
constexpr int kContentPaddingLogical = 12;
// Only re-place the window when the measured width moves out of this band, so a changing
// clock cannot make the entry twitch every time a glyph gets narrower.
constexpr int kContentWidthHysteresisLogical = 8;
// The layered colour key must be a colour the content never produces. Pure black is not:
// antialiased dark text lands on it and the glyph cores get punched out as transparent.
constexpr COLORREF kTransparentKey = RGB(255, 0, 255);

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

bool IsRectPointInside(const RECT& rectangle, const POINT point)
{
    return point.x >= rectangle.left && point.x < rectangle.right &&
        point.y >= rectangle.top && point.y < rectangle.bottom;
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
}

LauncherWindow::~LauncherWindow()
{
    Destroy();
}

bool LauncherWindow::Create(
    const HINSTANCE instance,
    const HMONITOR initialMonitor,
    const UINT taskbarCreatedMessage,
    std::wstring& error)
{
    error.clear();
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
    _content = ComposeContentText();
    _contentWidthLogical = MeasureContentWidthLogical(_content);

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

    if (SetLayeredWindowAttributes(_window, kTransparentKey, 0, LWA_COLORKEY) == FALSE)
    {
        error = L"SetLayeredWindowAttributes failed";
        Destroy();
        return false;
    }

    _dpi = GetDpiForWindow(_window);
    if (_dpi == 0)
    {
        _dpi = 96;
    }
    _contentWidthLogical = MeasureContentWidthLogical(_content);
    if (!InitializePlacement(error))
    {
        Destroy();
        return false;
    }

    ApplyPlacement();
    SetTimer(_window, kVisibilityTimerId, kVisibilityPollMilliseconds, nullptr);
    SetTimer(_window, kContentTimerId, kContentPollMilliseconds, nullptr);
    _coreBroker.Poll();
    return true;
}

void LauncherWindow::Destroy()
{
    // The panel is resident, so it outlives its own close; take it down with the launcher.
    _panelProcess.Shutdown();
    _coreBroker.Close();
    if (_window != nullptr)
    {
        KillTimer(_window, kVisibilityTimerId);
        KillTimer(_window, kContentTimerId);
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
        _contentWidthLogical);
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

    const int hitWidth = _localHitRect.right - _localHitRect.left;
    const int hitHeight = _localHitRect.bottom - _localHitRect.top;
    const int cornerRadius = std::max(8, std::min(hitWidth, hitHeight) / 2);
    HRGN region = CreateRoundRectRgn(
        _localHitRect.left,
        _localHitRect.top,
        _localHitRect.right + 1,
        _localHitRect.bottom + 1,
        cornerRadius,
        cornerRadius);
    if (region != nullptr)
    {
        // SetWindowRgn takes ownership of the region on success.
        if (SetWindowRgn(_window, region, TRUE) == 0)
        {
            DeleteObject(region);
        }
    }

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
    InvalidateRect(_window, nullptr, FALSE);
}

bool LauncherWindow::IsEmbedded() const noexcept
{
    return _placement.mode == LauncherPlacementMode::ExactWidgetReplacement;
}

HFONT LauncherWindow::CreateContentFont() const
{
    return CreateFontW(
        -ScaleLogical(kContentFontLogical, _dpi),
        0,
        0,
        0,
        FW_SEMIBOLD,
        FALSE,
        FALSE,
        FALSE,
        DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,
        CLIP_DEFAULT_PRECIS,
        ANTIALIASED_QUALITY,
        DEFAULT_PITCH | FF_SWISS,
        L"Segoe UI");
}

std::wstring LauncherWindow::ComposeContentText() const
{
    if (_preferences.content == LauncherContentMode::Weather)
    {
        // Until the first successful refresh reaches the broker there is nothing to show;
        // fall back to the clock rather than inventing a reading or blanking the entry.
        std::wstring weather = _coreBroker.GetWeatherSummary();
        if (!weather.empty())
        {
            return weather;
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
        return L"WinWidgetBoard";
    }

    return std::wstring{time} + L"   " + std::wstring{date};
}

int LauncherWindow::MeasureContentWidthLogical(const std::wstring& text) const
{
    const HDC screen = GetDC(nullptr);
    if (screen == nullptr)
    {
        return 0;
    }

    HFONT font = CreateContentFont();
    HGDIOBJ previousFont = SelectObject(screen, font);
    SIZE extent{};
    const bool measured = GetTextExtentPoint32W(
        screen,
        text.c_str(),
        static_cast<int>(text.size()),
        &extent) != FALSE;
    SelectObject(screen, previousFont);
    DeleteObject(font);
    ReleaseDC(nullptr, screen);

    if (!measured)
    {
        return 0;
    }

    return ToLogical(extent.cx, _dpi) + kContentPaddingLogical * 2;
}

bool LauncherWindow::RefreshContent()
{
    std::wstring text = ComposeContentText();
    const bool textChanged = text != _content;
    _content = std::move(text);

    const int measured = MeasureContentWidthLogical(_content);
    const bool widthChanged =
        std::abs(measured - _contentWidthLogical) >= kContentWidthHysteresisLogical;
    if (widthChanged)
    {
        _contentWidthLogical = measured;
    }

    if (textChanged && _window != nullptr)
    {
        InvalidateRect(_window, nullptr, FALSE);
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

    RefreshContent();
    Reposition();
}

void LauncherWindow::Paint(const HDC deviceContext)
{
    RECT clientRect{};
    GetClientRect(_window, &clientRect);

    HBRUSH transparentBrush = CreateSolidBrush(kTransparentKey);
    FillRect(deviceContext, &clientRect, transparentBrush);
    DeleteObject(transparentBrush);

    const COLORREF backgroundColor = GetSysColor(
        _pressed || _panelOpen ? COLOR_HIGHLIGHT : COLOR_BTNFACE);
    const COLORREF foregroundColor = GetSysColor(
        _pressed || _panelOpen ? COLOR_HIGHLIGHTTEXT : COLOR_BTNTEXT);
    const COLORREF borderColor = GetSysColor(COLOR_WINDOWTEXT);

    HBRUSH buttonBrush = CreateSolidBrush(backgroundColor);
    HPEN buttonPen = CreatePen(PS_SOLID, 1, borderColor);
    HGDIOBJ previousBrush = SelectObject(deviceContext, buttonBrush);
    HGDIOBJ previousPen = SelectObject(deviceContext, buttonPen);

    const int radius = std::max(
        8,
        std::min(
            static_cast<int>(_localHitRect.right - _localHitRect.left),
            static_cast<int>(_localHitRect.bottom - _localHitRect.top)) / 2);
    RoundRect(
        deviceContext,
        _localHitRect.left,
        _localHitRect.top,
        _localHitRect.right,
        _localHitRect.bottom,
        radius,
        radius);

    SelectObject(deviceContext, previousPen);
    SelectObject(deviceContext, previousBrush);
    DeleteObject(buttonPen);
    DeleteObject(buttonBrush);

    SetBkMode(deviceContext, TRANSPARENT);
    SetTextColor(deviceContext, foregroundColor);

    if (!IsEmbedded())
    {
        HFONT glyphFont = CreateFontW(
            -std::max(
                14,
                static_cast<int>(_localHitRect.bottom - _localHitRect.top) / 2),
            0,
            0,
            0,
            FW_SEMIBOLD,
            FALSE,
            FALSE,
            FALSE,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY,
            DEFAULT_PITCH | FF_SWISS,
            L"Segoe UI");
        HGDIOBJ previousGlyphFont = SelectObject(deviceContext, glyphFont);
        RECT glyphRect = _localHitRect;
        DrawTextW(
            deviceContext,
            L"W",
            1,
            &glyphRect,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(deviceContext, previousGlyphFont);
        DeleteObject(glyphFont);
        return;
    }

    HFONT font = CreateContentFont();
    HGDIOBJ previousFont = SelectObject(deviceContext, font);
    const int padding = ScaleLogical(kContentPaddingLogical, _dpi);
    RECT textRect = _localHitRect;
    textRect.left += padding;
    textRect.right -= padding;
    DrawTextW(
        deviceContext,
        _content.c_str(),
        static_cast<int>(_content.size()),
        &textRect,
        DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
    SelectObject(deviceContext, previousFont);
    DeleteObject(font);
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
    InvalidateRect(_window, nullptr, FALSE);
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
        InvalidateRect(_window, nullptr, FALSE);
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
    return IsRectPointInside(_localHitRect, clientPoint);
}

void LauncherWindow::SetPressed(const bool pressed, const bool pointerInside)
{
    _pressed = pressed;
    _pressInside = pointerInside;
    InvalidateRect(_window, nullptr, FALSE);
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
        PAINTSTRUCT paintStruct{};
        const HDC deviceContext = BeginPaint(window, &paintStruct);
        self->Paint(deviceContext);
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
        if (GetCapture() == window && self->_pressed)
        {
            const POINT clientPoint{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            self->SetPressed(true, self->IsPointInHitRect(clientPoint));
        }
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
    case WM_SETTINGCHANGE:
    case WM_DPICHANGED:
        self->ScheduleReposition();
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
            // Only a width change past the hysteresis band is worth re-placing the window;
            // a new string on its own just repaints.
            if (self->RefreshContent())
            {
                self->Reposition();
            }
        }
        return 0;

    case WM_CLOSE:
        DestroyWindow(window);
        return 0;

    case WM_DESTROY:
        KillTimer(window, kVisibilityTimerId);
        PostQuitMessage(0);
        return 0;

    default:
        if (message == self->_taskbarCreatedMessage &&
            self->_taskbarCreatedMessage != 0)
        {
            self->ScheduleReposition();
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
