using System.ComponentModel;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardSurfaceItem : INotifyPropertyChanged, IDisposable
{
    private readonly CardLayoutEditViewModel _editMode;
    private readonly Func<NoteEditorStatus, string> _statusFormatter;
    private bool _disposed;

    public CardSurfaceItem(
        CardPlacement placement,
        NoteEditorViewModel noteEditor,
        CardLayoutEditViewModel editMode,
        Func<NoteEditorStatus, string>? statusFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(noteEditor);
        ArgumentNullException.ThrowIfNull(editMode);
        Placement = placement;
        NoteEditor = noteEditor;
        _editMode = editMode;
        _statusFormatter = statusFormatter ?? (status => status.ToString());
        NoteEditor.PropertyChanged += NoteEditor_PropertyChanged;
        _editMode.PropertyChanged += EditMode_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CardPlacement Placement { get; private set; }

    public string InstanceId => Placement.InstanceId;

    public NoteEditorViewModel NoteEditor { get; }

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
        NoteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        _editMode.PropertyChanged -= EditMode_PropertyChanged;
    }

    private void NoteEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteEditorViewModel.Status)
            or nameof(NoteEditorViewModel.ErrorCode))
        {
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
}
