using System.ComponentModel;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardLayoutEditViewModel : INotifyPropertyChanged
{
    private readonly CardLayoutViewModel _layout;
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

    public void BeginEdit()
    {
        if (_isEditing)
        {
            return;
        }

        _editSnapshot = _layout.GetItemsInPlacementOrder().ToArray();
        SetEditing(true);
    }

    public void CommitEdit()
    {
        if (!_isEditing)
        {
            return;
        }

        _editSnapshot = null;
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
        SetEditing(false);
        _layout.ReplaceItems(snapshot);
    }

    public bool TryResizeCard(string instanceId, CardSize size)
    {
        return _isEditing && _layout.ResizeCard(instanceId, size);
    }

    public bool TryRemoveCard(string instanceId)
    {
        return _isEditing && _layout.RemoveCard(instanceId);
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

        _layout.ApplyPlacements(placements);
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
    }
}
