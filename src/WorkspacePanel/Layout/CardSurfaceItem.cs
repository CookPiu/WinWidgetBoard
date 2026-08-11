using System.ComponentModel;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardSurfaceItem : INotifyPropertyChanged, IDisposable
{
    private readonly CardLayoutEditViewModel _editMode;
    private readonly Func<NoteEditorStatus, string> _statusFormatter;
    private readonly CardRuntimeVisibilityScheduler? _visibilityScheduler;
    private readonly CardRuntimeRegistration? _visibilityRegistration;
    private bool _disposed;

    public CardSurfaceItem(
        CardPlacement placement,
        NoteEditorViewModel noteEditor,
        CardLayoutEditViewModel editMode,
        Func<NoteEditorStatus, string>? statusFormatter = null,
        CardRuntimeVisibilityScheduler? visibilityScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(noteEditor);
        ArgumentNullException.ThrowIfNull(editMode);
        Placement = placement;
        NoteEditor = noteEditor;
        _editMode = editMode;
        _statusFormatter = statusFormatter ?? (status => status.ToString());
        _visibilityScheduler = visibilityScheduler;
        Runtime = BuiltInCardRuntimeFactory.Create(
            placement.InstanceId,
            noteEditor);
        _visibilityRegistration = visibilityScheduler?.Register(
            Runtime,
            RefreshSnapshotAsync);
        NoteEditor.PropertyChanged += NoteEditor_PropertyChanged;
        _editMode.PropertyChanged += EditMode_PropertyChanged;
        Runtime.PropertyChanged += Runtime_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CardPlacement Placement { get; private set; }

    public string InstanceId => Placement.InstanceId;

    public NoteEditorViewModel NoteEditor { get; }

    public CardRuntimeInstance Runtime { get; }

    public string CardTypeId => Runtime.Definition.CardTypeId;

    public CardRuntimeSnapshot RuntimeSnapshot => Runtime.Snapshot;

    public CardRuntimeStatus RuntimeStatus => RuntimeSnapshot.Status;

    public bool SetViewportVisibility(bool isInViewport) =>
        _visibilityScheduler?.SetViewportVisibility(Runtime, isInViewport)
            ?? false;

    public bool IsEditing => _editMode.IsEditing;

    public string NoteStatusText => _statusFormatter(NoteEditor.Status);

    internal bool UpdatePlacement(CardPlacement placement)
    {
        if (!string.Equals(
                placement.InstanceId,
                InstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A surface item can only accept a placement for the same card.",
                nameof(placement));
        }

        if (Placement == placement)
        {
            return false;
        }

        Placement = placement;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(Placement)));
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visibilityRegistration?.Dispose();
        NoteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        _editMode.PropertyChanged -= EditMode_PropertyChanged;
        Runtime.PropertyChanged -= Runtime_PropertyChanged;
        Runtime.Dispose();
    }

    private ValueTask<CardRuntimeSnapshot> RefreshSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CardRuntimeSnapshot snapshot =
            BuiltInCardRuntimeFactory.CreateFreshSnapshot(
                Runtime,
                NoteEditor);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot);
    }

    private void NoteEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteEditorViewModel.Status)
            or nameof(NoteEditorViewModel.ErrorCode))
        {
            if (string.Equals(
                    CardTypeId,
                    BuiltInCardCatalog.NotesCardTypeId,
                    StringComparison.Ordinal) &&
                (_visibilityScheduler is null ||
                    Runtime.LifecycleState ==
                        CardLifecycleState.Visible))
            {
                BuiltInCardRuntimeFactory.SynchronizeNote(
                    Runtime,
                    NoteEditor);
            }

            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(NoteStatusText)));
        }
    }

    private void EditMode_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardLayoutEditViewModel.IsEditing))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    private void Runtime_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardRuntimeInstance.Snapshot))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RuntimeSnapshot)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RuntimeStatus)));
        }
    }
}
