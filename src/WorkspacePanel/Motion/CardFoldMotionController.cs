namespace WinWidgetBoard.WorkspacePanel.Motion;

public readonly record struct CardFoldMotionValue(
    string InstanceId,
    double Progress);

/// <summary>
/// Drives any ordered card set while preserving progress and velocity by
/// stable instance identity across reorder and realization changes.
/// </summary>
public sealed class CardFoldMotionController
{
    private const double FirstOpen = 17.6;
    private const double LastOpen = 13.2;
    private const double FirstClose = 17.2;
    private const double LastClose = 21.6;
    private const double ValueEpsilon = 0.001;
    private const double VelocityEpsilon = 0.025;
    private readonly bool _reducedMotion;
    private List<Channel> _channels = [];
    private CardFoldMotionValue[] _values = [];
    private bool _targetOpen;

    public CardFoldMotionController(bool reducedMotion) =>
        _reducedMotion = reducedMotion;

    public IReadOnlyList<CardFoldMotionValue> Values => _values;
    public bool IsAnimating { get; private set; }
    public bool IsOpen => _targetOpen && !IsAnimating;

    public void Configure(IReadOnlyList<string> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var old = _channels.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<Channel>(orderedIds.Count);
        for (int index = 0; index < orderedIds.Count; index++)
        {
            string id = orderedIds[index];
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                throw new ArgumentException(
                    "Card IDs must be non-empty and unique.",
                    nameof(orderedIds));
            }

            double position = orderedIds.Count <= 1
                ? 0
                : index / (double)(orderedIds.Count - 1);
            Channel channel = old.TryGetValue(id, out Channel? existing)
                ? existing
                : new Channel(id, _reducedMotion || _targetOpen ? 1 : 0);
            channel.OpenFrequency = Lerp(FirstOpen, LastOpen, position);
            channel.CloseFrequency = Lerp(FirstClose, LastClose, position);
            next.Add(channel);
        }

        _channels = next;
        _values = new CardFoldMotionValue[next.Count];
        SyncValues();
        IsAnimating = !_reducedMotion && !AllAtTarget();
    }

    public void RequestOpen()
    {
        _targetOpen = true;
        Retarget();
    }

    public void RequestClose()
    {
        _targetOpen = false;
        if (_reducedMotion)
        {
            Snap(1);
            return;
        }
        Retarget();
    }

    public IReadOnlyList<CardFoldMotionValue> Step(TimeSpan elapsed)
    {
        if (!IsAnimating)
        {
            return Values;
        }

        double seconds = Math.Clamp(elapsed.TotalSeconds, 0.001, 0.05);
        double target = _targetOpen ? 1 : 0;
        foreach (Channel channel in _channels)
        {
            (channel.Value, channel.Velocity) = CriticallyDampedSpring.Advance(
                channel.Value,
                channel.Velocity,
                target,
                _targetOpen ? channel.OpenFrequency : channel.CloseFrequency,
                seconds);
        }

        if (AllAtTarget())
        {
            Snap(target);
        }
        else
        {
            SyncValues();
        }
        return Values;
    }

    private void Retarget()
    {
        if (_reducedMotion)
        {
            Snap(1);
            return;
        }
        IsAnimating = !AllAtTarget();
        SyncValues();
    }

    private bool AllAtTarget()
    {
        double target = _targetOpen ? 1 : 0;
        return _channels.All(channel =>
            Math.Abs(channel.Value - target) <= ValueEpsilon &&
            Math.Abs(channel.Velocity) <= VelocityEpsilon);
    }

    private void Snap(double target)
    {
        foreach (Channel channel in _channels)
        {
            channel.Value = target;
            channel.Velocity = 0;
        }
        IsAnimating = false;
        SyncValues();
    }

    private void SyncValues()
    {
        for (int index = 0; index < _channels.Count; index++)
        {
            Channel channel = _channels[index];
            _values[index] = new(channel.Id, channel.Value);
        }
    }

    private static double Lerp(double from, double to, double progress) =>
        from + (to - from) * progress;

    private sealed class Channel(string id, double value)
    {
        public string Id { get; } = id;
        public double Value { get; set; } = value;
        public double Velocity { get; set; }
        public double OpenFrequency { get; set; }
        public double CloseFrequency { get; set; }
    }
}
