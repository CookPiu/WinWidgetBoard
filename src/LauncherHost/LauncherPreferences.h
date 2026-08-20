#pragma once

#include "TaskbarGeometry.h"

namespace winwidgetboard::launcher
{
// Entry preferences live in HKCU rather than the CoreBroker database: the entry has to place
// itself before it can reach the broker, and these are launcher-local view settings, not
// user data. Unknown or missing values fall back to the defaults instead of failing.
LauncherEntryPreferences LoadLauncherPreferences();

bool SaveLauncherPreferences(const LauncherEntryPreferences& preferences);
}
