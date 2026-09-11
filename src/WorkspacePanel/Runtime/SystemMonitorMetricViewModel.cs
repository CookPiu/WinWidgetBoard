using System.Collections.ObjectModel;
using System.ComponentModel;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// A bindable row of the hardware monitor card.
///
/// The projection produces immutable rows; this is what the UI actually binds to, and it is
/// updated in place. Replacing the item source every two seconds would rebuild every row's
/// visuals on each tick - visible flicker on a card whose whole job is to sit there and change
/// numbers. It is the same rule the card grid already follows: only a membership change may
/// rebuild the source.
/// </summary>
public sealed class SystemMonitorMetricViewModel : INotifyPropertyChanged
{
    private SystemMonitorMetricRow _row;
    private bool _isWithinCardLimit = true;

    public SystemMonitorMetricViewModel(SystemMonitorMetricRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Whether the card is tall enough to reach this reading. The list is priority-ordered by
    /// the broker, so the ones held back are the ones the user ranked last; a card clips what
    /// does not fit rather than scrolling it, and a row that is half-drawn at the card's edge
    /// is worse than one that is honestly absent.
    /// </summary>
    public bool IsWithinCardLimit
    {
        get => _isWithinCardLimit;
        internal set
        {
            if (_isWithinCardLimit == value)
            {
                return;
            }

            _isWithinCardLimit = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsWithinCardLimit)));
        }
    }

    public string MetricId => _row.MetricId;

    public string IconId => _row.IconId;

    public string Name => _row.Name;

    public string PrimaryText => _row.PrimaryText;

    public string SecondaryText => _row.SecondaryText;

    public string MetricStatusText => _row.MetricStatusText;

    public bool HasReading => _row.HasReading;

    public bool IsMeterVisible => _row.IsMeterVisible;

    public bool IsCurveVisible => _row.IsCurveVisible;

    /// <summary>
    /// The window drawn behind this reading. A fresh list arrives on every tick, so this
    /// raises on every tick - which is the point: the curve is the one part of the row that
    /// has to be redrawn to stay true, while the text around it usually has not moved.
    /// </summary>
    public IReadOnlyList<SystemMonitorCurvePoint> CurvePoints => _row.CurvePoints;

    public bool IsSecondaryVisible => _row.IsSecondaryVisible;

    public bool IsMetricStatusTextVisible => _row.IsMetricStatusTextVisible;

    /// <summary>0..100 for a ProgressBar, which has no fractional scale of its own.</summary>
    public double MeterPercent => _row.MeterFraction * 100d;

    public string AutomationName => _row.AutomationName;

    /// <summary>
    /// Updates from a newer row and raises only what actually moved. Returns false when the
    /// row is for a different metric, which is a caller mistake rather than a state to absorb.
    /// </summary>
    public bool Apply(SystemMonitorMetricRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.MetricId, _row.MetricId, StringComparison.Ordinal))
        {
            return false;
        }

        SystemMonitorMetricRow previous = _row;
        _row = row;

        Raise(previous.Name, row.Name, nameof(Name));
        Raise(previous.IconId, row.IconId, nameof(IconId));
        Raise(previous.PrimaryText, row.PrimaryText, nameof(PrimaryText));
        Raise(previous.SecondaryText, row.SecondaryText, nameof(SecondaryText));
        Raise(previous.MetricStatusText, row.MetricStatusText, nameof(MetricStatusText));
        Raise(previous.HasReading, row.HasReading, nameof(HasReading));
        Raise(previous.IsMeterVisible, row.IsMeterVisible, nameof(IsMeterVisible));
        Raise(previous.IsCurveVisible, row.IsCurveVisible, nameof(IsCurveVisible));
        Raise(previous.CurvePoints, row.CurvePoints, nameof(CurvePoints));
        Raise(previous.IsSecondaryVisible, row.IsSecondaryVisible, nameof(IsSecondaryVisible));
        Raise(previous.IsMetricStatusTextVisible, row.IsMetricStatusTextVisible, nameof(IsMetricStatusTextVisible));
        Raise(previous.MeterFraction, row.MeterFraction, nameof(MeterPercent));
        Raise(previous.AutomationName, row.AutomationName, nameof(AutomationName));
        return true;
    }

    public override string ToString() => MetricId + " " + PrimaryText;

    private void Raise<T>(T previous, T current, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

/// <summary>
/// Two readings side by side - one row of the card's two-column grid, or one reading and an
/// empty half where the list runs out.
///
/// The pairing is a view model rather than a layout that wraps on its own because the two
/// halves of a row have to share a height: a reading with a second line of detail is 18 DIP
/// taller than one without, and a grid that sizes every cell alike would clip it. An
/// ItemsControl over pairs gives each row an Auto height that the taller half decides, and it
/// is also the unit the height budget is spent in.
///
/// Pairs are immutable. They are rebuilt when the membership or the order changes - a
/// configuration change - and never on a tick, because the reading view models inside them are
/// the same instances being updated in place.
/// </summary>
public sealed class SystemMonitorMetricPairViewModel
{
    public SystemMonitorMetricPairViewModel(
        SystemMonitorMetricViewModel left,
        SystemMonitorMetricViewModel? right)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right;
    }

    public SystemMonitorMetricViewModel Left { get; }

    /// <summary>Null on the last row of an odd-length list.</summary>
    public SystemMonitorMetricViewModel? Right { get; }

    public override string ToString() =>
        Right is null ? Left.ToString() : Left + " | " + Right;
}

