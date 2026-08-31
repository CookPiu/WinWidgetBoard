using System.ComponentModel;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardLayoutEditViewModel : INotifyPropertyChanged
{
    public const int MaxHistoryDepth = 20;

    private readonly CardLayoutViewModel _layout;
    private readonly List<LayoutSnapshot> _undoHistory = [];
    private readonly List<LayoutSnapshot> _redoHistory = [];
    private IReadOnlyList<CardLayoutItem>? _editSnapshot;
    private bool _isEditing;

    public CardLayoutEditViewModel(CardLayoutViewModel layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CardLayoutViewModel Layout => _layout;

    public bool IsEditing => _isEditing;

    public bool CanUndo => _isEditing && _undoHistory.Count > 0;

    public bool CanRedo => _isEditing && _redoHistory.Count > 0;

    public void BeginEdit()
    {
        if (_isEditing)
        {
            return;
        }

        _editSnapshot = CaptureSnapshot().Items;
        ClearHistory();
        SetEditing(true);
    }

    public void CommitEdit()
    {
        if (!_isEditing)
        {
            return;
        }

        _editSnapshot = null;
        ClearHistory();
        SetEditing(false);
    }

    public void CancelEdit()
    {
        if (!_isEditing)
        {
            return;
        }

        IReadOnlyList<CardLayoutItem> snapshot = _editSnapshot ?? [];
        _editSnapshot = null;
        ClearHistory();
        SetEditing(false);
        _layout.ReplaceItems(snapshot);
    }

    public bool TryUndo()
    {
        if (!CanUndo)
        {
            return false;
        }

        int lastIndex = _undoHistory.Count - 1;
        LayoutSnapshot target = _undoHistory[lastIndex];
        _undoHistory.RemoveAt(lastIndex);
        _redoHistory.Add(CaptureSnapshot());
        RestoreSnapshot(target);
        PublishHistoryState();
        return true;
    }

    public bool TryRedo()
    {
        if (!CanRedo)
        {
            return false;
        }

        int lastIndex = _redoHistory.Count - 1;
        LayoutSnapshot target = _redoHistory[lastIndex];
        _redoHistory.RemoveAt(lastIndex);
        _undoHistory.Add(CaptureSnapshot());
        RestoreSnapshot(target);
        PublishHistoryState();
        return true;
    }

    public bool TryResizeCard(string instanceId, CardSize size)
    {
        if (!_isEditing)
        {
            return false;
        }

        LayoutSnapshot before = CaptureSnapshot();
        if (!_layout.ResizeCard(instanceId, size))
        {
            return false;
        }

        RecordMutation(before);
        return true;
    }

    public bool TryStepCardSize(
        string instanceId,
        int direction,
        IReadOnlyList<CardSize> orderedSizes)
    {
        if (!_isEditing)
        {
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(orderedSizes);
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Card size direction must be -1 or 1.");
        }

        CardLayoutItem? current = _layout.Items.FirstOrDefault(
            item => string.Equals(
                item.InstanceId,
                instanceId,
                StringComparison.Ordinal));
        if (current is null)
        {
            return false;
        }

        int currentIndex = -1;
        for (int index = 0; index < orderedSizes.Count; index++)
        {
            if (orderedSizes[index] == current.Size)
            {
                currentIndex = index;
                break;
            }
        }

        int nextIndex = currentIndex + direction;
        return currentIndex >= 0 &&
            nextIndex >= 0 &&
            nextIndex < orderedSizes.Count &&
            TryResizeCard(instanceId, orderedSizes[nextIndex]);
    }

    /// <summary>
    /// Puts a card back on the board at the end of the current order. Built-in instances are
    /// singletons - the same identity is what the broker subscription, the layout record and
    /// the note editor all key on - so adding one that is already placed is refused rather
    /// than duplicated.
    /// </summary>
    public bool TryAddCard(string instanceId, CardSize size)
    {
        if (!_isEditing)
        {
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (!Enum.IsDefined(size))
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                "Card size must be a defined card size.");
        }

        if (_layout.Items.Any(item => string.Equals(
                item.InstanceId,
                instanceId,
                StringComparison.Ordinal)))
        {
            return false;
        }

        LayoutSnapshot before = CaptureSnapshot();
        _layout.AddCard(new CardLayoutItem(instanceId, size));
        RecordMutation(before);
        return true;
    }

    public bool TryRemoveCard(string instanceId)
    {
        if (!_isEditing)
        {
            return false;
        }

        LayoutSnapshot before = CaptureSnapshot();
        if (!_layout.RemoveCard(instanceId))
        {
            return false;
        }

        RecordMutation(before);
        return true;
    }

    /// <summary>
    /// What the board would look like at a candidate size, without changing anything. A
    /// resize drag crosses several sizes on its way, and applying each one would push a
    /// history entry per threshold: the user would then need three undos to take back one
    /// gesture. The drag previews, and only the release commits.
    /// </summary>
    public bool TryPreviewResize(
        string instanceId,
        CardSize size,
        out IReadOnlyList<CardPlacement> placements)
    {
        placements = [];
        if (!_isEditing)
        {
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var items = new List<CardLayoutItem>(_layout.Items.Count);
        bool found = false;
        foreach (CardLayoutItem item in _layout.Items)
        {
            if (string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal))
            {
                found = true;
                items.Add(item with { Size = size });
            }
            else
            {
                items.Add(item);
            }
        }

        if (!found)
        {
            return false;
        }

        placements = ResponsiveGridLayout.Pack(_layout.ColumnCount, items);
        return true;
    }

    public bool TryPreviewDrop(
        string instanceId,
        GridCell requestedCell,
        out IReadOnlyList<CardPlacement> placements)
    {
        if (!_isEditing)
        {
            placements = Array.Empty<CardPlacement>();
            return false;
        }

        return CardDragPlacementProjector.TryProject(
            _layout.ColumnCount,
            _layout.Items,
            _layout.Placements,
            instanceId,
            requestedCell,
            out placements);
    }

    public bool TryCommitDrop(
        string instanceId,
        GridCell requestedCell,
        out IReadOnlyList<CardPlacement> placements)
    {
        if (!_isEditing ||
            !CardDragPlacementProjector.TryProject(
                _layout.ColumnCount,
                _layout.Items,
                _layout.Placements,
                instanceId,
                requestedCell,
                out placements))
        {
            placements = Array.Empty<CardPlacement>();
            return false;
        }

        if (PlacementsEqual(_layout.Placements, placements))
        {
            return true;
        }

        LayoutSnapshot before = CaptureSnapshot();
        _layout.ApplyPlacements(placements);
        RecordMutation(before);
        return true;
    }

    private void SetEditing(bool isEditing)
    {
        if (_isEditing == isEditing)
        {
            return;
        }

        _isEditing = isEditing;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsEditing)));
        PublishHistoryState();
    }

    private LayoutSnapshot CaptureSnapshot()
    {
        return new LayoutSnapshot(
            _layout.GetItemsInPlacementOrder()
                .Select(item => item with { })
                .ToArray());
    }

    private void RestoreSnapshot(LayoutSnapshot snapshot)
    {
        _layout.ReplaceItems(snapshot.Items);
    }

    private void RecordMutation(LayoutSnapshot before)
    {
        _undoHistory.Add(before);
        if (_undoHistory.Count > MaxHistoryDepth)
        {
            _undoHistory.RemoveAt(0);
        }

        _redoHistory.Clear();
        PublishHistoryState();
    }

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        PublishHistoryState();
    }

    private void PublishHistoryState()
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(CanUndo)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(CanRedo)));
    }

    private static bool PlacementsEqual(
        IReadOnlyList<CardPlacement> left,
        IReadOnlyList<CardPlacement> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        Dictionary<string, CardPlacement> leftById = left.ToDictionary(
            placement => placement.InstanceId,
            StringComparer.Ordinal);
        foreach (CardPlacement placement in right)
        {
            if (!leftById.TryGetValue(placement.InstanceId, out CardPlacement current) ||
                current != placement)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record LayoutSnapshot(IReadOnlyList<CardLayoutItem> Items);
}
