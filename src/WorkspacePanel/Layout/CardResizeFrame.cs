using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// Whether the pointer is on the card's resize handle. There is one handle and it stretches
/// both axes at once, so this says "on it" or "not on it" rather than naming a direction.
/// </summary>
public enum CardResizeDirection
{
    None,
    SouthEast,
}

/// <summary>
/// The edit-mode frame drawn over a card.
///
/// It exists as a type rather than a plain <see cref="ContentControl"/> for one reason: the
/// pointer has to say what it will do before it is pressed, and the cursor is the only place
/// to say it. <c>ProtectedCursor</c> is protected on UIElement, so only a subclass can set it.
/// Everything else about the frame - what it looks like, which handles exist - stays in the
/// style, and the gesture itself stays with the page that owns the layout.
/// </summary>
public sealed partial class CardResizeFrame : ContentControl
{
    // Created once. A cursor object per pointer move would allocate on every mouse message
    // along a drag, which is the one place in this UI that must add no work.
    private static readonly InputCursor SouthEastCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);

    private CardResizeDirection _cursorDirection = CardResizeDirection.None;

    /// <summary>
    /// Points the cursor along the axis this handle stretches. Repeats are cheap: the frame
    /// only touches the property when the direction actually changes, because assigning
    /// ProtectedCursor re-evaluates the cursor for the whole element.
    /// </summary>
    public void ShowResizeCursor(CardResizeDirection direction)
    {
        if (_cursorDirection == direction)
        {
            return;
        }

        _cursorDirection = direction;
        ProtectedCursor = direction == CardResizeDirection.SouthEast
            ? SouthEastCursor
            : null;
    }
}
