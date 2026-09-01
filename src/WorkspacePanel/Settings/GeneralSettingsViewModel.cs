using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// The general section: settings that belong to the application rather than to a card.
///
/// It has no save button, and that is deliberate rather than an omission. The other sections
/// edit a versioned record in the broker, so they need a commit and can report a conflict;
/// this one asks the shell to do something, and the shell either did it or did not. A commit
/// row here would only be able to say "saved" after the change had already taken effect.
/// </summary>
public sealed class GeneralSettingsViewModel : INotifyPropertyChanged
{
    private readonly StartupShortcut _startupShortcut;
    private readonly Func<string, string?> _resources;
    private bool _isAutostartEnabled;
    private string _statusText = string.Empty;

    public GeneralSettingsViewModel(
        StartupShortcut startupShortcut,
        Func<string, string?> resources)
    {
        _startupShortcut = startupShortcut ??
            throw new ArgumentNullException(nameof(startupShortcut));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        // The shell is the source of truth, so the initial value is read from it rather than
        // defaulted: the user may have removed the shortcut in Explorer since last time.
        _isAutostartEnabled = _startupShortcut.IsEnabled;
        IsAutostartAvailable = _startupShortcut.IsAvailable;
        if (!IsAutostartAvailable)
        {
            _statusText = Resolve("GeneralAutostartUnavailableStatus");
        }

        VersionText = string.Format(
            CultureInfo.CurrentCulture,
            Resolve("GeneralVersionLabel"),
            typeof(GeneralSettingsViewModel).Assembly.GetName().Version?.ToString(3) ??
                "0.0.0");
    }

    /// <summary>The running build, stated where a user reporting a bug will look for it.</summary>
    public string VersionText { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// False for a build started straight out of the build output, which has no Start-menu
    /// shortcut to copy. The switch is disabled and the section says why.
    /// </summary>
    public bool IsAutostartAvailable { get; }

    public bool IsAutostartUnavailable => !IsAutostartAvailable;

    /// <summary>
    /// Applies immediately. On failure the property reverts to what the shell actually holds,
    /// so the switch can never sit in a position the machine does not agree with.
    /// </summary>
    public bool IsAutostartEnabled
    {
        get => _isAutostartEnabled;
        set
        {
            if (_isAutostartEnabled == value)
            {
                return;
            }

            bool applied = _startupShortcut.TrySetEnabled(value);
            _isAutostartEnabled = _startupShortcut.IsEnabled;
            StatusText = applied
                ? Resolve(
                    _isAutostartEnabled
                        ? "GeneralAutostartEnabledStatus"
                        : "GeneralAutostartDisabledStatus")
                : Resolve("GeneralAutostartFailedStatus");
            Raise(nameof(IsAutostartEnabled));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                _statusText = value;
                Raise(nameof(StatusText));
                Raise(nameof(HasStatusText));
            }
        }
    }

    public bool HasStatusText => _statusText.Length > 0;

    private string Resolve(string key)
    {
        string? value = _resources(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
