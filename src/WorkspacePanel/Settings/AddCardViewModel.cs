using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// One card the picker offers. Every built-in card is always offered - a type that is
/// already on the board is added again as a new instance with its own identity.
/// </summary>
public sealed class AddCardOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public AddCardOption(
        string instanceId,
        string displayName,
        CardSize defaultSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        InstanceId = instanceId;
        DisplayName = displayName ?? instanceId;
        DefaultSize = defaultSize;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InstanceId { get; }

    public string DisplayName { get; }

    public CardSize DefaultSize { get; }

    /// <summary>
    /// Stable per instance so the UIA scripts can address one row without depending on the
    /// order the picker happens to list them in.
    /// </summary>
    public string IncludeAutomationId => "AddCardInclude_" + InstanceId;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise();
        }
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Backs the add-card picker. It offers every built-in card; a type already on the board is
/// offered again as a fresh instance ("demo.weather#2"), so a board can carry two weather
/// cards for two places worth watching. Adding is a layout edit like any other, so the dialog
/// only reports the choice and the caller applies it through
/// <see cref="CardLayoutEditViewModel"/> where undo can see it.
///
/// WinUI-free on purpose - see the unit-test project's linked sources.
/// </summary>
public sealed class AddCardViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<AddCardOption> _options = [];
    private string _statusText = string.Empty;

    public AddCardViewModel(
        IEnumerable<string> placedInstanceIds,
        Func<string, string?>? resourceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(placedInstanceIds);
        Func<string, string?> resolve = resourceResolver ?? (static key => key);
        HashSet<string> taken = placedInstanceIds.ToHashSet(StringComparer.Ordinal);

        foreach (BuiltInCardInstance candidate in BuiltInCardCatalog.Addable)
        {
            // Each option claims the ID it would add up front, so several selections in one
            // pass cannot collide with each other or with what the board already holds.
            string instanceId = BuiltInCardCatalog.CreateInstanceId(
                candidate.InstanceId,
                taken);
            taken.Add(instanceId);
            string? title = resolve(candidate.Definition.TitleResourceKey);
            AddCardOption option = new(
                instanceId,
                string.IsNullOrWhiteSpace(title)
                    ? candidate.Definition.CardTypeId
                    : title,
                candidate.Definition.DefaultSize);
            option.PropertyChanged += Option_PropertyChanged;
            _options.Add(option);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AddCardOption> Options => _options;

    /// <summary>False when every built-in card is already placed.</summary>
    public bool HasOptions => _options.Count > 0;

    public bool CanAdd => _options.Any(option => option.IsSelected);

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value ?? string.Empty;
            Raise();
        }
    }

    /// <summary>
    /// The chosen cards in list order, so several added at once land in the order the picker
    /// showed them rather than in click order.
    /// </summary>
    public IReadOnlyList<AddCardOption> Selected =>
        _options.Where(option => option.IsSelected).ToArray();

    private void Option_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddCardOption.IsSelected))
        {
            Raise(nameof(CanAdd));
        }
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
