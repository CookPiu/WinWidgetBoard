using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// Which surface a list of options configures. It is part of every automation ID on the row,
/// because the dialog shows the same twelve metrics twice and the UIA scripts have to be able
/// to tell the card's copy from the entry's.
/// </summary>
public enum SystemMonitorSurface
{
    Card,
    Entry,
}

public sealed record SystemMonitorDetailOption(
    SystemMonitorDetail Detail,
    string DisplayName);

/// <summary>
/// One selectable network source. The empty ID is the "all adapters" entry, which is where the
/// list always starts and what the readings meant before the source was configurable.
/// </summary>
public sealed record SystemMonitorNetworkOption(
    string Id,
    string DisplayName);

/// <summary>
/// One metric as the settings dialog shows it: included or not, at some level of detail, in
/// whatever position the list currently holds it. Display order is list order - there is no
/// separate index to keep in step with it.
/// </summary>
public sealed class SystemMonitorMetricOption : INotifyPropertyChanged
{
    private bool _isSelected;
    private SystemMonitorDetail _detail;

    public SystemMonitorMetricOption(
        string metricId,
        string displayName,
        SystemMonitorSurface surface,
        IReadOnlyList<SystemMonitorDetailOption> detailOptions,
        bool isSelected,
        SystemMonitorDetail detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        ArgumentNullException.ThrowIfNull(detailOptions);
        MetricId = metricId;
        DisplayName = displayName ?? metricId;
        Surface = surface;
        DetailOptions = detailOptions;
        _isSelected = isSelected;
        _detail = detail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MetricId { get; }

    public string DisplayName { get; }

    public SystemMonitorSurface Surface { get; }

    public IReadOnlyList<SystemMonitorDetailOption> DetailOptions { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public SystemMonitorDetail Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    /// <summary>
    /// Index of <see cref="Detail"/> inside <see cref="DetailOptions"/>, so a combo box can
    /// bind by selected index without the dialog needing a converter.
    /// </summary>
    public int DetailIndex
    {
        get
        {
            for (int index = 0; index < DetailOptions.Count; index++)
            {
                if (DetailOptions[index].Detail == Detail)
                {
                    return index;
                }
            }

            return 0;
        }

        set
        {
            if (value >= 0 && value < DetailOptions.Count)
            {
                Detail = DetailOptions[value].Detail;
            }
        }
    }

    public string SurfaceTag => Surface == SystemMonitorSurface.Card ? "Card" : "Entry";

    public string IncludeAutomationId => $"SysMon{SurfaceTag}Include_{MetricId}";

    public string DetailAutomationId => $"SysMon{SurfaceTag}Detail_{MetricId}";

    public SystemMonitorItemDto ToItem() =>
        new() { MetricId = MetricId, Detail = Detail };

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Detail))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailIndex)));
        }
    }
}
