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
    /// Updates from a newer row and raises only what moved. Returns false when the row is for a
    /// different metric, which is a caller mistake rather than a state to absorb.
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
/// One bar of the 24-hour strip. Identified by its position, because that is what a bar is: the
/// same slot keeps its visual and only changes height as the window slides.
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

public sealed class TokenUsageBreakdownViewModel : INotifyPropertyChanged
{
    private TokenUsageBreakdownRow _row;

    public TokenUsageBreakdownViewModel(TokenUsageBreakdownRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label => _row.Label;

    public string PrimaryText => _row.PrimaryText;

    public string SecondaryText => _row.SecondaryText;

    public double MeterPercent => _row.MeterPercent;

    public string AutomationName => _row.AutomationName;

    public bool Apply(TokenUsageBreakdownRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.Label, _row.Label, StringComparison.Ordinal))
        {
            return false;
        }

        TokenUsageBreakdownRow previous = _row;
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

public sealed class TokenUsageQuotaViewModel : INotifyPropertyChanged
{
    private TokenUsageQuotaRow _row;

    public TokenUsageQuotaViewModel(TokenUsageQuotaRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WindowId => _row.WindowId;

    public string WindowText => _row.WindowText;

    public string UsedText => _row.UsedText;

    public string ResetsLabel => _row.ResetsLabel;

    public bool IsResetVisible => _row.IsResetVisible;

    public double MeterPercent => _row.MeterPercent;

    public string AutomationName => _row.AutomationName;

    public bool Apply(TokenUsageQuotaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.WindowId, _row.WindowId, StringComparison.Ordinal))
        {
            return false;
        }

        TokenUsageQuotaRow previous = _row;
        _row = row;

        Raise(previous.WindowText, row.WindowText, nameof(WindowText));
        Raise(previous.UsedText, row.UsedText, nameof(UsedText));
        Raise(previous.ResetsLabel, row.ResetsLabel, nameof(ResetsLabel));
        Raise(previous.IsResetVisible, row.IsResetVisible, nameof(IsResetVisible));
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
/// One tab of the card's page switcher. Selection lives here rather than on the card so the
/// tab strip can bind a single collection and still show which page is current.
/// </summary>
public sealed class TokenUsagePageTabViewModel : INotifyPropertyChanged
{
    private string _name;
    private bool _isSelected;

    public TokenUsagePageTabViewModel(string pageId, string name, bool isSelected)
    {
        PageId = pageId;
        _name = name;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PageId { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (!string.Equals(_name, value, StringComparison.Ordinal))
            {
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
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
        IReadOnlyList<TokenUsageMetricRow> rows) =>
        Merge(
            target,
            rows,
            (item, row) => string.Equals(item.MetricId, row.MetricId, StringComparison.Ordinal),
            row => new TokenUsageMetricViewModel(row),
            (item, row) => item.Apply(row));

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

    public static void MergeBreakdown(
        ObservableCollection<TokenUsageBreakdownViewModel> target,
        IReadOnlyList<TokenUsageBreakdownRow> rows) =>
        Merge(
            target,
            rows,
            (item, row) => string.Equals(item.Label, row.Label, StringComparison.Ordinal),
            row => new TokenUsageBreakdownViewModel(row),
            (item, row) => item.Apply(row));

    public static void MergeQuota(
        ObservableCollection<TokenUsageQuotaViewModel> target,
        IReadOnlyList<TokenUsageQuotaRow> rows) =>
        Merge(
            target,
            rows,
            (item, row) => string.Equals(item.WindowId, row.WindowId, StringComparison.Ordinal),
            row => new TokenUsageQuotaViewModel(row),
            (item, row) => item.Apply(row));

    /// <summary>
    /// The page tabs. Membership changes only when a vendor is switched on or off, so this is
    /// normally a no-op that just keeps the selected flag in sync.
    /// </summary>
    public static void MergePages(
        ObservableCollection<TokenUsagePageTabViewModel> target,
        IReadOnlyList<TokenUsagePage> pages,
        string selectedPageId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pages);

        for (int index = 0; index < pages.Count; index++)
        {
            TokenUsagePage page = pages[index];
            int existing = IndexOf(
                target,
                item => string.Equals(item.PageId, page.PageId, StringComparison.Ordinal));
            if (existing < 0)
            {
                target.Insert(
                    index,
                    new TokenUsagePageTabViewModel(
                        page.PageId,
                        page.Name,
                        string.Equals(page.PageId, selectedPageId, StringComparison.Ordinal)));
                continue;
            }

            if (existing != index)
            {
                target.Move(existing, index);
            }

            target[index].Name = page.Name;
            target[index].IsSelected =
                string.Equals(page.PageId, selectedPageId, StringComparison.Ordinal);
        }

        Trim(target, pages.Count);
    }

    private static void Merge<TItem, TRow>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TRow> rows,
        Func<TItem, TRow, bool> matches,
        Func<TRow, TItem> create,
        Action<TItem, TRow> apply)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rows);

        for (int index = 0; index < rows.Count; index++)
        {
            TRow row = rows[index];
            int existing = IndexOf(target, item => matches(item, row));
            if (existing < 0)
            {
                target.Insert(index, create(row));
                continue;
            }

            if (existing != index)
            {
                target.Move(existing, index);
            }

            apply(target[index], row);
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
