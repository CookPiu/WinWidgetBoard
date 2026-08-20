#pragma once

#include <windows.h>

#include <string>

namespace winwidgetboard::launcher
{
enum class TaskbarEdge : unsigned char
{
    Unknown,
    Top,
    Bottom,
    Left,
    Right,
};

enum class LauncherPlacementMode : unsigned char
{
    ExactWidgetReplacement,
    SafeTaskbarSlot,
    EdgeTabFallback,
    Unavailable,
};

// What the user asked for. Embedded modes put the entry inside the taskbar strip itself;
// Floating keeps the historical square button just above the strip, inside the work area.
enum class LauncherEntryPlacementPreference : unsigned char
{
    EmbeddedLeft,
    EmbeddedCenter,
    EmbeddedRight,
    Floating,
};

// Where to go when EmbeddedLeft would collide with a left-aligned system icon cluster,
// because the Start button then owns the far left and must not be covered.
enum class LauncherLeftAlignFallback : unsigned char
{
    Floating,
    EmbeddedCenter,
    EmbeddedRight,
};

enum class LauncherContentMode : unsigned char
{
    DateTime,
    Weather,
};

struct LauncherEntryPreferences
{
    LauncherEntryPlacementPreference placement{
        LauncherEntryPlacementPreference::EmbeddedLeft};
    LauncherLeftAlignFallback leftAlignFallback{LauncherLeftAlignFallback::Floating};
    LauncherContentMode content{LauncherContentMode::DateTime};
};

struct MonitorSnapshot
{
    HMONITOR monitor{};
    RECT monitorRect{};
    RECT workArea{};
    RECT taskbarRect{};
    bool hasTaskbarRect{};
    bool taskbarAutoHide{};
    // The taskbar strip derived from monitorRect minus workArea. Unlike taskbarRect this
    // does not depend on ABM_GETTASKBARPOS, which only reports the primary taskbar.
    RECT taskbarStrip{};
    bool hasTaskbarStrip{};
    // HKCU TaskbarAl == 0. The Start button then sits at the far left of the strip.
    bool systemIconsLeftAligned{};
    TaskbarEdge inferredTaskbarEdge{TaskbarEdge::Unknown};
};

struct LauncherPlacement
{
    LauncherPlacementMode mode{LauncherPlacementMode::Unavailable};
    TaskbarEdge edge{TaskbarEdge::Unknown};
    UINT dpi{96};
    RECT windowRect{};
    RECT hitRect{};
    std::wstring reason;
};

bool IsValidRect(const RECT& rectangle);
bool IsRectWithin(const RECT& outer, const RECT& inner);
TaskbarEdge InferTaskbarEdge(const RECT& monitorRect, const RECT& workArea);

// Derives the taskbar strip as the single-edge difference between the monitor and the work
// area. Returns false when the difference is not a plausible taskbar on that edge.
bool DeriveTaskbarStrip(
    const RECT& monitorRect,
    const RECT& workArea,
    TaskbarEdge edge,
    RECT& strip);

// Clamps a measured content width into the supported range and quantises it to the 4 DIP
// rhythm the UI spec uses, so an adaptive width cannot jitter by single pixels.
int ResolveEntryWidthLogical(int measuredContentWidthLogical);

bool QueryMonitorSnapshot(
    HMONITOR monitor,
    MonitorSnapshot& snapshot,
    std::wstring& error);

LauncherPlacement ResolveLauncherPlacement(
    const MonitorSnapshot& snapshot,
    UINT dpi,
    const LauncherEntryPreferences& preferences,
    int measuredContentWidthLogical);

const wchar_t* ToString(TaskbarEdge edge);
const wchar_t* ToString(LauncherPlacementMode mode);
const wchar_t* ToString(LauncherEntryPlacementPreference placement);
const wchar_t* ToString(LauncherContentMode content);

// This exercises the placement invariants with synthetic monitor layouts. It
// deliberately does not inspect or modify the current desktop.
bool RunGeometryContractSmokeTest(std::wstring& failure);
}
