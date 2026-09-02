namespace WinWidgetBoard.WorkspacePanel.Notes;

/// <summary>
/// Where the caret belongs after a text box's content was replaced under it by undo or redo.
/// Setting <c>Text</c> parks the caret at the start, which is as far as it can get from the
/// place the edit happened; the end of the changed region is where the user was working.
/// </summary>
public static class NoteCaretPlacement
{
    public static int AfterChange(string previous, string current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        int shorter = Math.Min(previous.Length, current.Length);
        int prefix = 0;
        while (prefix < shorter && previous[prefix] == current[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < shorter - prefix &&
            previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix])
        {
            suffix++;
        }

        return current.Length - suffix;
    }
}
