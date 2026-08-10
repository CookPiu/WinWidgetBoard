#include "TaskbarGeometry.h"

#include <shellapi.h>

#include <algorithm>
#include <array>
#include <limits>
#include <utility>

namespace winwidgetboard::launcher
{
namespace
{
constexpr UINT kDefaultDpi = 96;
constexpr int kWindowLogicalSize = 52;
constexpr int kButtonLogicalSize = 36;
constexpr int kEdgeGapLogical = 8;
constexpr int kMinimumTaskbarThicknessPx = 8;
constexpr int kMaximumTaskbarThicknessPx = 240;

int ScaleLogicalPixels(const int logicalPixels, const UINT dpi)
{
    const UINT effectiveDpi = dpi == 0 ? kDefaultDpi : dpi;
    const long long scaled =
        (static_cast<long long>(logicalPixels) * static_cast<long long>(effectiveDpi)) /
        static_cast<long long>(kDefaultDpi);

    if (scaled <= 0)
    {
        return 1;
    }

    if (scaled > std::numeric_limits<int>::max())
    {
        return std::numeric_limits<int>::max();
    }

    return static_cast<int>(scaled);
}

int RectWidth(const RECT& rectangle)
{
    return rectangle.right - rectangle.left;
}

int RectHeight(const RECT& rectangle)
{
    return rectangle.bottom - rectangle.top;
}

bool IsPlausibleTaskbarThickness(const int thickness)
{
    return thickness >= kMinimumTaskbarThicknessPx &&
        thickness <= kMaximumTaskbarThicknessPx;
}

bool IsSingleEdgeDifference(
    const RECT& monitorRect,
    const RECT& workArea,
    const TaskbarEdge edge)
{
    const int top = workArea.top - monitorRect.top;
    const int bottom = monitorRect.bottom - workArea.bottom;
    const int left = workArea.left - monitorRect.left;
    const int right = monitorRect.right - workArea.right;

    const std::array<int, 4> differences{top, bottom, left, right};
    int activeEdges = 0;
    for (const int difference : differences)
    {
        if (difference >= kMinimumTaskbarThicknessPx)
        {
            ++activeEdges;
        }
    }

    if (activeEdges != 1)
    {
        return false;
    }

    switch (edge)
    {
    case TaskbarEdge::Top:
        return IsPlausibleTaskbarThickness(top);
    case TaskbarEdge::Bottom:
        return IsPlausibleTaskbarThickness(bottom);
    case TaskbarEdge::Left:
        return IsPlausibleTaskbarThickness(left);
    case TaskbarEdge::Right:
        return IsPlausibleTaskbarThickness(right);
    case TaskbarEdge::Unknown:
    default:
        return false;
    }
}

RECT MakeEdgePlacement(
    const RECT& workArea,
    const TaskbarEdge edge,
    const int windowSize,
    const int gap)
{
    RECT rectangle{
        workArea.right - gap - windowSize,
        workArea.bottom - gap - windowSize,
        workArea.right - gap,
        workArea.bottom - gap,
    };

    switch (edge)
    {
    case TaskbarEdge::Top:
        rectangle.left = workArea.left + gap;
        rectangle.top = workArea.top + gap;
        break;
    case TaskbarEdge::Bottom:
        rectangle.left = workArea.left + gap;
        rectangle.top = workArea.bottom - gap - windowSize;
        break;
    case TaskbarEdge::Left:
        rectangle.left = workArea.left + gap;
        rectangle.top = workArea.top + gap;
        break;
    case TaskbarEdge::Right:
        rectangle.left = workArea.right - gap - windowSize;
        rectangle.top = workArea.top + gap;
        break;
    case TaskbarEdge::Unknown:
    default:
        break;
    }

    rectangle.right = rectangle.left + windowSize;
    rectangle.bottom = rectangle.top + windowSize;
    return rectangle;
}

bool CheckPlacement(
    const MonitorSnapshot& snapshot,
    const LauncherPlacement& placement,
    std::wstring& failure)
{
    if (placement.mode == LauncherPlacementMode::Unavailable)
    {
        failure = L"placement unexpectedly unavailable";
        return false;
    }

    if (!IsRectWithin(snapshot.workArea, placement.windowRect))
    {
        failure = L"window rectangle escapes the work area";
        return false;
    }

    if (!IsRectWithin(placement.windowRect, placement.hitRect))
    {
        failure = L"hit rectangle escapes the window rectangle";
        return false;
    }

    if (!IsValidRect(placement.hitRect))
    {
        failure = L"hit rectangle is empty";
        return false;
    }

    return true;
}
}

bool IsValidRect(const RECT& rectangle)
{
    return rectangle.right > rectangle.left && rectangle.bottom > rectangle.top;
}

bool IsRectWithin(const RECT& outer, const RECT& inner)
{
    return IsValidRect(outer) && IsValidRect(inner) &&
        inner.left >= outer.left &&
        inner.top >= outer.top &&
        inner.right <= outer.right &&
        inner.bottom <= outer.bottom;
}

TaskbarEdge InferTaskbarEdge(const RECT& monitorRect, const RECT& workArea)
{
    if (!IsValidRect(monitorRect) || !IsValidRect(workArea) ||
        workArea.left < monitorRect.left ||
        workArea.top < monitorRect.top ||
        workArea.right > monitorRect.right ||
        workArea.bottom > monitorRect.bottom)
    {
        return TaskbarEdge::Unknown;
    }

    const int top = workArea.top - monitorRect.top;
    const int bottom = monitorRect.bottom - workArea.bottom;
    const int left = workArea.left - monitorRect.left;
    const int right = monitorRect.right - workArea.right;

    const std::array<std::pair<int, TaskbarEdge>, 4> differences{
        std::pair{top, TaskbarEdge::Top},
        std::pair{bottom, TaskbarEdge::Bottom},
        std::pair{left, TaskbarEdge::Left},
        std::pair{right, TaskbarEdge::Right},
    };

    int activeEdges = 0;
    TaskbarEdge activeEdge = TaskbarEdge::Unknown;
    for (const auto& [difference, edge] : differences)
    {
        if (difference >= kMinimumTaskbarThicknessPx)
        {
            ++activeEdges;
            activeEdge = edge;
        }
    }

    if (activeEdges != 1)
    {
        return TaskbarEdge::Unknown;
    }

    return IsPlausibleTaskbarThickness(
        activeEdge == TaskbarEdge::Top ? top :
        activeEdge == TaskbarEdge::Bottom ? bottom :
        activeEdge == TaskbarEdge::Left ? left : right)
        ? activeEdge
        : TaskbarEdge::Unknown;
}

bool QueryMonitorSnapshot(
    const HMONITOR monitor,
    MonitorSnapshot& snapshot,
    std::wstring& error)
{
    snapshot = {};
    error.clear();

    if (monitor == nullptr)
    {
        error = L"monitor handle is null";
        return false;
    }

    MONITORINFOEXW monitorInfo{};
    monitorInfo.cbSize = sizeof(monitorInfo);
    if (GetMonitorInfoW(monitor, &monitorInfo) == FALSE)
    {
        error = L"GetMonitorInfoW failed";
        return false;
    }

    if (!IsValidRect(monitorInfo.rcMonitor) ||
        !IsRectWithin(monitorInfo.rcMonitor, monitorInfo.rcWork))
    {
        error = L"monitor/work-area rectangles are invalid";
        return false;
    }

    snapshot.monitor = monitor;
    snapshot.monitorRect = monitorInfo.rcMonitor;
    snapshot.workArea = monitorInfo.rcWork;
    snapshot.inferredTaskbarEdge = InferTaskbarEdge(
        snapshot.monitorRect,
        snapshot.workArea);

    APPBARDATA stateData{};
    stateData.cbSize = sizeof(stateData);
    const UINT_PTR appBarState = SHAppBarMessage(ABM_GETSTATE, &stateData);
    snapshot.taskbarAutoHide = appBarState != 0 &&
        (appBarState & ABS_AUTOHIDE) != 0;

    APPBARDATA positionData{};
    positionData.cbSize = sizeof(positionData);
    if (SHAppBarMessage(ABM_GETTASKBARPOS, &positionData) != 0 &&
        IsValidRect(positionData.rc))
    {
        const RECT taskbarRect = positionData.rc;
        const bool intersectsMonitor =
            taskbarRect.left < snapshot.monitorRect.right &&
            taskbarRect.right > snapshot.monitorRect.left &&
            taskbarRect.top < snapshot.monitorRect.bottom &&
            taskbarRect.bottom > snapshot.monitorRect.top;

        if (intersectsMonitor)
        {
            snapshot.taskbarRect = taskbarRect;
            snapshot.hasTaskbarRect = true;
        }
    }

    return true;
}

LauncherPlacement ResolveLauncherPlacement(
    const MonitorSnapshot& snapshot,
    const UINT dpi)
{
    LauncherPlacement placement{};
    placement.dpi = dpi == 0 ? kDefaultDpi : dpi;
    placement.edge = snapshot.inferredTaskbarEdge;

    if (!IsValidRect(snapshot.monitorRect) ||
        !IsRectWithin(snapshot.monitorRect, snapshot.workArea))
    {
        placement.reason = L"invalid monitor or work-area geometry";
        return placement;
    }

    const int windowSize = ScaleLogicalPixels(kWindowLogicalSize, placement.dpi);
    const int buttonSize = ScaleLogicalPixels(kButtonLogicalSize, placement.dpi);
    const int gap = ScaleLogicalPixels(kEdgeGapLogical, placement.dpi);
    if (RectWidth(snapshot.workArea) < windowSize + gap * 2 ||
        RectHeight(snapshot.workArea) < windowSize + gap * 2)
    {
        placement.reason = L"work area is too small for the minimum entry";
        return placement;
    }

    const bool hasReliableTaskbarEdge =
        !snapshot.taskbarAutoHide &&
        IsSingleEdgeDifference(
            snapshot.monitorRect,
            snapshot.workArea,
            snapshot.inferredTaskbarEdge);

    placement.mode = hasReliableTaskbarEdge
        ? LauncherPlacementMode::SafeTaskbarSlot
        : LauncherPlacementMode::EdgeTabFallback;

    placement.windowRect = MakeEdgePlacement(
        snapshot.workArea,
        hasReliableTaskbarEdge ? snapshot.inferredTaskbarEdge : TaskbarEdge::Unknown,
        windowSize,
        gap);

    const int margin = std::max(0, (windowSize - buttonSize) / 2);
    placement.hitRect = RECT{
        placement.windowRect.left + margin,
        placement.windowRect.top + margin,
        placement.windowRect.left + margin + buttonSize,
        placement.windowRect.top + margin + buttonSize,
    };

    if (!IsRectWithin(snapshot.workArea, placement.windowRect) ||
        !IsRectWithin(placement.windowRect, placement.hitRect))
    {
        placement.mode = LauncherPlacementMode::Unavailable;
        placement.reason = L"computed entry rectangle is not within the work area";
        return placement;
    }

    placement.reason = hasReliableTaskbarEdge
        ? L"taskbar edge inferred from monitor/work-area difference; entry stays in the work area"
        : L"taskbar edge is ambiguous or auto-hidden; using work-area edge fallback";
    return placement;
}

const wchar_t* ToString(const TaskbarEdge edge)
{
    switch (edge)
    {
    case TaskbarEdge::Top:
        return L"top";
    case TaskbarEdge::Bottom:
        return L"bottom";
    case TaskbarEdge::Left:
        return L"left";
    case TaskbarEdge::Right:
        return L"right";
    case TaskbarEdge::Unknown:
    default:
        return L"unknown";
    }
}

const wchar_t* ToString(const LauncherPlacementMode mode)
{
    switch (mode)
    {
    case LauncherPlacementMode::ExactWidgetReplacement:
        return L"exact-widget-replacement";
    case LauncherPlacementMode::SafeTaskbarSlot:
        return L"safe-taskbar-slot";
    case LauncherPlacementMode::EdgeTabFallback:
        return L"edge-tab-fallback";
    case LauncherPlacementMode::Unavailable:
    default:
        return L"unavailable";
    }
}

bool RunGeometryContractSmokeTest(std::wstring& failure)
{
    failure.clear();

    MonitorSnapshot bottomTaskbar{
        .monitor = reinterpret_cast<HMONITOR>(1),
        .monitorRect = RECT{0, 0, 1920, 1080},
        .workArea = RECT{0, 0, 1920, 1032},
        .taskbarRect = RECT{0, 1032, 1920, 1080},
        .hasTaskbarRect = true,
        .taskbarAutoHide = false,
        .inferredTaskbarEdge = TaskbarEdge::Bottom,
    };

    const LauncherPlacement normalPlacement =
        ResolveLauncherPlacement(bottomTaskbar, 96);
    if (normalPlacement.mode != LauncherPlacementMode::SafeTaskbarSlot ||
        !CheckPlacement(bottomTaskbar, normalPlacement, failure))
    {
        if (failure.empty())
        {
            failure = L"bottom taskbar did not resolve to a safe slot";
        }
        return false;
    }

    const LauncherPlacement highDpiPlacement =
        ResolveLauncherPlacement(bottomTaskbar, 192);
    if (highDpiPlacement.dpi != 192 ||
        RectWidth(highDpiPlacement.hitRect) < RectWidth(normalPlacement.hitRect) * 2 ||
        RectHeight(highDpiPlacement.hitRect) < RectHeight(normalPlacement.hitRect) * 2 ||
        !CheckPlacement(bottomTaskbar, highDpiPlacement, failure))
    {
        if (failure.empty())
        {
            failure = L"high-DPI placement did not scale the hit target";
        }
        return false;
    }

    MonitorSnapshot autoHidden = bottomTaskbar;
    autoHidden.taskbarAutoHide = true;
    const LauncherPlacement fallbackPlacement =
        ResolveLauncherPlacement(autoHidden, 96);
    if (fallbackPlacement.mode != LauncherPlacementMode::EdgeTabFallback ||
        !CheckPlacement(autoHidden, fallbackPlacement, failure))
    {
        if (failure.empty())
        {
            failure = L"auto-hidden taskbar did not use the fallback";
        }
        return false;
    }

    MonitorSnapshot ambiguous = bottomTaskbar;
    ambiguous.workArea = RECT{0, 32, 1920, 1032};
    ambiguous.inferredTaskbarEdge = TaskbarEdge::Unknown;
    const LauncherPlacement ambiguousPlacement =
        ResolveLauncherPlacement(ambiguous, 120);
    if (ambiguousPlacement.mode != LauncherPlacementMode::EdgeTabFallback ||
        !CheckPlacement(ambiguous, ambiguousPlacement, failure))
    {
        if (failure.empty())
        {
            failure = L"ambiguous work-area geometry did not use the fallback";
        }
        return false;
    }

    MonitorSnapshot invalid = bottomTaskbar;
    invalid.monitorRect = RECT{0, 0, 0, 0};
    const LauncherPlacement invalidPlacement =
        ResolveLauncherPlacement(invalid, 96);
    if (invalidPlacement.mode != LauncherPlacementMode::Unavailable)
    {
        failure = L"invalid geometry was not rejected";
        return false;
    }

    if (InferTaskbarEdge(bottomTaskbar.monitorRect, bottomTaskbar.workArea) !=
        TaskbarEdge::Bottom ||
        InferTaskbarEdge(RECT{0, 0, 1920, 1080}, RECT{0, 0, 1920, 1080}) !=
        TaskbarEdge::Unknown)
    {
        failure = L"taskbar edge inference is not deterministic";
        return false;
    }

    return true;
}
}
