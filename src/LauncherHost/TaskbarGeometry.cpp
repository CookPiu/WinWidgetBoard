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
constexpr int kEntryMinWidthLogical = 96;
constexpr int kEntryMaxWidthLogical = 280;
constexpr int kEntryWidthQuantumLogical = 4;
constexpr int kEntryMinHeightLogical = 32;
constexpr int kEntryMaxHeightLogical = 40;
constexpr int kStripMarginLogical = 4;

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

// Reads the documented user setting that moves the Windows 11 icon cluster to the left. The
// value is absent until the user changes it, and absent means centred. This only reads a
// per-user preference; it does not touch Explorer's windows or visual tree.
bool QuerySystemIconsLeftAligned()
{
    DWORD value = 1;
    DWORD size = sizeof(value);
    DWORD type = REG_DWORD;
    const LSTATUS status = RegGetValueW(
        HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced",
        L"TaskbarAl",
        RRF_RT_REG_DWORD,
        &type,
        &value,
        &size);
    if (status != ERROR_SUCCESS)
    {
        return false;
    }

    return value == 0;
}

// Embedded entries deliberately leave the work area, so the work-area containment check no
// longer applies to them. The strip containment check below replaces that safety net.
bool IsEmbeddedMode(const LauncherPlacementMode mode)
{
    return mode == LauncherPlacementMode::ExactWidgetReplacement;
}

// A horizontal information strip only fits a top or bottom taskbar. A vertical taskbar is
// roughly as wide as the old square button, so embedding there would clip the content.
bool SupportsEmbeddedEntry(const TaskbarEdge edge)
{
    return edge == TaskbarEdge::Bottom || edge == TaskbarEdge::Top;
}

