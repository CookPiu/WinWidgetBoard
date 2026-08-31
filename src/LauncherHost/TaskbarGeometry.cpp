#include "TaskbarGeometry.h"

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
// The main capsule shows a clock, or a glyph and a temperature. It never needed more than
// this; the hardware readings moved to their own capsule with its own ceiling.
constexpr int kEntryMaxWidthLogical = 280;
constexpr int kEntryWidthQuantumLogical = 4;
// The hardware capsule. No minimum: it is simply absent when there is nothing to show, and
// holding a 96 DIP hole open for it would be worse than that. The ceiling covers the
// contract's eight readings folded into four columns at the detailed shape.
constexpr int kMonitorMaxWidthLogical = 440;
// Between the two capsules. Wider than the 4 DIP rhythm's usual step because it has to read
// as a break between two separate instruments, not as padding inside one.
constexpr int kCapsuleGapLogical = 8;
// Spare room reserved beyond the measured content on every refit, so a reading that grows a
// little does not need the window moved again immediately.
constexpr int kWidthHeadroomLogical = 8;
// How much unused room has to accumulate before it is worth reclaiming. Larger than the
// headroom, so reserving headroom cannot itself trigger the shrink that undoes it.
constexpr int kWidthReclaimLogical = 16;
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

    if (placement.hasMonitorRect)
    {
        if (!IsValidRect(placement.monitorRect) ||
            !IsRectWithin(placement.windowRect, placement.monitorRect))
        {
            failure = L"hardware capsule escapes the window rectangle";
            return false;
        }

        if (placement.monitorRect.left < placement.hitRect.right)
        {
            failure = L"hardware capsule overlaps the main capsule";
            return false;
        }
    }
    else if (IsValidRect(placement.monitorRect))
    {
        failure = L"hardware capsule is present without being declared";
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

static int Quantise(const int width)
{
    const int remainder = width % kEntryWidthQuantumLogical;
    return remainder == 0 ? width : width + kEntryWidthQuantumLogical - remainder;
}

int ResolveEntryWidthLogical(const int measuredContentWidthLogical)
{
    int width = measuredContentWidthLogical <= 0
        ? kEntryMinWidthLogical
        : measuredContentWidthLogical;
    width = std::clamp(width, kEntryMinWidthLogical, kEntryMaxWidthLogical);
    return std::min(Quantise(width), kEntryMaxWidthLogical);
}

int ResolveMonitorWidthLogical(const int measuredMonitorWidthLogical)
{
    if (measuredMonitorWidthLogical <= 0)
    {
        return 0;
    }

    return std::min(
        Quantise(std::min(measuredMonitorWidthLogical, kMonitorMaxWidthLogical)),
        kMonitorMaxWidthLogical);
}

static bool TryRefitWidth(
    const int measuredLogical,
    int& reservedLogical,
    int (*resolve)(int))
{
    const int measured = std::max(measuredLogical, 0);
    // Canonicalise first: the main capsule has a floor, so 0 and 96 describe the same reserved
    // width, and leaving both spellings in the field would make later comparisons lie.
    const int reserved = resolve(reservedLogical);
    reservedLogical = reserved;
    const int target = resolve(measured > 0 ? measured + kWidthHeadroomLogical : 0);

    // Grows only when the content genuinely no longer fits, and shrinks only once enough room
    // has gone spare. Comparing resolved widths rather than raw measurements is what keeps the
    // main capsule from twitching against its own 96 DIP floor, where a narrower clock changes
    // nothing that is actually reserved.
    const bool mustGrow = measured > reserved;
    const bool worthShrinking = reserved - target >= kWidthReclaimLogical;
    if (!mustGrow && !worthShrinking)
    {
        return false;
    }

    // Never shrink below what the content needs; a reclaim that clipped would be worse than
    // the wasted room it recovered.
    const int next = std::max(target, resolve(measured));
    if (next == reserved)
    {
        return false;
    }

    reservedLogical = next;
    return true;
}

bool TryRefitEntryWidth(const int measuredLogical, int& reservedLogical)
{
    return TryRefitWidth(measuredLogical, reservedLogical, ResolveEntryWidthLogical);
}

bool TryRefitMonitorWidth(const int measuredLogical, int& reservedLogical)
{
    return TryRefitWidth(measuredLogical, reservedLogical, ResolveMonitorWidthLogical);
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

bool IsWindows11OrLater() noexcept
{
    // RtlGetVersion reports the real OS. GetVersionExW would lie here: without a
    // supportedOS manifest entry it caps the answer at an older version, and tying a
    // placement decision to the manifest would make it change silently with build settings.
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    static const bool isWindows11OrLater = []() noexcept {
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr)
        {
            return false;
        }

        const auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(
            GetProcAddress(ntdll, "RtlGetVersion"));
        if (rtlGetVersion == nullptr)
        {
            return false;
        }

        RTL_OSVERSIONINFOW info{};
        info.dwOSVersionInfoSize = sizeof(info);
        if (rtlGetVersion(&info) != 0)
        {
            return false;
        }

        return info.dwMajorVersion > 10 ||
            (info.dwMajorVersion == 10 && info.dwBuildNumber >= 22000);
    }();
    return isWindows11OrLater;
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

    // Nothing here asks Explorer anything. This runs on the entry's UI thread, and
    // SHAppBarMessage(ABM_GETSTATE / ABM_GETTASKBARPOS) is a synchronous send into the tray
    // window with no timeout: while Explorer is busy - an MSIX servicing pass broadcasting
    // WM_SETTINGCHANGE is one observed case, and that broadcast is itself what schedules this
    // capture - the send blocks, the entry stops pumping, and Windows kills it as a hung
    // application. Every child process then dies with it through the job object.
    //
    // Neither reading is missed. ABM_GETTASKBARPOS reported only the primary taskbar and its
    // result was never read. ABM_GETSTATE's auto-hide flag is implied by the geometry already
    // captured: an auto-hidden taskbar leaves the work area at, or within a pixel or two of,
    // the whole monitor, which is below kMinimumTaskbarThicknessPx, so both DeriveTaskbarStrip
    // and IsSingleEdgeDifference already reject it and the entry degrades exactly as before.
    // The one case the flag covered and geometry cannot: auto-hide combined with some other
    // application reserving a single work-area edge, which is indistinguishable from a taskbar
    // on that edge.
    snapshot.hasTaskbarStrip = DeriveTaskbarStrip(
        snapshot.monitorRect,
        snapshot.workArea,
        snapshot.inferredTaskbarEdge,
        snapshot.taskbarStrip);
    snapshot.systemIconsLeftAligned = QuerySystemIconsLeftAligned();
    // The Windows 10 taskbar fills its strip with window buttons this geometry cannot see;
    // embedding there would cover them, which SYS-003 forbids. The strip is only trusted to
    // have hostable free space on the Windows 11 taskbar shape.
    snapshot.taskbarStripTrusted = IsWindows11OrLater();

    return true;
}

// Places the entry along the taskbar strip. Returns false when the strip cannot host it.
static bool TryMakeEmbeddedPlacement(
    const MonitorSnapshot& snapshot,
    const LauncherEntryPlacementPreference alignment,
    const int measuredContentWidthLogical,
    const int measuredMonitorWidthLogical,
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

    // The hardware capsule rides beside the main one inside a single window: two windows would
    // mean two placements to keep in step, two z-order fights with the taskbar and two things
    // to hide for a fullscreen app. Its own width is resolved separately so the main capsule's
    // minimum never applies to it.
    const int monitorLogical =
        ResolveMonitorWidthLogical(measuredMonitorWidthLogical);
    int capsuleGap = 0;
    int monitorWidth = 0;
    if (monitorLogical > 0)
    {
        capsuleGap = ScaleLogicalPixels(kCapsuleGapLogical, placement.dpi);
        monitorWidth = ScaleLogicalPixels(monitorLogical, placement.dpi);
    }

    const int totalWidth = width + capsuleGap + monitorWidth;
    if (RectWidth(strip) < totalWidth + gap * 2)
    {
        // The pair does not fit. Drop the hardware capsule rather than the entry itself: the
        // launcher must still be reachable on a narrow strip.
        if (monitorWidth == 0 || RectWidth(strip) < width + gap * 2)
        {
            return false;
        }

        capsuleGap = 0;
        monitorWidth = 0;
    }

    const int blockWidth = width + capsuleGap + monitorWidth;
    int left = strip.left + gap;
    switch (alignment)
    {
    case LauncherEntryPlacementPreference::EmbeddedCenter:
        left = strip.left + (RectWidth(strip) - blockWidth) / 2;
        break;
    case LauncherEntryPlacementPreference::EmbeddedRight:
        left = strip.right - gap - blockWidth;
        break;
    case LauncherEntryPlacementPreference::EmbeddedLeft:
    case LauncherEntryPlacementPreference::Floating:
    default:
        break;
    }

    const int top = strip.top + (RectHeight(strip) - height) / 2;
    placement.mode = LauncherPlacementMode::ExactWidgetReplacement;
    placement.windowRect = RECT{left, top, left + blockWidth, top + height};
    // The main capsule is the button. The rest of the window - the gap, and the hardware
    // capsule's own rounded corners - is shaped by per-pixel alpha and passes input through.
    placement.hitRect = RECT{left, top, left + width, top + height};
    if (monitorWidth > 0)
    {
        placement.monitorRect = RECT{
            left + width + capsuleGap,
            top,
            left + width + capsuleGap + monitorWidth,
            top + height};
        placement.hasMonitorRect = true;
    }

    return true;
}

LauncherPlacement ResolveLauncherPlacement(
    const MonitorSnapshot& snapshot,
    const UINT dpi,
    const LauncherEntryPreferences& preferences,
    const int measuredContentWidthLogical,
    const int measuredMonitorWidthLogical)
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

    if (snapshot.taskbarStripTrusted &&
        ResolveAlignmentMode(preferences.placement) ==
        LauncherPlacementMode::ExactWidgetReplacement)
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
                preferences.showSystemMonitor ? measuredMonitorWidthLogical : 0,
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

    // An auto-hidden taskbar reserves nothing, so no edge clears the thickness floor and this
    // is false without a separate flag to say so.
    const bool hasReliableTaskbarEdge =
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

    placement.reason = !snapshot.taskbarStripTrusted
        ? L"pre-Windows-11 taskbar owns its strip; embedded placement disabled, entry stays in the work area"
        : hasReliableTaskbarEdge
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

const wchar_t* ToString(const LauncherHotkeyPreference hotkey)
{
    switch (hotkey)
    {
    case LauncherHotkeyPreference::Disabled:
        return L"disabled";
    case LauncherHotkeyPreference::CtrlAltD:
        return L"ctrl-alt-d";
    case LauncherHotkeyPreference::CtrlAltQ:
        return L"ctrl-alt-q";
    case LauncherHotkeyPreference::CtrlAltB:
    default:
        return L"ctrl-alt-b";
    }
}

LauncherHotkeyBinding ToHotkeyBinding(const LauncherHotkeyPreference hotkey)
{
    // MOD_NOREPEAT everywhere: holding the chord down has to open the panel once, not once
    // per keyboard repeat. The Windows key is deliberately absent from every preset - the
    // shell owns most of that space and takes more of it in updates.
    //
    // Every preset is a letter rather than a punctuation or space key. Punctuation moves
    // between layouts, so the menu would print a chord the keyboard cannot produce; and the
    // obvious candidates there are already taken - on the reference machine Ctrl+Alt+Space is
    // the Microsoft IME's half-width toggle and Ctrl+Alt+W is held by another application,
    // both of which registered as nothing at all when they were the presets here.
    switch (hotkey)
    {
    case LauncherHotkeyPreference::CtrlAltB:
        return {MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'B'};
    case LauncherHotkeyPreference::CtrlAltD:
        return {MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'D'};
    case LauncherHotkeyPreference::CtrlAltQ:
        return {MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'Q'};
    case LauncherHotkeyPreference::Disabled:
    default:
        return {};
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

    // A pre-Windows-11 taskbar fills its strip with window buttons the geometry cannot see,
    // so an untrusted strip must never host an embedded entry - only the work-area slot.
    MonitorSnapshot untrustedStrip = bottomTaskbar;
    untrustedStrip.taskbarStripTrusted = false;
    const LauncherPlacement untrusted =
        ResolveLauncherPlacement(untrustedStrip, 96, embeddedLeft, 160);
    if (untrusted.mode != LauncherPlacementMode::SafeTaskbarSlot ||
        !CheckPlacement(untrustedStrip, untrusted, failure))
    {
        if (failure.empty())
        {
            failure = L"untrusted taskbar strip still embedded the entry";
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

    // The width refit rule. The case that matters is the one that used to lose a column: the
    // readings grew by only a couple of DIP, the old symmetric hysteresis kept the window
    // still, and the layout dropped the whole network column because it no longer fitted.
    {
        int reserved = 0;
        if (!TryRefitMonitorWidth(149, reserved) || reserved < 149)
        {
            failure = L"the first measurement did not reserve room for itself";
            return false;
        }

        const int afterFirst = reserved;
        if (TryRefitMonitorWidth(154, reserved) || reserved != afterFirst)
        {
            failure = L"a reading that still fits moved the entry";
            return false;
        }

        // Beyond the reserved room it must move, however small the step.
        if (!TryRefitMonitorWidth(afterFirst + 1, reserved) ||
            reserved <= afterFirst)
        {
            failure = L"content wider than the reserved room did not refit";
            return false;
        }

        // Room is reclaimed only once enough of it has gone spare.
        const int wide = reserved;
        if (TryRefitMonitorWidth(wide - kWidthReclaimLogical + 1, reserved) ||
            reserved != wide)
        {
            failure = L"a small shrink reclaimed room and moved the entry";
            return false;
        }

        if (!TryRefitMonitorWidth(40, reserved) || reserved >= wide || reserved < 40)
        {
            failure = L"a large shrink did not reclaim room";
            return false;
        }

        // Absent readings collapse the capsule entirely rather than holding a hole open.
        if (!TryRefitMonitorWidth(0, reserved) || reserved != 0)
        {
            failure = L"an empty hardware capsule still reserved room";
            return false;
        }

        // The main capsule has a floor. Anything narrower than it is already covered, so a
        // clock whose text keeps changing under that floor must never re-place the entry.
        int mainReserved = 0;
        if (TryRefitEntryWidth(60, mainReserved) ||
            TryRefitEntryWidth(40, mainReserved) ||
            mainReserved != ResolveEntryWidthLogical(0))
        {
            failure = L"the main capsule twitched against its own minimum width";
            return false;
        }

        // Past the floor it reserves room like any other capsule.
        if (!TryRefitEntryWidth(200, mainReserved) || mainReserved < 200)
        {
            failure = L"the main capsule did not reserve room past its minimum";
            return false;
        }
    }

    // The hardware capsule rides beside the main one, never inside it, and only when it was
    // asked for.
    constexpr LauncherEntryPreferences withMonitor{
        .placement = LauncherEntryPlacementPreference::EmbeddedLeft,
        .showSystemMonitor = true,
    };
    const LauncherPlacement paired =
        ResolveLauncherPlacement(bottomTaskbar, 96, withMonitor, 160, 200);
    if (paired.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        !paired.hasMonitorRect ||
        paired.monitorRect.left <= paired.hitRect.right ||
        paired.monitorRect.right != paired.windowRect.right ||
        paired.hitRect.left != paired.windowRect.left ||
        RectWidth(paired.windowRect) <= RectWidth(left.windowRect) ||
        !CheckPlacement(bottomTaskbar, paired, failure))
    {
        if (failure.empty())
        {
            failure = L"the hardware capsule is not placed beside the main one";
        }
        return false;
    }

    // Turning it off must leave the entry exactly as it was, and asking for it with nothing
    // to show must not reserve a hole for it.
    const LauncherPlacement unpaired =
        ResolveLauncherPlacement(bottomTaskbar, 96, embeddedLeft, 160, 200);
    const LauncherPlacement empty =
        ResolveLauncherPlacement(bottomTaskbar, 96, withMonitor, 160, 0);
    if (unpaired.hasMonitorRect ||
        empty.hasMonitorRect ||
        RectWidth(unpaired.windowRect) != RectWidth(left.windowRect) ||
        RectWidth(empty.windowRect) != RectWidth(left.windowRect))
    {
        failure = L"the hardware capsule reserved room it was not asked for";
        return false;
    }

    // A right-aligned pair ends at the strip edge: the block is aligned, not just the main
    // capsule, or the hardware readings would hang off the end.
    const LauncherPlacement pairedRight = ResolveLauncherPlacement(
        bottomTaskbar,
        96,
        LauncherEntryPreferences{
            .placement = LauncherEntryPlacementPreference::EmbeddedRight,
            .showSystemMonitor = true,
        },
        160,
        200);
    if (pairedRight.mode != LauncherPlacementMode::ExactWidgetReplacement ||
        pairedRight.windowRect.right !=
            bottomTaskbar.taskbarStrip.right - kEdgeGapLogical ||
        !CheckPlacement(bottomTaskbar, pairedRight, failure))
    {
        if (failure.empty())
        {
            failure = L"a right-aligned pair did not end at the strip edge";
        }
        return false;
    }

    // Auto-hide, vertical taskbars and strips too thin for a 32 DIP target must degrade.
    // Auto-hide is expressed as the geometry it actually produces - a work area that keeps
    // the whole monitor bar the unhide trigger band - rather than as a flag, because that
    // band is what the entry now reads.
    MonitorSnapshot autoHidden{
        .monitor = reinterpret_cast<HMONITOR>(4),
        .monitorRect = RECT{0, 0, 1920, 1080},
        .workArea = RECT{0, 0, 1920, 1079},
        .inferredTaskbarEdge = TaskbarEdge::Unknown,
    };
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

    // An auto-hidden taskbar reserves nothing but the unhide trigger band, which is thinner
    // than any real taskbar, so no edge is reliable and the entry falls back.
    MonitorSnapshot autoHidden = bottomTaskbar;
    autoHidden.workArea = RECT{0, 0, 1920, 1079};
    autoHidden.taskbarStrip = RECT{};
    autoHidden.hasTaskbarStrip = false;
    autoHidden.inferredTaskbarEdge = TaskbarEdge::Unknown;
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
