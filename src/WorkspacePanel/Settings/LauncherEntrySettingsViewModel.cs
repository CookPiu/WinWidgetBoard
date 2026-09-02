using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// The taskbar entry's preference store: the HKCU key the launcher reads at startup and
/// watches for changes. The panel writes plain values; the launcher applies them live and
/// writes one value back (whether the hotkey actually registered). Behind an interface so
/// the view model is testable without a registry.
/// </summary>
public interface ILauncherPreferenceStore
{
    int? ReadValue(string name);

    bool WriteValue(string name, int value);
}

public sealed class RegistryLauncherPreferenceStore : ILauncherPreferenceStore
{
    private const string KeyPath = @"Software\WinWidgetBoard\Launcher";

    public int? ReadValue(string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception exception)
            when (exception is System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public bool WriteValue(string name, int value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception exception)
            when (exception is System.Security.SecurityException or
                IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The taskbar entry's settings: what the main capsule shows, whether the hardware capsule
/// draws, where the entry sits, and the global shortcut - including a recorded custom chord.
///
/// Everything here applies immediately, like autostart: these are HKCU view settings the
/// launcher watches, not versioned broker records, so there is no commit row and no conflict.
/// The one asynchronous fact is whether a chosen chord actually registered - the launcher
/// writes that back to the same key, and <see cref="RefreshHotkeyState"/> reads it.
/// </summary>
public sealed class LauncherEntrySettingsViewModel : INotifyPropertyChanged
{
    // Value names shared with src/LauncherHost/LauncherPreferences.cpp; the two lists must
    // stay in step or the pages stop talking about the same settings.
    private const string ContentValueName = "Content";
    private const string ShowSystemMonitorValueName = "ShowSystemMonitor";
    private const string PlacementValueName = "Placement";
    private const string LeftAlignFallbackValueName = "LeftAlignFallback";
    private const string HotkeyValueName = "Hotkey";
    private const string HotkeyModifiersValueName = "HotkeyModifiers";
    private const string HotkeyKeyValueName = "HotkeyKey";
    private const string HotkeyActiveValueName = "HotkeyActive";

    private const int HotkeyModeCustom = 4;

    public const int ModifierAlt = 0x1;
    public const int ModifierControl = 0x2;
    public const int ModifierShift = 0x4;

    private readonly ILauncherPreferenceStore _store;
    private readonly Func<string, string?> _resources;
    private int _contentModeIndex;
    private bool _showSystemMonitor;
    private int _placementIndex;
    private int _leftAlignFallbackIndex;
    private int _hotkeyModeIndex;
    private int _customModifiers;
    private int _customVirtualKey;
    private string _statusText = string.Empty;
    private bool _loading;

    public LauncherEntrySettingsViewModel(
        ILauncherPreferenceStore store,
        Func<string, string?> resources)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));

        _loading = true;
        _contentModeIndex = Clamp(_store.ReadValue(ContentValueName) ?? 0, 0, 1);
        _showSystemMonitor = (_store.ReadValue(ShowSystemMonitorValueName) ?? 0) != 0;
        _placementIndex = Clamp(_store.ReadValue(PlacementValueName) ?? 0, 0, 3);
        _leftAlignFallbackIndex =
            Clamp(_store.ReadValue(LeftAlignFallbackValueName) ?? 0, 0, 2);
        // Absent means the default preset, which is on - mirroring the launcher's own load.
        _hotkeyModeIndex = Clamp(_store.ReadValue(HotkeyValueName) ?? 1, 0, 4);
        _customModifiers = _store.ReadValue(HotkeyModifiersValueName) ?? 0;
        _customVirtualKey = _store.ReadValue(HotkeyKeyValueName) ?? 0;
        _loading = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>0 = date and time, 1 = weather.</summary>
    public int ContentModeIndex
    {
        get => _contentModeIndex;
        set => Apply(ref _contentModeIndex, Clamp(value, 0, 1), ContentValueName);
    }

    public bool ShowSystemMonitor
    {
        get => _showSystemMonitor;
        set
        {
            if (_showSystemMonitor == value)
            {
                return;
            }

            _showSystemMonitor = value;
            Persist(ShowSystemMonitorValueName, value ? 1 : 0);
            Raise();
        }
    }

    /// <summary>0 = embedded left, 1 = centre, 2 = right, 3 = floating.</summary>
    public int PlacementIndex
    {
        get => _placementIndex;
        set => Apply(ref _placementIndex, Clamp(value, 0, 3), PlacementValueName);
    }

    /// <summary>0 = floating, 1 = embedded centre, 2 = embedded right.</summary>
    public int LeftAlignFallbackIndex
    {
        get => _leftAlignFallbackIndex;
        set => Apply(
            ref _leftAlignFallbackIndex,
            Clamp(value, 0, 2),
            LeftAlignFallbackValueName);
    }

    /// <summary>0 = off, 1..3 = the presets, 4 = the recorded custom chord.</summary>
    public int HotkeyModeIndex
    {
        get => _hotkeyModeIndex;
        set
        {
            int clamped = Clamp(value, 0, 4);
            if (_hotkeyModeIndex == clamped)
            {
                return;
            }

            // Custom without a recorded chord would fall back to a preset on the launcher
            // side and look like nothing happened; the mode is stored, and the recorder box
            // (now visible) says what to do next.
            _hotkeyModeIndex = clamped;
            Persist(HotkeyValueName, clamped);
            Raise();
            Raise(nameof(IsCustomHotkey));
            Raise(nameof(CustomHotkeyText));
        }
    }

    public bool IsCustomHotkey => _hotkeyModeIndex == HotkeyModeCustom;

    public bool HasCustomChord => _customModifiers != 0 && _customVirtualKey != 0;

    public string CustomHotkeyText =>
        HasCustomChord ? FormatChord(_customModifiers, _customVirtualKey) : string.Empty;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                _statusText = value;
                Raise();
            }
        }
    }

    /// <summary>
    /// Records a chord. Only Ctrl/Alt/Shift may participate - the Windows key belongs to the
    /// shell - and at least one modifier is required, because a global bare key would eat
    /// ordinary typing everywhere.
    /// </summary>
    public bool TrySetCustomHotkey(int modifiers, int virtualKey)
    {
        const int allowed = ModifierAlt | ModifierControl | ModifierShift;
        if ((modifiers & allowed) == 0 ||
            (modifiers & ~allowed) != 0 ||
            virtualKey is < 0x08 or > 0xFE)
        {
            StatusText = Resolve("EntryHotkeyInvalidStatus");
            return false;
        }

        _customModifiers = modifiers & allowed;
        _customVirtualKey = virtualKey;
        bool saved =
            Persist(HotkeyModifiersValueName, _customModifiers) &&
            Persist(HotkeyKeyValueName, _customVirtualKey) &&
            Persist(HotkeyValueName, HotkeyModeCustom);
        if (_hotkeyModeIndex != HotkeyModeCustom)
        {
            _hotkeyModeIndex = HotkeyModeCustom;
            Raise(nameof(HotkeyModeIndex));
            Raise(nameof(IsCustomHotkey));
        }

        Raise(nameof(CustomHotkeyText));
        Raise(nameof(HasCustomChord));
        if (saved)
        {
            StatusText = Resolve("EntrySettingsAppliedStatus");
        }

        return saved;
    }

    /// <summary>
    /// Reads back whether the launcher's last registration attempt succeeded. The launcher
    /// writes this after applying a change, so the caller polls it once, shortly after one.
    /// </summary>
    public void RefreshHotkeyState()
    {
        if (_hotkeyModeIndex == 0)
        {
            StatusText = Resolve("EntrySettingsAppliedStatus");
            return;
        }

        int? active = _store.ReadValue(HotkeyActiveValueName);
        StatusText = active == 0
            ? Resolve("EntryHotkeyTakenStatus")
            : Resolve("EntrySettingsAppliedStatus");
    }

    public static string FormatChord(int modifiers, int virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModifierControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierShift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(FormatKey(virtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatKey(int virtualKey) => virtualKey switch
    {
        >= 'A' and <= 'Z' => ((char)virtualKey).ToString(),
        >= '0' and <= '9' => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),
        >= 0x60 and <= 0x69 => "Num " + (virtualKey - 0x60),
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        0xC0 => "`",
        _ => "0x" + virtualKey.ToString(
            "X2",
            System.Globalization.CultureInfo.InvariantCulture),
    };

    private void Apply(ref int field, int value, string valueName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Persist(valueName, value);
        Raise(PropertyNameFor(valueName));
    }

    private static string PropertyNameFor(string valueName) => valueName switch
    {
        ContentValueName => nameof(ContentModeIndex),
        PlacementValueName => nameof(PlacementIndex),
        LeftAlignFallbackValueName => nameof(LeftAlignFallbackIndex),
        _ => valueName,
    };

    private bool Persist(string valueName, int value)
    {
        if (_loading)
        {
            return true;
        }

        bool saved = _store.WriteValue(valueName, value);
        StatusText = Resolve(
            saved ? "EntrySettingsAppliedStatus" : "EntrySettingsFailedStatus");
        return saved;
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Clamp(value, minimum, maximum);

    private string Resolve(string key)
    {
        string? value = _resources(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
