using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// Settings-dialog state for the built-in hardware monitor.
///
/// Both surfaces list all twelve metrics; a metric is included by its checkbox and positioned
/// by its place in the list. Keeping the unselected ones visible in the same list is what lets
/// the user move something into position before turning it on, instead of adding it at the end
/// and then walking it up.
/// </summary>
public sealed class SystemMonitorSettingsViewModel : INotifyPropertyChanged
{
    private readonly ISystemMonitorSettingsClient? _client;
    private readonly Func<string, string> _text;
    private string _statusText = string.Empty;
    private string _validationText = string.Empty;
    private int _revision;
    private bool _isBusy;
    private bool _wasSaved;
    private bool _isLoaded;

    public SystemMonitorSettingsViewModel(
        ISystemMonitorSettingsClient? client,
        Func<string, string>? textResolver = null)
    {
        _client = client;
        _text = textResolver ?? (static key => key);
        DetailOptions =
        [
            new SystemMonitorDetailOption(
                SystemMonitorDetail.Compact,
                _text("SysMonDetail.Compact")),
            new SystemMonitorDetailOption(
                SystemMonitorDetail.Normal,
                _text("SysMonDetail.Normal")),
            new SystemMonitorDetailOption(
                SystemMonitorDetail.Detailed,
                _text("SysMonDetail.Detailed")),
        ];
        CardOptions = BuildOptions(
            SystemMonitorSurface.Card,
            SystemMonitorContract.DefaultCardItems);
        EntryOptions = BuildOptions(
            SystemMonitorSurface.Entry,
            SystemMonitorContract.DefaultEntryItems);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SystemMonitorDetailOption> DetailOptions { get; }

    public ObservableCollection<SystemMonitorMetricOption> CardOptions { get; }

    public ObservableCollection<SystemMonitorMetricOption> EntryOptions { get; }

    public bool IsAvailable => _client is not null;

    public int Revision
    {
        get => _revision;
        private set => SetField(ref _revision, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ValidationText
    {
        get => _validationText;
        private set => SetField(ref _validationText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool WasSaved
    {
        get => _wasSaved;
        private set => SetField(ref _wasSaved, value);
    }

    public bool CanSave => IsAvailable && !IsBusy && _isLoaded;

    public IReadOnlyList<SystemMonitorItemDto> SelectedCardItems => Selected(CardOptions);

    public IReadOnlyList<SystemMonitorItemDto> SelectedEntryItems => Selected(EntryOptions);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            StatusText = _text("SysMonSettingsUnavailableStatus");
            return;
        }

        IsBusy = true;
        try
        {
            SystemMonitorSettingsDto settings = await _client
                .GetSystemMonitorSettingsAsync(
                    BuiltInCardCatalog.SystemMonitorInstanceId,
                    cancellationToken)
                .ConfigureAwait(true);
            Revision = settings.Revision;
            ApplyItems(CardOptions, settings.CardItems);
            ApplyItems(EntryOptions, settings.EntryItems);
            _isLoaded = true;
            StatusText = _text("SysMonSettingsLoadedStatus");
        }
        catch (CoreBrokerClientException)
        {
            StatusText = _text("SysMonSettingsLoadFailedStatus");
        }
        catch (IOException)
        {
            StatusText = _text("SysMonSettingsLoadFailedStatus");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        WasSaved = false;
        if (_client is null || !_isLoaded)
        {
            StatusText = _text("SysMonSettingsUnavailableStatus");
            return false;
        }

        if (!Validate())
        {
            return false;
        }

        IsBusy = true;
        try
        {
            SystemMonitorSettingsDto saved = await _client
                .SaveSystemMonitorSettingsAsync(
                    new SystemMonitorSettingsSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        InstanceId = BuiltInCardCatalog.SystemMonitorInstanceId,
                        CardItems = SelectedCardItems,
                        EntryItems = SelectedEntryItems,
                        ExpectedRevision = Revision,
                    },
                    cancellationToken)
                .ConfigureAwait(true);
            Revision = saved.Revision;
            ApplyItems(CardOptions, saved.CardItems);
            ApplyItems(EntryOptions, saved.EntryItems);
            StatusText = _text("SysMonSettingsSavedStatus");
            WasSaved = true;
            return true;
        }
        catch (CoreBrokerClientException exception)
        {
            // A revision conflict is someone else's save, not bad input, and it is recoverable
            // by reloading - so it gets its own wording rather than a generic failure.
            StatusText = string.Equals(
                exception.Code,
                "conflict.sysmon-settings-revision",
                StringComparison.Ordinal)
                ? _text("SysMonSettingsConflictStatus")
                : _text("SysMonSettingsSaveFailedStatus");
            return false;
        }
        catch (IOException)
        {
            StatusText = _text("SysMonSettingsSaveFailedStatus");
            return false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public static bool MoveUp(
        ObservableCollection<SystemMonitorMetricOption> options,
        SystemMonitorMetricOption option)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(option);
        int index = options.IndexOf(option);
        if (index <= 0)
        {
            return false;
        }

        options.Move(index, index - 1);
        return true;
    }

    public static bool MoveDown(
        ObservableCollection<SystemMonitorMetricOption> options,
        SystemMonitorMetricOption option)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(option);
        int index = options.IndexOf(option);
        if (index < 0 || index >= options.Count - 1)
        {
            return false;
        }

        options.Move(index, index + 1);
        return true;
    }

    /// <summary>
    /// Checks what the broker would reject anyway, so the user sees the reason next to the
    /// control rather than as a failed save.
    /// </summary>
    public bool Validate()
    {
        if (SelectedCardItems.Count > SystemMonitorContract.MaxItemsPerSurface ||
            SelectedEntryItems.Count > SystemMonitorContract.MaxItemsPerSurface)
        {
            ValidationText = _text("SysMonSettingsTooManyItems");
            return false;
        }

        ValidationText = string.Empty;
        return true;
    }

    private ObservableCollection<SystemMonitorMetricOption> BuildOptions(
        SystemMonitorSurface surface,
        IReadOnlyList<SystemMonitorItemDto> selected)
    {
        var options = new ObservableCollection<SystemMonitorMetricOption>();
        foreach (string metricId in SystemMonitorContract.MetricIds)
        {
            SystemMonitorItemDto? match = selected.FirstOrDefault(
                item => string.Equals(item.MetricId, metricId, StringComparison.Ordinal));
            options.Add(
                new SystemMonitorMetricOption(
                    metricId,
                    _text(SystemMonitorResourceKeys.GetNameKey(metricId)),
                    surface,
                    DetailOptions,
                    match is not null,
                    match?.Detail ?? SystemMonitorDetail.Normal));
        }

        Reorder(options, selected);
        return options;
    }

    private static void ApplyItems(
        ObservableCollection<SystemMonitorMetricOption> options,
        IReadOnlyList<SystemMonitorItemDto> items)
    {
        foreach (SystemMonitorMetricOption option in options)
        {
            SystemMonitorItemDto? match = items.FirstOrDefault(
                item => string.Equals(item.MetricId, option.MetricId, StringComparison.Ordinal));
            option.IsSelected = match is not null;
            option.Detail = match?.Detail ?? option.Detail;
        }

        Reorder(options, items);
    }

    /// <summary>
    /// Puts the selected metrics at the top in their saved order and leaves the rest below in
    /// the catalog's order, so the list always reads top-to-bottom as what is displayed.
    /// </summary>
    private static void Reorder(
        ObservableCollection<SystemMonitorMetricOption> options,
        IReadOnlyList<SystemMonitorItemDto> items)
    {
        int target = 0;
        foreach (SystemMonitorItemDto item in items)
        {
            SystemMonitorMetricOption? option = options.FirstOrDefault(
                candidate => string.Equals(
                    candidate.MetricId,
                    item.MetricId,
                    StringComparison.Ordinal));
            if (option is null)
            {
                continue;
            }

            int index = options.IndexOf(option);
            if (index != target)
            {
                options.Move(index, target);
            }

            target++;
        }
    }

    private static SystemMonitorItemDto[] Selected(
        ObservableCollection<SystemMonitorMetricOption> options) =>
        options
            .Where(option => option.IsSelected)
            .Select(option => option.ToItem())
            .ToArray();

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
