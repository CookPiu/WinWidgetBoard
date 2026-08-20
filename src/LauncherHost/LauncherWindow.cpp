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

enum ContextMenuCommand : UINT
{
    MenuTogglePanel = 1001,
    MenuEditLayout = 1002,
    MenuPauseRefresh = 1003,
    MenuSettings = 1004,
    MenuDiagnostics = 1005,
    MenuExit = 1006,
};

std::wstring RectToString(const RECT& rectangle)
{
    return L"[" + std::to_wstring(rectangle.left) + L"," +
        std::to_wstring(rectangle.top) + L" - " +
        std::to_wstring(rectangle.right) + L"," +
        std::to_wstring(rectangle.bottom) + L"]";
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

    if (SetLayeredWindowAttributes(_window, RGB(0, 0, 0), 0, LWA_COLORKEY) == FALSE)
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
    if (!InitializePlacement(error))
    {
        Destroy();
        return false;
    }

    ApplyPlacement();
    SetTimer(_window, kVisibilityTimerId, kVisibilityPollMilliseconds, nullptr);
    _coreBroker.Poll();
    return true;
}

void LauncherWindow::Destroy()
{
    _coreBroker.Close();
    if (_window != nullptr)
    {
        KillTimer(_window, kVisibilityTimerId);
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

    // Wiring for the embedded strip lands with the rendering change; keep the historical
    // floating slot until then so this commit changes no visible behaviour.
    _placement = ResolveLauncherPlacement(
        snapshot,
        _dpi,
        LauncherEntryPreferences{
            .placement = LauncherEntryPlacementPreference::Floating,
        },
        0);
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
    const int cornerRadius = std::max(8, hitWidth / 4);
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

void LauncherWindow::Paint(const HDC deviceContext)
{
    RECT clientRect{};
    GetClientRect(_window, &clientRect);

    HBRUSH transparentBrush = CreateSolidBrush(RGB(0, 0, 0));
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
        static_cast<int>(_localHitRect.right - _localHitRect.left) / 4);
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
    HFONT font = CreateFontW(
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
    HGDIOBJ previousFont = SelectObject(deviceContext, font);
    RECT textRect = _localHitRect;
    DrawTextW(
        deviceContext,
        L"W",
        1,
        &textRect,
        DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
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

    const bool opening = !_panelOpen;
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
    const bool wasRunning = _panelProcess.IsRunning();
    _panelProcess.Poll();
    if (_panelOpen && wasRunning && !_panelProcess.IsRunning())
    {
        _panelOpen = false;
        InvalidateRect(_window, nullptr, FALSE);
        Log(L"WorkspacePanel process exited; launcher state reset");
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
