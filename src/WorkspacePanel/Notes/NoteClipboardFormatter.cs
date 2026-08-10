namespace WinWidgetBoard.WorkspacePanel.Notes;

public static class NoteClipboardFormatter
{
    public static string Format(string? title, string? body)
    {
        title ??= string.Empty;
        body ??= string.Empty;

        if (title.Length == 0)
        {
            return body;
        }

        if (body.Length == 0)
        {
            return title;
        }

        return string.Concat(title, Environment.NewLine, body);
    }
}