public static class SystemMonitorMetricPairListMerger
{
    /// <summary>How many readings sit on one row of the card.</summary>
    public const int Columns = 2;

    /// <summary>
    /// Pairs up <paramref name="metrics"/> in order, replacing only the rows whose halves
    /// actually changed. Reading left to right then down keeps the user's ranking legible:
    /// filling one column top to bottom first would put their second choice halfway down the
    /// card.
    /// </summary>
    public static void Merge(
        ObservableCollection<SystemMonitorMetricPairViewModel> target,
        IReadOnlyList<SystemMonitorMetricViewModel> metrics)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(metrics);

        int rows = (metrics.Count + Columns - 1) / Columns;
        for (int index = 0; index < rows; index++)
        {
            SystemMonitorMetricViewModel left = metrics[index * Columns];
            SystemMonitorMetricViewModel? right = (index * Columns) + 1 < metrics.Count
                ? metrics[(index * Columns) + 1]
                : null;
            if (index < target.Count)
            {
                SystemMonitorMetricPairViewModel existing = target[index];
                if (ReferenceEquals(existing.Left, left) &&
                    ReferenceEquals(existing.Right, right))
                {
                    continue;
                }

                target[index] = new SystemMonitorMetricPairViewModel(left, right);
                continue;
            }

            target.Add(new SystemMonitorMetricPairViewModel(left, right));
        }

        while (target.Count > rows)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

public static class SystemMonitorMetricListMerger
{
    /// <summary>
    /// Brings <paramref name="target"/> in line with <paramref name="rows"/> while keeping the
    /// existing view models wherever the same metric is still present, so a two-second refresh
    /// updates text instead of rebuilding rows.
    /// </summary>
    public static void Merge(
        ObservableCollection<SystemMonitorMetricViewModel> target,
        IReadOnlyList<SystemMonitorMetricRow> rows)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rows);

        for (int index = 0; index < rows.Count; index++)
        {
            SystemMonitorMetricRow row = rows[index];
            int existing = IndexOfMetric(target, row.MetricId);
            if (existing < 0)
            {
                target.Insert(index, new SystemMonitorMetricViewModel(row));
                continue;
            }

            if (existing != index)
            {
                target.Move(existing, index);
            }

            target[index].Apply(row);
        }

        while (target.Count > rows.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOfMetric(
        ObservableCollection<SystemMonitorMetricViewModel> target,
        string metricId)
    {
        for (int index = 0; index < target.Count; index++)
        {
            if (string.Equals(target[index].MetricId, metricId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
