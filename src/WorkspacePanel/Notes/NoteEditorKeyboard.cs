using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace WinWidgetBoard.WorkspacePanel.Notes;

/// <summary>
/// Routes Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z from a note text box to the editor's own history.
/// The box has an undo stack of its own, and the two cannot coexist: the box's undo comes
/// back through <c>TextChanged</c> as a fresh edit, which pushed a step and wiped redo. The
/// keys are claimed even when there is nothing to undo, so the box's stack never gets a turn.
/// </summary>
internal static class NoteEditorKeyboard
{
    public static bool TryHandle(
        NoteEditorViewModel editor,
        TextBox textBox,
        KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(textBox);
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key is not (VirtualKey.Z or VirtualKey.Y) ||
            !IsDown(VirtualKey.Control) ||
            IsDown(VirtualKey.Menu))
        {
            return false;
        }

        bool redo = e.Key == VirtualKey.Y || IsDown(VirtualKey.Shift);
        string previous = textBox.Text;
        bool applied = redo ? editor.Redo() : editor.Undo();
        // The binding has already written the restored text back by the time the call
        // returns; only the caret is left where the setter dropped it. A box whose text did
        // not change - the step belonged to the other field - keeps its caret.
        if (applied && !string.Equals(previous, textBox.Text, StringComparison.Ordinal))
        {
            int caret = NoteCaretPlacement.AfterChange(previous, textBox.Text);
            textBox.Select(Math.Min(caret, textBox.Text.Length), 0);
        }

        e.Handled = true;
        return true;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);
}
