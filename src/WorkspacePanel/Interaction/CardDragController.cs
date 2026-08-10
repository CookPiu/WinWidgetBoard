namespace WinWidgetBoard.WorkspacePanel.Interaction;

public enum CardDragState
{
    Idle,
    Pressed,
    Dragging,
}

public readonly record struct DragPoint(double X, double Y);

public readonly record struct DragOffset(double X, double Y)
{
    public static DragOffset Zero => new(0, 0);
}

public readonly record struct CardDragUpdate(
    CardDragState State,
    DragOffset Offset,
    bool Started,
    bool Completed,
    bool Clicked,
    bool Canceled);

public sealed class CardDragController
{
    public const double DragThreshold = 10;

    private CardDragState _state;
    private DragPoint _pressPoint;
    private DragOffset _startOffset;
    private DragOffset _offset;

    public CardDragState State => _state;

    public bool IsActive => _state != CardDragState.Idle;

    public DragOffset Offset => _offset;

    public void SetOffset(DragOffset offset)
    {
        if (IsActive)
        {
            throw new InvalidOperationException(
                "Card drag offset cannot be changed while a pointer gesture is active.");
        }

        _offset = offset;
        _startOffset = offset;
    }

    public CardDragUpdate Press(DragPoint point)
    {
        if (IsActive)
        {
            _state = CardDragState.Idle;
        }

        _state = CardDragState.Pressed;
        _pressPoint = point;
        _startOffset = _offset;
        return Current();
    }

    public CardDragUpdate Move(DragPoint point)
    {
        if (_state == CardDragState.Idle)
        {
            return Current();
        }

        double deltaX = point.X - _pressPoint.X;
        double deltaY = point.Y - _pressPoint.Y;
        if (_state == CardDragState.Pressed)
        {
            double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (distance < DragThreshold)
            {
                return Current();
            }

            _state = CardDragState.Dragging;
            _offset = new(
                _startOffset.X + deltaX,
                _startOffset.Y + deltaY);
            return Current(started: true);
        }

        _offset = new(
            _startOffset.X + deltaX,
            _startOffset.Y + deltaY);
        return Current();
    }

    public CardDragUpdate Release(DragPoint point)
    {
        if (_state == CardDragState.Pressed)
        {
            _state = CardDragState.Idle;
            return Current(clicked: true);
        }

        if (_state != CardDragState.Dragging)
        {
            return Current();
        }

        Move(point);
        _state = CardDragState.Idle;
        return Current(completed: true);
    }

    public CardDragUpdate Cancel()
    {
        if (!IsActive)
        {
            return Current();
        }

        _offset = _startOffset;
        _state = CardDragState.Idle;
        return Current(canceled: true);
    }

    private CardDragUpdate Current(
        bool started = false,
        bool completed = false,
        bool clicked = false,
        bool canceled = false) =>
        new(_state, _offset, started, completed, clicked, canceled);
}
