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

DWORD FromContent(const LauncherContentMode content)
{
    return content == LauncherContentMode::Weather ? 1u : 0u;
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
        WriteDword(key, kContentValue, FromContent(preferences.content));
    RegCloseKey(key);
    return saved;
}
}