LauncherPlacementMode ResolveAlignmentMode(
    const LauncherEntryPlacementPreference placement)
{
    return placement == LauncherEntryPlacementPreference::Floating
        ? LauncherPlacementMode::SafeTaskbarSlot
        : LauncherPlacementMode::ExactWidgetReplacement;
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

    if (IsEmbeddedMode(placement.mode))
    {
        if (!IsRectWithin(snapshot.monitorRect, placement.windowRect))
        {
            failure = L"embedded window rectangle escapes the monitor";
            return false;
        }

        if (!snapshot.hasTaskbarStrip ||
            !IsRectWithin(snapshot.taskbarStrip, placement.windowRect))
        {
            failure = L"embedded window rectangle escapes the taskbar strip";
            return false;
        }
    }
    else if (!IsRectWithin(snapshot.workArea, placement.windowRect))
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

bool DeriveTaskbarStrip(
    const RECT& monitorRect,
    const RECT& workArea,
    const TaskbarEdge edge,
    RECT& strip)
{
    strip = RECT{};
    if (!IsValidRect(monitorRect) || !IsRectWithin(monitorRect, workArea))
    {
        return false;
    }

    RECT candidate{};
    switch (edge)
    {
    case TaskbarEdge::Bottom:
        candidate = RECT{
            monitorRect.left, workArea.bottom, monitorRect.right, monitorRect.bottom};
        break;
    case TaskbarEdge::Top:
        candidate = RECT{
            monitorRect.left, monitorRect.top, monitorRect.right, workArea.top};
        break;
    case TaskbarEdge::Left:
        candidate = RECT{
            monitorRect.left, monitorRect.top, workArea.left, monitorRect.bottom};
        break;
    case TaskbarEdge::Right:
        candidate = RECT{
            workArea.right, monitorRect.top, monitorRect.right, monitorRect.bottom};
        break;
    case TaskbarEdge::Unknown:
    default:
        return false;
    }

    if (!IsValidRect(candidate))
    {
        return false;
    }

    const bool horizontal =
        edge == TaskbarEdge::Bottom || edge == TaskbarEdge::Top;
    const int thickness = horizontal
        ? RectHeight(candidate)
        : RectWidth(candidate);
    if (thickness < kMinimumTaskbarThicknessPx ||
        thickness > kMaximumTaskbarThicknessPx)
    {
        return false;
    }

    strip = candidate;
    return true;
}

int ResolveEntryWidthLogical(const int measuredContentWidthLogical)
{
    int width = measuredContentWidthLogical <= 0
        ? kEntryMinWidthLogical
        : measuredContentWidthLogical;
    width = std::clamp(width, kEntryMinWidthLogical, kEntryMaxWidthLogical);

    const int remainder = width % kEntryWidthQuantumLogical;
    if (remainder != 0)
    {
        width += kEntryWidthQuantumLogical - remainder;
    }

    return std::min(width, kEntryMaxWidthLogical);
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

    snapshot.hasTaskbarStrip = DeriveTaskbarStrip(
        snapshot.monitorRect,
        snapshot.workArea,
        snapshot.inferredTaskbarEdge,
        snapshot.taskbarStrip);
    snapshot.systemIconsLeftAligned = QuerySystemIconsLeftAligned();

    return true;
}

// Places the entry along the taskbar strip. Returns false when the strip cannot host it.
static bool TryMakeEmbeddedPlacement(
    const MonitorSnapshot& snapshot,
    const LauncherEntryPlacementPreference alignment,
    const int measuredContentWidthLogical,
    LauncherPlacement& placement)
{
    if (!snapshot.hasTaskbarStrip ||
        !SupportsEmbeddedEntry(snapshot.inferredTaskbarEdge))
    {
        return false;
    }

    const RECT strip = snapshot.taskbarStrip;
    const int margin = ScaleLogicalPixels(kStripMarginLogical, placement.dpi);
    const int gap = ScaleLogicalPixels(kEdgeGapLogical, placement.dpi);
    const int minHeight = ScaleLogicalPixels(kEntryMinHeightLogical, placement.dpi);
    const int maxHeight = ScaleLogicalPixels(kEntryMaxHeightLogical, placement.dpi);

    const int available = RectHeight(strip) - margin * 2;
    if (available < minHeight)
    {
        return false;
    }

    const int height = std::min(available, maxHeight);
    const int width = ScaleLogicalPixels(
        ResolveEntryWidthLogical(measuredContentWidthLogical),
        placement.dpi);
    if (RectWidth(strip) < width + gap * 2)
    {
        return false;
    }

    int left = strip.left + gap;
    switch (alignment)
    {
    case LauncherEntryPlacementPreference::EmbeddedCenter:
        left = strip.left + (RectWidth(strip) - width) / 2;
        break;
    case LauncherEntryPlacementPreference::EmbeddedRight:
        left = strip.right - gap - width;
        break;
    case LauncherEntryPlacementPreference::EmbeddedLeft:
    case LauncherEntryPlacementPreference::Floating:
    default:
        break;
    }

    const int top = strip.top + (RectHeight(strip) - height) / 2;
    placement.mode = LauncherPlacementMode::ExactWidgetReplacement;
    placement.windowRect = RECT{left, top, left + width, top + height};
    // The whole strip is the button; there is no inert padding to pass through.
    placement.hitRect = placement.windowRect;
    return true;
}

LauncherPlacement ResolveLauncherPlacement(
    const MonitorSnapshot& snapshot,
    const UINT dpi,
    const LauncherEntryPreferences& preferences,
    const int measuredContentWidthLogical)
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

    if (ResolveAlignmentMode(preferences.placement) ==
        LauncherPlacementMode::ExactWidgetReplacement &&
        !snapshot.taskbarAutoHide)
    {
        LauncherEntryPlacementPreference alignment = preferences.placement;
        if (alignment == LauncherEntryPlacementPreference::EmbeddedLeft &&
            snapshot.systemIconsLeftAligned)
        {
            switch (preferences.leftAlignFallback)
            {
            case LauncherLeftAlignFallback::EmbeddedCenter:
                alignment = LauncherEntryPlacementPreference::EmbeddedCenter;
                break;
            case LauncherLeftAlignFallback::EmbeddedRight:
                alignment = LauncherEntryPlacementPreference::EmbeddedRight;
                break;
            case LauncherLeftAlignFallback::Floating:
            default:
                alignment = LauncherEntryPlacementPreference::Floating;
                break;
            }
        }

        if (alignment != LauncherEntryPlacementPreference::Floating &&
            TryMakeEmbeddedPlacement(
                snapshot,
                alignment,
                measuredContentWidthLogical,
                placement))
        {
            std::wstring failure;
            if (CheckPlacement(snapshot, placement, failure))
            {
                placement.reason =
                    L"entry embedded in the taskbar strip derived from the work area";
                return placement;
            }

            placement = LauncherPlacement{};
            placement.dpi = dpi == 0 ? kDefaultDpi : dpi;
            placement.edge = snapshot.inferredTaskbarEdge;
        }
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

const wchar_t* ToString(const LauncherEntryPlacementPreference placement)
{
    switch (placement)
    {
    case LauncherEntryPlacementPreference::EmbeddedLeft:
        return L"embedded-left";
    case LauncherEntryPlacementPreference::EmbeddedCenter:
        return L"embedded-center";
    case LauncherEntryPlacementPreference::EmbeddedRight:
        return L"embedded-right";
    case LauncherEntryPlacementPreference::Floating:
    default:
        return L"floating";
    }
}

const wchar_t* ToString(const LauncherContentMode content)
{
    switch (content)
    {
    case LauncherContentMode::Weather:
        return L"weather";
    case LauncherContentMode::DateTime:
    default:
        return L"date-time";
    }
}

// Exercises the embedded-entry invariants with synthetic layouts. Like the rest of this
// smoke test it never inspects the live desktop.
static bool RunEmbeddedEntryContractSmokeTest(
    const MonitorSnapshot& bottomTaskbar,
    std::wstring& failure)
{
    constexpr LauncherEntryPreferences embeddedLeft{
        .placement = LauncherEntryPlacementPreference::EmbeddedLeft,
    };

    const LauncherPlacement left =
        ResolveLauncherPlacement(bottomTaskbar, 96, embeddedLeft, 160);
    if (left.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        !CheckPlacement(bottomTaskbar, left, failure))
    {
        if (failure.empty())
        {
            failure = L"bottom taskbar did not embed the entry";
        }
        return false;
    }

    if (RectWidth(left.windowRect) != 160 ||
        RectHeight(left.windowRect) != kEntryMaxHeightLogical ||
        left.windowRect.left != bottomTaskbar.taskbarStrip.left + kEdgeGapLogical)
    {
        failure = L"embedded left entry has the wrong size or offset";
        return false;
    }

    if (ResolveEntryWidthLogical(0) != kEntryMinWidthLogical ||
        ResolveEntryWidthLogical(10) != kEntryMinWidthLogical ||
        ResolveEntryWidthLogical(4000) != kEntryMaxWidthLogical ||
        ResolveEntryWidthLogical(101) != 104 ||
        ResolveEntryWidthLogical(160) != 160)
    {
        failure = L"entry width was not clamped and quantised to the 4 DIP rhythm";
        return false;
    }

    constexpr LauncherEntryPreferences embeddedCenter{
        .placement = LauncherEntryPlacementPreference::EmbeddedCenter,
    };
    constexpr LauncherEntryPreferences embeddedRight{
        .placement = LauncherEntryPlacementPreference::EmbeddedRight,
    };
    const LauncherPlacement centered =
        ResolveLauncherPlacement(bottomTaskbar, 96, embeddedCenter, 160);
    const LauncherPlacement right =
        ResolveLauncherPlacement(bottomTaskbar, 96, embeddedRight, 160);
    if (centered.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        right.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        !(left.windowRect.left < centered.windowRect.left) ||
        !(centered.windowRect.left < right.windowRect.left) ||
        right.windowRect.right !=
            bottomTaskbar.taskbarStrip.right - kEdgeGapLogical ||
        !CheckPlacement(bottomTaskbar, centered, failure) ||
        !CheckPlacement(bottomTaskbar, right, failure))
    {
        if (failure.empty())
        {
            failure = L"embedded alignments are not ordered left, center, right";
        }
        return false;
    }

    // A 192 DPI desktop also has a physically taller strip, so the snapshot has to scale
    // with the DPI or the entry would be compared against a strip it could never fit.
    const MonitorSnapshot highDpi{
        .monitor = reinterpret_cast<HMONITOR>(4),
        .monitorRect = RECT{0, 0, 3840, 2160},
        .workArea = RECT{0, 0, 3840, 2064},
        .taskbarStrip = RECT{0, 2064, 3840, 2160},
        .hasTaskbarStrip = true,
        .inferredTaskbarEdge = TaskbarEdge::Bottom,
    };
    const LauncherPlacement scaled =
        ResolveLauncherPlacement(highDpi, 192, embeddedLeft, 160);
    if (scaled.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        RectWidth(scaled.windowRect) != RectWidth(left.windowRect) * 2 ||
        RectHeight(scaled.windowRect) != RectHeight(left.windowRect) * 2 ||
        !CheckPlacement(highDpi, scaled, failure))
    {
        if (failure.empty())
        {
            failure = L"embedded entry did not scale with DPI";
        }
        return false;
    }

    // A left-aligned icon cluster puts Start at the far left, so EmbeddedLeft must move.
    MonitorSnapshot leftAligned = bottomTaskbar;
    leftAligned.systemIconsLeftAligned = true;
    const LauncherPlacement floatingFallback =
        ResolveLauncherPlacement(leftAligned, 96, embeddedLeft, 160);
    if (floatingFallback.mode != LauncherPlacementMode::SafeTaskbarSlot)
    {
        failure = L"left-aligned taskbar did not fall back out of the embedded slot";
        return false;
    }

    constexpr LauncherEntryPreferences rightFallback{
        .placement = LauncherEntryPlacementPreference::EmbeddedLeft,
        .leftAlignFallback = LauncherLeftAlignFallback::EmbeddedRight,
    };
    const LauncherPlacement movedRight =
        ResolveLauncherPlacement(leftAligned, 96, rightFallback, 160);
    if (movedRight.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        movedRight.windowRect.left != right.windowRect.left)
    {
        failure = L"left-align fallback did not honour the configured alignment";
        return false;
    }

    // Auto-hide, vertical taskbars and strips too thin for a 32 DIP target must degrade.
    MonitorSnapshot autoHidden = bottomTaskbar;
    autoHidden.taskbarAutoHide = true;
    MonitorSnapshot vertical{
        .monitor = reinterpret_cast<HMONITOR>(2),
        .monitorRect = RECT{0, 0, 1920, 1080},
        .workArea = RECT{72, 0, 1920, 1080},
        .taskbarStrip = RECT{0, 0, 72, 1080},
        .hasTaskbarStrip = true,
        .inferredTaskbarEdge = TaskbarEdge::Left,
    };
    MonitorSnapshot thinStrip{
        .monitor = reinterpret_cast<HMONITOR>(3),
        .monitorRect = RECT{0, 0, 1920, 1080},
        .workArea = RECT{0, 0, 1920, 1060},
        .taskbarStrip = RECT{0, 1060, 1920, 1080},
        .hasTaskbarStrip = true,
        .inferredTaskbarEdge = TaskbarEdge::Bottom,
    };
    for (const MonitorSnapshot& degraded : {autoHidden, vertical, thinStrip})
    {
        const LauncherPlacement placement =
            ResolveLauncherPlacement(degraded, 96, embeddedLeft, 160);
        if (placement.mode == LauncherPlacementMode::ExactWidgetReplacement)
        {
            failure = L"a taskbar that cannot host the entry still embedded it";
            return false;
        }
    }

    RECT derived{};
    if (!DeriveTaskbarStrip(
            bottomTaskbar.monitorRect,
            bottomTaskbar.workArea,
            TaskbarEdge::Bottom,
            derived) ||
        derived.top != bottomTaskbar.workArea.bottom ||
        derived.bottom != bottomTaskbar.monitorRect.bottom ||
        DeriveTaskbarStrip(
            bottomTaskbar.monitorRect,
            bottomTaskbar.monitorRect,
            TaskbarEdge::Bottom,
            derived))
    {
        failure = L"taskbar strip derivation is not deterministic";
        return false;
    }

    failure.clear();
    return true;
}

bool RunGeometryContractSmokeTest(std::wstring& failure)
{
    failure.clear();

    constexpr LauncherEntryPreferences floatingPreferences{
        .placement = LauncherEntryPlacementPreference::Floating,
    };
    constexpr int kNoMeasuredContent = 0;

    MonitorSnapshot bottomTaskbar{
        .monitor = reinterpret_cast<HMONITOR>(1),
        .monitorRect = RECT{0, 0, 1920, 1080},
        .workArea = RECT{0, 0, 1920, 1032},
        .taskbarRect = RECT{0, 1032, 1920, 1080},
        .hasTaskbarRect = true,
        .taskbarAutoHide = false,
        .taskbarStrip = RECT{0, 1032, 1920, 1080},
        .hasTaskbarStrip = true,
        .systemIconsLeftAligned = false,
        .inferredTaskbarEdge = TaskbarEdge::Bottom,
    };

    if (!RunEmbeddedEntryContractSmokeTest(bottomTaskbar, failure))
    {
        return false;
    }

    const LauncherPlacement normalPlacement = ResolveLauncherPlacement(
        bottomTaskbar, 96, floatingPreferences, kNoMeasuredContent);
    if (normalPlacement.mode != LauncherPlacementMode::SafeTaskbarSlot ||
        !CheckPlacement(bottomTaskbar, normalPlacement, failure))
    {
        if (failure.empty())
        {
            failure = L"bottom taskbar did not resolve to a safe slot";
        }
        return false;
    }

    const LauncherPlacement highDpiPlacement = ResolveLauncherPlacement(
        bottomTaskbar, 192, floatingPreferences, kNoMeasuredContent);
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
    const LauncherPlacement fallbackPlacement = ResolveLauncherPlacement(
        autoHidden, 96, floatingPreferences, kNoMeasuredContent);
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
    const LauncherPlacement ambiguousPlacement = ResolveLauncherPlacement(
        ambiguous, 120, floatingPreferences, kNoMeasuredContent);
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
    const LauncherPlacement invalidPlacement = ResolveLauncherPlacement(
        invalid, 96, floatingPreferences, kNoMeasuredContent);
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
