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

// What the main capsule shows. The hardware monitor is deliberately not in here: it lives in
// its own capsule beside this one, so it is a switch rather than a third choice, and a user
// who wants weather and readings at the same time can have both.
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
    bool showSystemMonitor{};
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
    // The whole window, spanning both capsules and the gap between them.
    RECT windowRect{};
    // The main capsule - the one that carries the indicator and toggles the panel.
    RECT hitRect{};
    // The hardware capsule to its right. Empty and inert unless showSystemMonitor is on and
    // the strip had room for it; the gap between the two is pass-through like any other
    // point outside a capsule.
    RECT monitorRect{};
    bool hasMonitorRect{};
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

// The hardware capsule is measured, clamped and quantised on its own scale: it has no minimum
// to hold open - it simply is not drawn when there is nothing to show - and its own ceiling.
int ResolveMonitorWidthLogical(int measuredMonitorWidthLogical);

// Decides whether a freshly measured content width needs the entry re-placed, and if so what
// to reserve. `reservedLogical` is both the width currently reserved and, on a true return,
// the width to reserve next.
//
// The rule is deliberately asymmetric. Growth is honoured the moment the content no longer
// fits, because a capsule that is too narrow does not clip its contents - the layout drops a
// whole column, and with the shipped configuration that column is the network readings, which
// is what made them wink out. Shrinking is what needs damping: a clock or a rate whose text
// gets a few pixels narrower must not make the entry twitch, so room is only reclaimed once
// there is a worthwhile amount of it. A refit also reserves a little headroom, so an
// oscillating reading does not re-place the window every couple of seconds.
bool TryRefitEntryWidth(int measuredLogical, int& reservedLogical);
bool TryRefitMonitorWidth(int measuredLogical, int& reservedLogical);

bool QueryMonitorSnapshot(
    HMONITOR monitor,
    MonitorSnapshot& snapshot,
    std::wstring& error);

LauncherPlacement ResolveLauncherPlacement(
    const MonitorSnapshot& snapshot,
    UINT dpi,
    const LauncherEntryPreferences& preferences,
    int measuredContentWidthLogical,
    int measuredMonitorWidthLogical = 0);

const wchar_t* ToString(TaskbarEdge edge);
const wchar_t* ToString(LauncherPlacementMode mode);
const wchar_t* ToString(LauncherEntryPlacementPreference placement);
const wchar_t* ToString(LauncherContentMode content);

// This exercises the placement invariants with synthetic monitor layouts. It
// deliberately does not inspect or modify the current desktop.
bool RunGeometryContractSmokeTest(std::wstring& failure);
}
