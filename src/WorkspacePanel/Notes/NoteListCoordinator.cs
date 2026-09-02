namespace WinWidgetBoard.WorkspacePanel.Notes;

/// <summary>
/// Coordinates note-list workflows shared by search, list, open, and delete refresh UI actions.
/// It keeps query-version checks next to the view models and leaves visual state to MainWindow.
/// </summary>
public sealed class NoteListCoordinator
{
    private readonly NoteEditorViewModel _editor;
    private readonly NoteSearchViewModel _search;

    public NoteListCoordinator(
        NoteEditorViewModel editor,
        NoteSearchViewModel search)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    public async Task<NoteListOperationResult> LoadAllAsync(
        CancellationToken cancellationToken)
    {
        bool completed = await _search
            .LoadAllAsync(cancellationToken)
            .ConfigureAwait(false);
        return CaptureResult(completed, string.Empty);
    }

    public async Task<NoteListOperationResult> SearchAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        string normalizedQuery = query?.Trim() ?? string.Empty;
        bool completed = await _search
            .SearchAsync(normalizedQuery, cancellationToken)
            .ConfigureAwait(false);
        return CaptureResult(completed, normalizedQuery);
    }

    public async Task<bool> LoadNoteAsync(
        string noteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        if (!await TryFlushDraftAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await _editor
            .LoadNoteAsync(noteId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CreateNoteAsync(CancellationToken cancellationToken)
    {
        if (!await TryFlushDraftAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await _editor
            .CreateNoteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteCurrentNoteAsync(CancellationToken cancellationToken)
    {
        if (!await TryFlushDraftAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await _editor
            .DeleteCurrentNoteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Saves what the user was typing a moment ago before the editor moves on, so "new",
    /// "open" and "delete" do not answer "save first" for a draft that would have saved
    /// itself half a second later. A draft whose save has already failed still blocks, and
    /// that one the user can see and retry.
    /// </summary>
    private async Task<bool> TryFlushDraftAsync(CancellationToken cancellationToken)
    {
        if (_editor.CanLoadNote)
        {
            return true;
        }

        if (!_editor.HasUnsavedChanges ||
            !await _editor.SaveNowAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return _editor.CanLoadNote;
    }

    public async Task<NoteListOperationResult> RefreshAfterDeleteAsync(
        CancellationToken cancellationToken)
    {
        string query = _search.Query;
        bool completed = query.Length == 0
            ? await _search.LoadAllAsync(cancellationToken).ConfigureAwait(false)
            : await _search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return CaptureResult(completed, query);
    }

    public async Task<bool> LoadFirstListedNoteAsync(
        CancellationToken cancellationToken)
    {
        NoteSearchResult? first = _search.Results.Count == 0
            ? null
            : _search.Results[0];
        if (first is null || !_editor.CanLoadNote)
        {
            return false;
        }

        return await _editor
            .LoadNoteAsync(first.NoteId, cancellationToken)
            .ConfigureAwait(false);
    }

    private NoteListOperationResult CaptureResult(
        bool completed,
        string expectedQuery)
    {
        return new NoteListOperationResult(
            completed,
            string.Equals(
                _search.Query,
                expectedQuery,
                StringComparison.Ordinal),
            _search.Status,
            _search.Results.Count);
    }
}

public sealed record NoteListOperationResult(
    bool Completed,
    bool QueryMatches,
    NoteSearchStatus Status,
    int ResultCount)
{
    public bool IsCurrentQuery => Completed && QueryMatches;

    public bool HasResults => ResultCount > 0;
}
