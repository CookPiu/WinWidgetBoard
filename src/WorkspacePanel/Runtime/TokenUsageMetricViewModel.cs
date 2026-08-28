using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// A bindable row of the token-usage card.
///
/// The projection produces immutable rows; this is what the UI binds to, and it is updated in
/// place. Replacing the item source on every tick would rebuild each row's visuals - visible
/// flicker on a card whose whole job is to sit there and change numbers - and it is the same
/// rule the card grid already follows: only a membership change may rebuild a source.
/// </summary>
public sealed class TokenUsageMetricViewModel : INotifyPropertyChanged
{
    private TokenUsageMetricRow _row;

    public TokenUsageMetricViewModel(TokenUsageMetricRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MetricId => _row.MetricId;

    public string Name => _row.Name;

    public string PrimaryText => _row.PrimaryText;

    public string SecondaryText => _row.SecondaryText;

    public string MetricStatusText => _row.MetricStatusText;

    public bool HasReading => _row.HasReading;

    public bool IsMeterVisible => _row.IsMeterVisible;

    public bool IsSecondaryVisible => _row.IsSecondaryVisible;

    public bool IsMetricStatusTextVisible => _row.IsMetricStatusTextVisible;

    /// <summary>0..100 for a ProgressBar, which has no fractional scale of its own.</summary>
    public double MeterPercent => _row.MeterFraction * 100d;

    public string AutomationName => _row.AutomationName;

    /// <summary>
    /// Updates from a newer row and raises only what moved. Returns false when the row is for
    /// a different metric, which is a caller mistake rather than a state to absorb.
    /// </summary>
    public bool Apply(TokenUsageMetricRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.MetricId, _row.MetricId, StringComparison.Ordinal))
        {
            return false;
        }

        TokenUsageMetricRow previous = _row;
        _row = row;

        Raise(previous.Name, row.Name, nameof(Name));
        Raise(previous.PrimaryText, row.PrimaryText, nameof(PrimaryText));
        Raise(previous.SecondaryText, row.SecondaryText, nameof(SecondaryText));
        Raise(previous.MetricStatusText, row.MetricStatusText, nameof(MetricStatusText));
        Raise(previous.HasReading, row.HasReading, nameof(HasReading));
        Raise(previous.IsMeterVisible, row.IsMeterVisible, nameof(IsMeterVisible));
        Raise(previous.IsSecondaryVisible, row.IsSecondaryVisible, nameof(IsSecondaryVisible));
        Raise(
            previous.IsMetricStatusTextVisible,
            row.IsMetricStatusTextVisible,
            nameof(IsMetricStatusTextVisible));
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
/// One bar of the 24-hour strip. Identified by its position, because that is what a bar is:
/// the same slot keeps its visual and only changes height as the window slides.
/// </summary>
public sealed class TokenUsageTrendBarViewModel : INotifyPropertyChanged
{
    private TokenUsageTrendBar _bar;

    public TokenUsageTrendBarViewModel(TokenUsageTrendBar bar)
    {
        _bar = bar ?? throw new ArgumentNullException(nameof(bar));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => _bar.Index;

    public double BarHeight => _bar.BarHeight;

    public void Apply(TokenUsageTrendBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        double previous = _bar.BarHeight;
        _bar = bar;
        if (!previous.Equals(bar.BarHeight))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarHeight)));
        }
    }
}

public sealed class TokenUsageModelViewModel : INotifyPropertyChanged
{
    private TokenUsageModelRow _row;

    public TokenUsageModelViewModel(TokenUsageModelRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Model => _row.Model;

    public string PrimaryText => _row.PrimaryText;

    public string SecondaryText => _row.SecondaryText;

    public double MeterPercent => _row.MeterPercent;

    public string AutomationName => _row.AutomationName;

    public bool Apply(TokenUsageModelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.Model, _row.Model, StringComparison.Ordinal))
        {
            return false;
        }

        TokenUsageModelRow previous = _row;
        _row = row;

        Raise(previous.PrimaryText, row.PrimaryText, nameof(PrimaryText));
        Raise(previous.SecondaryText, row.SecondaryText, nameof(SecondaryText));
        Raise(previous.MeterPercent, row.MeterPercent, nameof(MeterPercent));
        Raise(previous.AutomationName, row.AutomationName, nameof(AutomationName));
        return true;
    }

    private void Raise<T>(T previous, T current, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

/// <summary>
/// Brings the bound collections in line with a new projection while keeping the existing view
/// models wherever the same row is still present, so a refresh updates text rather than
/// rebuilding rows.
/// </summary>
public static class TokenUsageListMerger
{
    public static void MergeMetrics(
        ObservableCollection<TokenUsageMetricViewModel> target,
        IReadOnlyList<TokenUsageMetricRow> rows)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rows);

        for (int index = 0; index < rows.Count; index++)
        {
            TokenUsageMetricRow row = rows[index];
            int existing = IndexOf(target, item => string.Equals(
                item.MetricId,
                row.MetricId,
                StringComparison.Ordinal));
            if (existing < 0)
            {
                target.Insert(index, new TokenUsageMetricViewModel(row));
                continue;
            }

            if (existing != index)
            {
                target.Move(existing, index);
            }

            target[index].Apply(row);
        }

        Trim(target, rows.Count);
    }

    public static void MergeTrend(
        ObservableCollection<TokenUsageTrendBarViewModel> target,
        IReadOnlyList<TokenUsageTrendBar> bars)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(bars);

        for (int index = 0; index < bars.Count; index++)
        {
            if (index < target.Count)
            {
                target[index].Apply(bars[index]);
            }
            else
            {
                target.Add(new TokenUsageTrendBarViewModel(bars[index]));
            }
        }

        Trim(target, bars.Count);
    }

    public static void MergeModels(
        ObservableCollection<TokenUsageModelViewModel> target,
        IReadOnlyList<TokenUsageModelRow> rows)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rows);

        for (int index = 0; index < rows.Count; index++)
        {
            TokenUsageModelRow row = rows[index];
            int existing = IndexOf(target, item => string.Equals(
                item.Model,
                row.Model,
                StringComparison.Ordinal));
            if (existing < 0)
            {
                target.Insert(index, new TokenUsageModelViewModel(row));
                continue;
            }

            if (existing != index)
            {
                target.Move(existing, index);
            }

            target[index].Apply(row);
        }

        Trim(target, rows.Count);
    }

    private static int IndexOf<T>(ObservableCollection<T> target, Func<T, bool> predicate)
    {
        for (int index = 0; index < target.Count; index++)
        {
            if (predicate(target[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Trim<T>(ObservableCollection<T> target, int count)
    {
        while (target.Count > count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
