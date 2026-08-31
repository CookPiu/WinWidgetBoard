#include "LauncherPreferences.h"

#include <windows.h>

namespace winwidgetboard::launcher
{
namespace
{
constexpr wchar_t kPreferencesKey[] = L"Software\\WinWidgetBoard\\Launcher";
constexpr wchar_t kPlacementValue[] = L"Placement";
constexpr wchar_t kLeftAlignFallbackValue[] = L"LeftAlignFallback";
constexpr wchar_t kContentValue[] = L"Content";
constexpr wchar_t kShowSystemMonitorValue[] = L"ShowSystemMonitor";
constexpr wchar_t kHotkeyValue[] = L"Hotkey";

// The value Content used to carry when the hardware monitor was a third content mode rather
// than its own capsule. A profile written by that build is read once and split into the two
// settings it became; nothing rewrites it until the user changes something, so downgrading
// keeps working.
constexpr DWORD kLegacySystemMonitorContent = 2u;

bool TryReadDword(const wchar_t* name, DWORD& value)
{
    DWORD size = sizeof(value);
    DWORD type = REG_DWORD;
    return RegGetValueW(
        HKEY_CURRENT_USER,
        kPreferencesKey,
        name,
        RRF_RT_REG_DWORD,
        &type,
        &value,
        &size) == ERROR_SUCCESS;
}

LauncherEntryPlacementPreference ToPlacement(const DWORD value)
{
    switch (value)
    {
    case 1:
        return LauncherEntryPlacementPreference::EmbeddedCenter;
    case 2:
        return LauncherEntryPlacementPreference::EmbeddedRight;
    case 3:
        return LauncherEntryPlacementPreference::Floating;
    case 0:
    default:
        return LauncherEntryPlacementPreference::EmbeddedLeft;
    }
}

LauncherLeftAlignFallback ToLeftAlignFallback(const DWORD value)
{
    switch (value)
    {
    case 1:
        return LauncherLeftAlignFallback::EmbeddedCenter;
    case 2:
        return LauncherLeftAlignFallback::EmbeddedRight;
    case 0:
    default:
        return LauncherLeftAlignFallback::Floating;
    }
}

LauncherContentMode ToContent(const DWORD value)
{
    switch (value)
    {
    case 1:
        return LauncherContentMode::Weather;
    case 0:
    default:
        return LauncherContentMode::DateTime;
    }
}

DWORD FromPlacement(const LauncherEntryPlacementPreference placement)
{
    switch (placement)
    {
    case LauncherEntryPlacementPreference::EmbeddedCenter:
        return 1;
    case LauncherEntryPlacementPreference::EmbeddedRight:
        return 2;
    case LauncherEntryPlacementPreference::Floating:
        return 3;
    case LauncherEntryPlacementPreference::EmbeddedLeft:
    default:
        return 0;
    }
}

DWORD FromLeftAlignFallback(const LauncherLeftAlignFallback fallback)
{
    switch (fallback)
    {
    case LauncherLeftAlignFallback::EmbeddedCenter:
        return 1;
    case LauncherLeftAlignFallback::EmbeddedRight:
        return 2;
    case LauncherLeftAlignFallback::Floating:
    default:
        return 0;
    }
}

LauncherHotkeyPreference ToHotkey(const DWORD value)
{
    switch (value)
    {
    case 0:
        return LauncherHotkeyPreference::Disabled;
    case 2:
        return LauncherHotkeyPreference::CtrlAltD;
    case 3:
        return LauncherHotkeyPreference::CtrlAltQ;
    case 1:
    default:
        return LauncherHotkeyPreference::CtrlAltB;
    }
}

DWORD FromHotkey(const LauncherHotkeyPreference hotkey)
{
    switch (hotkey)
    {
    case LauncherHotkeyPreference::Disabled:
        return 0u;
    case LauncherHotkeyPreference::CtrlAltD:
        return 2u;
    case LauncherHotkeyPreference::CtrlAltQ:
        return 3u;
    case LauncherHotkeyPreference::CtrlAltB:
    default:
        return 1u;
    }
}

DWORD FromContent(const LauncherContentMode content)
{
    switch (content)
    {
    case LauncherContentMode::Weather:
        return 1u;
    case LauncherContentMode::DateTime:
    default:
        return 0u;
    }
}

bool WriteDword(const HKEY key, const wchar_t* name, const DWORD value)
{
    return RegSetValueExW(
        key,
        name,
        0,
        REG_DWORD,
        reinterpret_cast<const BYTE*>(&value),
        sizeof(value)) == ERROR_SUCCESS;
}
}

LauncherEntryPreferences LoadLauncherPreferences()
{
    LauncherEntryPreferences preferences{};

    DWORD value = 0;
    if (TryReadDword(kPlacementValue, value))
    {
        preferences.placement = ToPlacement(value);
    }
    if (TryReadDword(kLeftAlignFallbackValue, value))
    {
        preferences.leftAlignFallback = ToLeftAlignFallback(value);
    }
    if (TryReadDword(kContentValue, value))
    {
        preferences.content = ToContent(value);
        if (value == kLegacySystemMonitorContent)
        {
            // A profile from the three-way build: the user had asked for readings, so keep
            // showing them, with the clock back in the capsule they used to replace.
            preferences.showSystemMonitor = true;
        }
    }
    if (TryReadDword(kShowSystemMonitorValue, value))
    {
        preferences.showSystemMonitor = value != 0u;
    }
    // Absent means the default, which is on: a shortcut nobody is told about is a shortcut
    // nobody uses, and the context menu both names the chord and can switch it off.
    if (TryReadDword(kHotkeyValue, value))
    {
        preferences.hotkey = ToHotkey(value);
    }

    return preferences;
}

bool SaveLauncherPreferences(const LauncherEntryPreferences& preferences)
{
    HKEY key = nullptr;
    if (RegCreateKeyExW(
            HKEY_CURRENT_USER,
            kPreferencesKey,
            0,
            nullptr,
            REG_OPTION_NON_VOLATILE,
            KEY_SET_VALUE,
            nullptr,
            &key,
            nullptr) != ERROR_SUCCESS)
    {
        return false;
    }

    const bool saved =
        WriteDword(key, kPlacementValue, FromPlacement(preferences.placement)) &&
        WriteDword(
            key,
            kLeftAlignFallbackValue,
            FromLeftAlignFallback(preferences.leftAlignFallback)) &&
        WriteDword(key, kContentValue, FromContent(preferences.content)) &&
        WriteDword(
            key,
            kShowSystemMonitorValue,
            preferences.showSystemMonitor ? 1u : 0u) &&
        WriteDword(key, kHotkeyValue, FromHotkey(preferences.hotkey));
    RegCloseKey(key);
    return saved;
}
}
