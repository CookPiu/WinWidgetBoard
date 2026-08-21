using System.Collections.ObjectModel;
using System.ComponentModel;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardLayoutSurfaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CardLayoutViewModel _layout;
    private readonly CardLayoutEditViewModel _editMode;
    private readonly NoteEditorViewModel _noteEditor;
    private readonly Func<NoteEditorStatus, string> _statusFormatter;
    private readonly Func<string, string?> _runtimeResourceResolver;
    private readonly Action<Action> _uiInvoker;
    private readonly CardRuntimeVisibilityScheduler _visibilityScheduler;
    private readonly ObservableCollection<CardSurfaceItem> _items = [];
    private bool _disposed;

    public CardLayoutSurfaceViewModel(
        CardLayoutEditViewModel editMode,
        NoteEditorViewModel noteEditor,
        Func<NoteEditorStatus, string>? statusFormatter = null,
        CardRuntimeVisibilityScheduler? visibilityScheduler = null,
        Func<string, string?>? runtimeResourceResolver = null,
        Action<Action>? uiInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(editMode);
        ArgumentNullException.ThrowIfNull(noteEditor);
        _editMode = editMode;
        _layout = editMode.Layout;
        _noteEditor = noteEditor;
        _statusFormatter = statusFormatter ?? (status => status.ToString());
        _runtimeResourceResolver = runtimeResourceResolver ??
            (static key => key);
        _uiInvoker = uiInvoker ?? (static action => action());
        _visibilityScheduler = visibilityScheduler ??
            new CardRuntimeVisibilityScheduler();
        _layout.PropertyChanged += Layout_PropertyChanged;
        SynchronizeItems();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<CardSurfaceItem> Items => _items;

    public CardRuntimeVisibilityScheduler VisibilityScheduler =>
        _visibilityScheduler;

    public bool SetPanelVisibility(bool panelVisible) =>
        _visibilityScheduler.SetPanelVisibility(panelVisible);

    public bool SetViewportVisibility(
        CardRuntimeInstance runtime,
        bool isInViewport) =>
        _visibilityScheduler.SetViewportVisibility(runtime, isInViewport);

    public bool ClearViewportVisibility() =>
        _visibilityScheduler.ClearViewportVisibility();

    public CardSurfaceItem? GetItemAt(int index) =>
        index >= 0 && index < _items.Count
            ? _items[index]
            : null;

    public bool ApplyDropPreview(
        string draggedInstanceId,
        IReadOnlyList<CardPlacement> projectedPlacements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draggedInstanceId);
        ArgumentNullException.ThrowIfNull(projectedPlacements);

        Dictionary<string, CardPlacement> projectedById =
            CreatePlacementMap(projectedPlacements, nameof(projectedPlacements));
        Dictionary<string, CardPlacement> committedById =
            CreatePlacementMap(_layout.Placements, nameof(_layout.Placements));
        if (committedById.Count != _items.Count ||
            _items.Any(item => !committedById.ContainsKey(item.InstanceId)))
        {
            throw new InvalidOperationException(
                "Surface items and committed placements are out of sync.");
        }

        if (projectedById.Count != _items.Count ||
            _items.Any(item => !projectedById.ContainsKey(item.InstanceId)) ||
            !projectedById.ContainsKey(draggedInstanceId))
        {
            throw new ArgumentException(
                "Preview placements must contain exactly one placement for each card.",
                nameof(projectedPlacements));
        }

        bool changed = false;
        foreach (CardSurfaceItem item in _items)
        {
            CardPlacement placement = string.Equals(
                    item.InstanceId,
                    draggedInstanceId,
                    StringComparison.Ordinal)
                ? committedById[item.InstanceId]
                : projectedById[item.InstanceId];
            changed |= item.UpdatePlacement(placement);
        }

        return changed;
    }

    public bool ResetDropPreview()
    {
        Dictionary<string, CardPlacement> committedById =
            CreatePlacementMap(_layout.Placements, nameof(_layout.Placements));
        if (committedById.Count != _items.Count ||
            _items.Any(item => !committedById.ContainsKey(item.InstanceId)))
        {
            throw new InvalidOperationException(
                "Surface items and committed placements are out of sync.");
        }

        bool changed = false;
        foreach (CardSurfaceItem item in _items)
        {
            changed |= item.UpdatePlacement(committedById[item.InstanceId]);
        }

        return changed;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _layout.PropertyChanged -= Layout_PropertyChanged;
        DisposeItems(_items);
        _items.Clear();
        await _visibilityScheduler.DisposeAsync().ConfigureAwait(false);
    }

    private void Layout_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName == nameof(CardLayoutViewModel.Placements))
        {
            SynchronizeItems();
        }
    }

    private void SynchronizeItems()
    {
        IReadOnlyList<CardPlacement> placements = _layout.Placements;
        HashSet<string> desiredIds = placements
            .Select(placement => placement.InstanceId)
            .ToHashSet(StringComparer.Ordinal);
        bool membershipChanged = false;

        for (int index = _items.Count - 1; index >= 0; index--)
        {
            CardSurfaceItem item = _items[index];
            if (desiredIds.Contains(item.InstanceId))
            {
                continue;
            }

            _items.RemoveAt(index);
            item.Dispose();
            membershipChanged = true;
        }

        for (int desiredIndex = 0;
            desiredIndex < placements.Count;
            desiredIndex++)
        {
            CardPlacement placement = placements[desiredIndex];
            int currentIndex = IndexOf(placement.InstanceId);
            CardSurfaceItem item;
            if (currentIndex < 0)
            {
                item = new CardSurfaceItem(
                    placement,
                    _noteEditor,
                    _editMode,
                    _statusFormatter,
                    _visibilityScheduler,
                    _runtimeResourceResolver,
                    _uiInvoker);
                _items.Insert(Math.Min(desiredIndex, _items.Count), item);
                membershipChanged = true;
            }
            else
            {
                item = _items[currentIndex];
                item.UpdatePlacement(placement);
            }
        }

        if (membershipChanged)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Items)));
        }
    }

    private int IndexOf(string instanceId)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (string.Equals(
                    _items[index].InstanceId,
                    instanceId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static Dictionary<string, CardPlacement> CreatePlacementMap(
        IReadOnlyList<CardPlacement> placements,
        string parameterName)
    {
        var placementsById = new Dictionary<string, CardPlacement>(
            StringComparer.Ordinal);
        foreach (CardPlacement placement in placements)
        {
            if (!placementsById.TryAdd(placement.InstanceId, placement))
            {
                throw new ArgumentException(
                    $"Duplicate placement instance ID '{placement.InstanceId}'.",
                    parameterName);
            }
        }

        return placementsById;
    }

    private static void DisposeItems(IEnumerable<CardSurfaceItem> items)
    {
        foreach (CardSurfaceItem item in items)
        {
            item.Dispose();
        }
    }
}
