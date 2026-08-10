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

struct MonitorSnapshot
{
    HMONITOR monitor{};
    RECT monitorRect{};
    RECT workArea{};
    RECT taskbarRect{};
    bool hasTaskbarRect{};
    bool taskbarAutoHide{};
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

bool QueryMonitorSnapshot(
    HMONITOR monitor,
    MonitorSnapshot& snapshot,
    std::wstring& error);

LauncherPlacement ResolveLauncherPlacement(
    const MonitorSnapshot& snapshot,
    UINT dpi);

const wchar_t* ToString(TaskbarEdge edge);
const wchar_t* ToString(LauncherPlacementMode mode);

// This exercises the placement invariants with synthetic monitor layouts. It
// deliberately does not inspect or modify the current desktop.
bool RunGeometryContractSmokeTest(std::wstring& failure);
}
