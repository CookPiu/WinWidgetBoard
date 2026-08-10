using System.ComponentModel;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardLayoutViewModel : INotifyPropertyChanged
{
    private readonly List<CardLayoutItem> _mutableItems = [];
    private IReadOnlyList<CardLayoutItem> _items = Array.Empty<CardLayoutItem>();
    private IReadOnlyList<CardPlacement> _placements = Array.Empty<CardPlacement>();
    private int _columnCount;

    public CardLayoutViewModel(
        int columnCount,
        IEnumerable<CardLayoutItem>? items = null)
    {
        ValidateColumnCount(columnCount);
        _columnCount = columnCount;
        if (items is not null)
        {
            ReplaceItems(items);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ColumnCount => _columnCount;

    public IReadOnlyList<CardLayoutItem> Items => _items;

    public IReadOnlyList<CardPlacement> Placements => _placements;

    public bool IsEmpty => _mutableItems.Count == 0;

    public IReadOnlyList<CardLayoutItem> GetItemsInPlacementOrder()
    {
        if (_mutableItems.Count == 0)
        {
            return Array.Empty<CardLayoutItem>();
        }

        Dictionary<string, CardLayoutItem> itemsById = _mutableItems.ToDictionary(
            item => item.InstanceId,
            StringComparer.Ordinal);
        return _placements
            .OrderBy(placement => placement.Row)
            .ThenBy(placement => placement.Column)
            .Select(placement => itemsById[placement.InstanceId])
            .ToArray();
    }

    public void SetEffectiveWidth(double effectiveWidth)
    {
        int columnCount = ResponsiveGridLayout.SelectColumnCount(effectiveWidth);
        SetColumnCount(columnCount);
    }

    public void SetColumnCount(int columnCount)
    {
        ValidateColumnCount(columnCount);
        if (_columnCount == columnCount)
        {
            return;
        }

        IReadOnlyList<CardPlacement> placements = ResponsiveGridLayout.Pack(
            columnCount,
            GetItemsInPlacementOrder());
        _columnCount = columnCount;
        SetItemsAndPlacements(placements);
        Publish(nameof(ColumnCount), nameof(Items), nameof(Placements));
    }

    public void ReplaceItems(IEnumerable<CardLayoutItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<CardLayoutItem> nextItems = items.ToList();
        IReadOnlyList<CardPlacement> nextPlacements = ResponsiveGridLayout.Pack(
            _columnCount,
            nextItems);

        SetItemsAndPlacements(
            nextPlacements,
            nextItems,
            normalizePreferences: false);
        Publish(nameof(Items), nameof(Placements), nameof(IsEmpty));
    }

    public void AddCard(CardLayoutItem item)
    {
        var nextItems = new List<CardLayoutItem>(GetItemsInPlacementOrder())
        {
            item,
        };
        ReplaceItems(nextItems);
    }

    public bool RemoveCard(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var nextItems = new List<CardLayoutItem>(GetItemsInPlacementOrder());
        int index = nextItems.FindIndex(
            item => string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        nextItems.RemoveAt(index);
        ReplaceItems(nextItems);
        return true;
    }

    public bool ResizeCard(string instanceId, CardSize size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var nextItems = new List<CardLayoutItem>(GetItemsInPlacementOrder());
        int index = nextItems.FindIndex(
            item => string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        CardLayoutItem current = nextItems[index];
        if (current.Size == size)
        {
            return false;
        }

        nextItems[index] = current with { Size = size };
        ReplaceItems(nextItems);
        return true;
    }

    public void ApplyPlacements(IReadOnlyList<CardPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count != _mutableItems.Count)
        {
            throw new ArgumentException(
                "Placement count must match the current card count.",
                nameof(placements));
        }

        Dictionary<string, CardPlacement> placementsById = new(
            StringComparer.Ordinal);
        foreach (CardPlacement placement in placements)
        {
            if (!placementsById.TryAdd(placement.InstanceId, placement))
            {
                throw new ArgumentException(
                    $"Duplicate placement instance ID '{placement.InstanceId}'.",
                    nameof(placements));
            }
        }

        foreach (CardLayoutItem item in _mutableItems)
        {
            if (!placementsById.TryGetValue(item.InstanceId, out CardPlacement placement))
            {
                throw new ArgumentException(
                    $"Missing placement for card '{item.InstanceId}'.",
                    nameof(placements));
            }

            CardSpan expectedSpan = ResponsiveGridLayout.GetSpan(item.Size);
            int expectedColumnSpan = Math.Min(expectedSpan.Columns, _columnCount);
            if (placement.Size != item.Size ||
                placement.Column < 0 ||
                placement.Row < 0 ||
                placement.ColumnSpan != expectedColumnSpan ||
                placement.RowSpan != expectedSpan.Rows ||
                placement.RightExclusive > _columnCount)
            {
                throw new ArgumentException(
                    $"Invalid placement for card '{item.InstanceId}'.",
                    nameof(placements));
            }
        }

        for (int index = 0; index < placements.Count; index++)
        {
            for (int otherIndex = index + 1;
                otherIndex < placements.Count;
                otherIndex++)
            {
                if (Overlaps(placements[index], placements[otherIndex]))
                {
                    throw new ArgumentException(
                        "Placements must not overlap.",
                        nameof(placements));
                }
            }
        }

        SetItemsAndPlacements(placements);
        Publish(nameof(Items), nameof(Placements));
    }

    public bool TryGetPlacement(
        string instanceId,
        out CardPlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        foreach (CardPlacement candidate in _placements)
        {
            if (string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal))
            {
                placement = candidate;
                return true;
            }
        }

        placement = default;
        return false;
    }

    private void Publish(params string[] propertyNames)
    {
        PropertyChangedEventHandler? handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        foreach (string propertyName in propertyNames.Distinct(StringComparer.Ordinal))
        {
            handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private void SetItemsAndPlacements(
        IReadOnlyList<CardPlacement> placements,
        IReadOnlyList<CardLayoutItem>? sourceItems = null,
        bool normalizePreferences = true)
    {
        IReadOnlyList<CardLayoutItem> items = sourceItems ?? _mutableItems;
        List<CardLayoutItem> normalizedItems;
        if (!normalizePreferences)
        {
            normalizedItems = items.ToList();
        }
        else
        {
            Dictionary<string, CardPlacement> placementsById = placements.ToDictionary(
                placement => placement.InstanceId,
                StringComparer.Ordinal);
            normalizedItems = items
                .Select(item => item with
                {
                    PreferredColumn = placementsById[item.InstanceId].Column,
                    PreferredRow = placementsById[item.InstanceId].Row,
                })
                .ToList();
        }

        _mutableItems.Clear();
        _mutableItems.AddRange(normalizedItems);
        _items = normalizedItems.AsReadOnly();
        _placements = Array.AsReadOnly(placements.ToArray());
    }

    private static void ValidateColumnCount(int columnCount)
    {
        _ = ResponsiveGridLayout.Pack(columnCount, Array.Empty<CardLayoutItem>());
    }

    private static bool Overlaps(
        CardPlacement left,
        CardPlacement right)
    {
        return left.Column < right.RightExclusive &&
            right.Column < left.RightExclusive &&
            left.Row < right.BottomExclusive &&
            right.Row < left.BottomExclusive;
    }
}
