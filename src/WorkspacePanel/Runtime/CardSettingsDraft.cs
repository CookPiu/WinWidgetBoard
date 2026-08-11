using System.Collections.ObjectModel;
using System.Text.Json;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Describes whether changing a setting may be sent to a live preview sink.
/// </summary>
public enum CardSettingWritePolicy
{
    PreviewSafe = 0,
    CommitOnly = 1,
}

/// <summary>
/// The lifecycle of a settings editing session.
/// </summary>
public enum CardSettingsDraftState
{
    Active = 0,
    Committed,
    Cancelled,
}

/// <summary>
/// The operation a preview sink should apply.
/// </summary>
public enum CardSettingsPreviewOperation
{
    Set = 0,
    Remove,
    Restore,
}

/// <summary>
/// Immutable, revisioned settings owned by one card instance.
/// </summary>
public sealed class CardSettingsSnapshot
{
    public const int MaxSettingsJsonBytes = 64 * 1024;

    private readonly JsonElement _settings;

    public CardSettingsSnapshot(
        string instanceId,
        string cardTypeId,
        int schemaVersion,
        long revision,
        JsonElement settings = default)
    {
        InstanceId = CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        CardTypeId = CardRuntimeContractGuards.RequireIdentifier(
            cardTypeId,
            nameof(cardTypeId));
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be positive.");
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "Settings revision cannot be negative.");
        }

        SchemaVersion = schemaVersion;
        Revision = revision;
        _settings = CloneSettings(settings);
    }

    public string InstanceId { get; }

    public string CardTypeId { get; }

    public int SchemaVersion { get; }

    public long Revision { get; }

    /// <summary>
    /// Returns a detached JSON object. JsonElement itself is immutable, and a
    /// fresh clone also prevents callers from retaining the source document.
    /// </summary>
    public JsonElement Settings => _settings.Clone();

    internal IReadOnlyDictionary<string, JsonElement> CopyProperties()
    {
        var properties = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (JsonProperty property in _settings.EnumerateObject())
        {
            properties[property.Name] = property.Value.Clone();
        }

        return properties;
    }

    internal static JsonElement CreateSettingsObject(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        using var stream = new MemoryStream();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> property in properties)
            {
                writer.WritePropertyName(property.Key);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        byte[] utf8 = stream.ToArray();
        EnsureSettingsSize(utf8);
        using JsonDocument document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }

    private static JsonElement CloneSettings(JsonElement settings)
    {
        if (settings.ValueKind == JsonValueKind.Undefined)
        {
            return CreateSettingsObject(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Card settings must be a JSON object.",
                nameof(settings));
        }

        JsonElement clone = settings.Clone();
        EnsureSettingsSize(JsonSerializer.SerializeToUtf8Bytes(clone));
        return clone;
    }

    internal static void EnsureSettingsSize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxSettingsJsonBytes)
        {
            throw new ArgumentException(
                $"Card settings JSON cannot exceed {MaxSettingsJsonBytes} bytes.",
                nameof(utf8));
        }
    }
}

/// <summary>
/// Immutable declaration for one editable setting.
/// </summary>
public sealed class CardSettingDefinition
{
    private readonly ReadOnlySet<JsonValueKind> _allowedKinds;
    private readonly Func<JsonElement, bool>? _validator;

    public CardSettingDefinition(
        string settingId,
        IEnumerable<JsonValueKind> allowedKinds,
        CardSettingWritePolicy writePolicy,
        Func<JsonElement, bool>? validator = null)
    {
        SettingId = CardRuntimeContractGuards.RequireIdentifier(
            settingId,
            nameof(settingId));
        ArgumentNullException.ThrowIfNull(allowedKinds);
        JsonValueKind[] materializedKinds = allowedKinds.ToArray();
        if (materializedKinds.Length == 0)
        {
            throw new ArgumentException(
                "At least one JSON value kind is required.",
                nameof(allowedKinds));
        }

        var kinds = new HashSet<JsonValueKind>();
        foreach (JsonValueKind kind in materializedKinds)
        {
            if (kind == JsonValueKind.Undefined)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(allowedKinds),
                    kind,
                    "Undefined is not a valid setting value kind.");
            }

            if (!kinds.Add(kind))
            {
                throw new ArgumentException(
                    "Allowed JSON value kinds cannot contain duplicates.",
                    nameof(allowedKinds));
            }
        }

        if (!Enum.IsDefined(writePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(writePolicy),
                writePolicy,
                "Write policy must be defined.");
        }

        _allowedKinds = new ReadOnlySet<JsonValueKind>(kinds);
        _validator = validator;
        WritePolicy = writePolicy;
    }

    public CardSettingDefinition(
        string settingId,
        JsonValueKind allowedKind,
        CardSettingWritePolicy writePolicy,
        Func<JsonElement, bool>? validator = null)
        : this(settingId, [allowedKind], writePolicy, validator)
    {
    }

    public string SettingId { get; }

    public IReadOnlySet<JsonValueKind> AllowedKinds => _allowedKinds;

    public CardSettingWritePolicy WritePolicy { get; }

    public bool PreviewSafe => WritePolicy == CardSettingWritePolicy.PreviewSafe;

    public bool CommitOnly => WritePolicy == CardSettingWritePolicy.CommitOnly;

    public bool Accepts(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined ||
            !_allowedKinds.Contains(value.ValueKind))
        {
            return false;
        }

        return _validator is null || _validator(value);
    }

    private sealed class ReadOnlySet<T>(ISet<T> source) :
        IReadOnlySet<T>
    {
        private readonly ISet<T> _source = new HashSet<T>(source);

        public int Count => _source.Count;

        public bool Contains(T item) => _source.Contains(item);

        public IEnumerator<T> GetEnumerator() => _source.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<T> other) =>
            _source.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<T> other) =>
            _source.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<T> other) => _source.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<T> other) => _source.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<T> other) => _source.Overlaps(other);

        public bool SetEquals(IEnumerable<T> other) => _source.SetEquals(other);
    }
}

/// <summary>
/// Validated setting declarations for a card type.
/// </summary>
public sealed class CardSettingsSchema
{
    private readonly ReadOnlyDictionary<string, CardSettingDefinition> _definitions;

    public CardSettingsSchema(
        IEnumerable<CardSettingDefinition> definitions,
        int schemaVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be positive.");
        }

        var materialized = new Dictionary<string, CardSettingDefinition>(
            StringComparer.Ordinal);
        foreach (CardSettingDefinition definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!materialized.TryAdd(definition.SettingId, definition))
            {
                throw new ArgumentException(
                    $"Duplicate setting ID '{definition.SettingId}'.",
                    nameof(definitions));
            }
        }

        _definitions = new ReadOnlyDictionary<string, CardSettingDefinition>(
            materialized);
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }

    public IReadOnlyDictionary<string, CardSettingDefinition> Definitions =>
        _definitions;

    public bool TryGetDefinition(
        string settingId,
        out CardSettingDefinition? definition)
    {
        definition = null;
        if (!CardRuntimeContractGuards.IsValidIdentifier(settingId))
        {
            return false;
        }

        return _definitions.TryGetValue(settingId, out definition);
    }

    public CardSettingDefinition GetDefinition(string settingId)
    {
        if (!TryGetDefinition(settingId, out CardSettingDefinition? definition))
        {
            throw new KeyNotFoundException(
                $"Unknown card setting '{settingId}'.");
        }

        return definition!;
    }
}

/// <summary>
/// Detached event sent to a preview sink.
/// </summary>
public sealed class CardSettingsPreviewChange
{
    public CardSettingsPreviewChange(
        string sessionId,
        string instanceId,
        string cardTypeId,
        long draftVersion,
        string settingId,
        CardSettingsPreviewOperation operation,
        bool valuePresent,
        JsonElement value = default)
    {
        SessionId = CardRuntimeContractGuards.RequireIdentifier(
            sessionId,
            nameof(sessionId));
        InstanceId = CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        CardTypeId = CardRuntimeContractGuards.RequireIdentifier(
            cardTypeId,
            nameof(cardTypeId));
        if (draftVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draftVersion),
                draftVersion,
                "Draft version cannot be negative.");
        }

        SettingId = CardRuntimeContractGuards.RequireIdentifier(
            settingId,
            nameof(settingId));
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Preview operation must be defined.");
        }

        if (valuePresent && value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "A present preview value cannot be Undefined.",
                nameof(value));
        }

        DraftVersion = draftVersion;
        Operation = operation;
        ValuePresent = valuePresent;
        _value = valuePresent ? value.Clone() : null;
    }

    private readonly JsonElement? _value;

    public string SessionId { get; }

    public string InstanceId { get; }

    public string CardTypeId { get; }

    public long DraftVersion { get; }

    public string SettingId { get; }

    public CardSettingsPreviewOperation Operation { get; }

    public bool ValuePresent { get; }

    public JsonElement? Value => _value?.Clone();

}

public sealed class CardSettingsCancelReport
{
    internal CardSettingsCancelReport(
        IReadOnlyList<CardSettingsPreviewChange> restoreChanges,
        IReadOnlyList<CardSettingsPreviewRestoreFailure> failures)
    {
        RestoreChanges = restoreChanges;
        Failures = failures;
    }

    public IReadOnlyList<CardSettingsPreviewChange> RestoreChanges { get; }

    public IReadOnlyList<CardSettingsPreviewRestoreFailure> Failures { get; }

    public bool Succeeded => Failures.Count == 0;
}

public sealed class CardSettingsPreviewRestoreFailure
{
    internal CardSettingsPreviewRestoreFailure(
        CardSettingsPreviewChange restoreChange,
        Exception exception)
    {
        RestoreChange = restoreChange;
        Exception = exception;
    }

    public CardSettingsPreviewChange RestoreChange { get; }

    public Exception Exception { get; }

    public string SettingId => RestoreChange.SettingId;
}

/// <summary>
/// Immutable two-phase commit request. It never performs persistence itself.
/// </summary>
public sealed class CardSettingsCommitRequest
{
    private readonly JsonElement _settings;

    internal CardSettingsCommitRequest(
        string sessionId,
        long draftVersion,
        CardSettingsSnapshot originalSnapshot,
        JsonElement settings)
    {
        SessionId = CardRuntimeContractGuards.RequireIdentifier(
            sessionId,
            nameof(sessionId));
        if (draftVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draftVersion),
                draftVersion,
                "Draft version cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(originalSnapshot);
        DraftVersion = draftVersion;
        InstanceId = originalSnapshot.InstanceId;
        CardTypeId = originalSnapshot.CardTypeId;
        SchemaVersion = originalSnapshot.SchemaVersion;
        ExpectedRevision = originalSnapshot.Revision;
        _settings = settings.Clone();
    }

    public string SessionId { get; }

    public long DraftVersion { get; }

    public string InstanceId { get; }

    public string CardTypeId { get; }

    public int SchemaVersion { get; }

    public long ExpectedRevision { get; }

    public JsonElement Settings => _settings.Clone();
}

/// <summary>
/// UI-independent settings draft with preview/cancel and two-phase commit.
/// </summary>
public sealed class CardSettingsDraft
{
    private readonly object _gate = new();
    private readonly CardSettingsSnapshot _originalSnapshot;
    private readonly CardSettingsSchema _schema;
    private readonly Dictionary<string, JsonElement> _values;
    private readonly Dictionary<string, JsonElement?> _originalPreviewValues;
    private readonly List<string> _previewOrder = [];
    private readonly Action<CardSettingsPreviewChange>? _previewSink;
    // Serializes preview publication order only. External sink code is
    // always called after _gate has been released; this is not the state lock.
    private readonly object _publishGate = new();
    private CardSettingsDraftState _state = CardSettingsDraftState.Active;
    private long _version;

    /// <summary>
    /// Creates an active settings editing session. The optional preview sink
    /// is a trusted, synchronous, local callback and must not throw.
    /// </summary>
    /// <param name="previewSink">
    /// A trusted local callback invoked synchronously after a preview-safe
    /// value has been accepted. If it throws, <see cref="Set"/> or
    /// <see cref="Remove"/> propagates the exception; the draft retains its
    /// new value and remains active. The caller should cancel or discard that
    /// session and reopen it instead of retrying the same-value dispatch.
    /// Restore callbacks during <see cref="Cancel"/> are best-effort and are
    /// reported through the returned failure collection.
    /// </param>
    public CardSettingsDraft(
        CardSettingsSnapshot snapshot,
        CardSettingsSchema schema,
        string? sessionId = null,
        Action<CardSettingsPreviewChange>? previewSink = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(schema);
        if (snapshot.SchemaVersion != schema.SchemaVersion)
        {
            throw new ArgumentException(
                "Snapshot and settings schema versions must match.",
                nameof(schema));
        }

        _originalSnapshot = snapshot;
        _schema = schema;
        _values = new Dictionary<string, JsonElement>(
            snapshot.CopyProperties(),
            StringComparer.Ordinal);
        _originalPreviewValues = new Dictionary<string, JsonElement?>(
            StringComparer.Ordinal);
        foreach (CardSettingDefinition definition in schema.Definitions.Values)
        {
            if (_values.TryGetValue(definition.SettingId, out JsonElement value) &&
                !definition.Accepts(value))
            {
                throw new ArgumentException(
                    $"Snapshot value for setting '{definition.SettingId}' " +
                    "does not satisfy its schema.",
                    nameof(snapshot));
            }
        }

        SessionId = CardRuntimeContractGuards.RequireIdentifier(
            string.IsNullOrWhiteSpace(sessionId)
                ? Guid.NewGuid().ToString("N")
                : sessionId,
            nameof(sessionId));
        _previewSink = previewSink;
    }

    public string SessionId { get; }

    public CardSettingsDraftState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public long DraftVersion => Version;

    public CardSettingsSnapshot OriginalSnapshot => _originalSnapshot;

    public JsonElement Settings
    {
        get
        {
            lock (_gate)
            {
                return CardSettingsSnapshot.CreateSettingsObject(_values);
            }
        }
    }

    public CardSettingsPreviewChange? Set(string settingId, JsonElement value)
    {
        CardSettingDefinition definition;
        lock (_gate)
        {
            EnsureActive();
            definition = GetDefinition(settingId);
        }

        // Validators are external delegates. They run with no draft state
        // lock held, so a validator may safely inspect or re-enter the draft.
        if (!definition.Accepts(value))
        {
            throw new ArgumentException(
                $"Value for setting '{settingId}' does not satisfy its schema.",
                nameof(value));
        }

        lock (_publishGate)
        {
            CardSettingsPreviewChange? previewChange;
            lock (_gate)
            {
                EnsureActive();

                JsonElement detachedValue = value.Clone();
                if (_values.TryGetValue(settingId, out JsonElement current) &&
                    JsonElement.DeepEquals(current, detachedValue))
                {
                    return null;
                }

                var candidateValues = new Dictionary<string, JsonElement>(
                    _values,
                    StringComparer.Ordinal)
                {
                    [settingId] = detachedValue,
                };
                // Validate the complete candidate before mutating the draft;
                // this keeps the update atomic when the 64 KiB contract is
                // exceeded.
                _ = CardSettingsSnapshot.CreateSettingsObject(candidateValues);

                long nextVersion = checked(_version + 1);
                TrackPreviewBaseline(definition, settingId);
                _values[settingId] = detachedValue;
                _version = nextVersion;
                previewChange = definition.PreviewSafe
                    ? CreatePreviewChange(
                        settingId,
                        CardSettingsPreviewOperation.Set,
                        valuePresent: true,
                        detachedValue)
                    : null;
            }

            if (previewChange is not null)
            {
                PublishPreviewCore(previewChange, _previewSink, null);
            }

            return previewChange;
        }
    }

    public CardSettingsPreviewChange? Remove(string settingId)
    {
        lock (_publishGate)
        {
            CardSettingsPreviewChange? previewChange;
            lock (_gate)
            {
                EnsureActive();
                CardSettingDefinition definition = GetDefinition(settingId);
                if (!_values.ContainsKey(settingId))
                {
                    return null;
                }

                long nextVersion = checked(_version + 1);
                TrackPreviewBaseline(definition, settingId);
                _values.Remove(settingId);
                _version = nextVersion;
                previewChange = definition.PreviewSafe
                    ? CreatePreviewChange(
                        settingId,
                        CardSettingsPreviewOperation.Remove,
                        valuePresent: false)
                    : null;
            }

            if (previewChange is not null)
            {
                PublishPreviewCore(previewChange, _previewSink, null);
            }

            return previewChange;
        }
    }

    public CardSettingsCommitRequest BuildCommitRequest()
    {
        lock (_gate)
        {
            EnsureActive();
            JsonElement settings = CardSettingsSnapshot.CreateSettingsObject(_values);
            return new CardSettingsCommitRequest(
                SessionId,
                _version,
                _originalSnapshot,
                settings);
        }
    }

    public CardSettingsCancelReport Cancel()
    {
        lock (_publishGate)
        {
            List<CardSettingsPreviewChange> restoreChanges;
            lock (_gate)
            {
                EnsureActive();
                restoreChanges = [];
                for (int index = _previewOrder.Count - 1; index >= 0; index--)
                {
                    string settingId = _previewOrder[index];
                    JsonElement? original = _originalPreviewValues[settingId];
                    restoreChanges.Add(CreatePreviewChange(
                        settingId,
                        CardSettingsPreviewOperation.Restore,
                        original.HasValue,
                        original.GetValueOrDefault()));
                }

                _state = CardSettingsDraftState.Cancelled;
            }

            var failures = new List<CardSettingsPreviewRestoreFailure>();
            foreach (CardSettingsPreviewChange change in restoreChanges)
            {
                PublishPreviewCore(change, _previewSink, failures);
            }

            return new CardSettingsCancelReport(
                new ReadOnlyCollection<CardSettingsPreviewChange>(restoreChanges),
                new ReadOnlyCollection<CardSettingsPreviewRestoreFailure>(failures));
        }
    }

    public CardSettingsSnapshot AcceptCommit(
        CardSettingsCommitRequest request,
        long newRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            EnsureActive();
            ValidateCommitRequest(request);
            ValidateNewRevision(newRevision);

            CardSettingsSnapshot committed = new(
                request.InstanceId,
                request.CardTypeId,
                request.SchemaVersion,
                newRevision,
                request.Settings);
            _state = CardSettingsDraftState.Committed;
            return committed;
        }
    }

    private void EnsureActive()
    {
        if (_state != CardSettingsDraftState.Active)
        {
            throw new InvalidOperationException(
                $"The settings draft is already {_state.ToString().ToLowerInvariant()}.");
        }
    }

    private CardSettingDefinition GetDefinition(string settingId)
    {
        if (!_schema.TryGetDefinition(settingId, out CardSettingDefinition? definition))
        {
            throw new ArgumentException(
                $"Unknown card setting '{settingId}'.",
                nameof(settingId));
        }

        return definition!;
    }

    private void TrackPreviewBaseline(
        CardSettingDefinition definition,
        string settingId)
    {
        if (!definition.PreviewSafe || _originalPreviewValues.ContainsKey(settingId))
        {
            return;
        }

        _originalPreviewValues[settingId] = _values.TryGetValue(
            settingId,
            out JsonElement original)
            ? original.Clone()
            : null;
        _previewOrder.Add(settingId);
    }

    private CardSettingsPreviewChange CreatePreviewChange(
        string settingId,
        CardSettingsPreviewOperation operation,
        bool valuePresent,
        JsonElement value = default) =>
        new(
            SessionId,
            _originalSnapshot.InstanceId,
            _originalSnapshot.CardTypeId,
            _version,
            settingId,
            operation,
            valuePresent,
            value);

    private static void PublishPreviewCore(
        CardSettingsPreviewChange change,
        Action<CardSettingsPreviewChange>? sink,
        List<CardSettingsPreviewRestoreFailure>? failures)
    {
        if (sink is not null)
        {
            try
            {
                sink(change);
            }
            catch (Exception exception)
            {
                if (failures is null)
                {
                    throw;
                }

                failures.Add(new CardSettingsPreviewRestoreFailure(change, exception));
            }
        }

    }

    private void ValidateCommitRequest(CardSettingsCommitRequest request)
    {
        if (!string.Equals(
                request.SessionId,
                SessionId,
                StringComparison.Ordinal) ||
            request.DraftVersion != _version ||
            !string.Equals(
                request.InstanceId,
                _originalSnapshot.InstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.CardTypeId,
                _originalSnapshot.CardTypeId,
                StringComparison.Ordinal) ||
            request.SchemaVersion != _originalSnapshot.SchemaVersion ||
            request.ExpectedRevision != _originalSnapshot.Revision)
        {
            throw new InvalidOperationException(
                "Commit request does not belong to the active settings draft.");
        }

        if (!JsonElement.DeepEquals(request.Settings, Settings))
        {
            throw new InvalidOperationException(
                "Commit request settings are stale.");
        }
    }

    private void ValidateNewRevision(long newRevision)
    {
        long expectedNext;
        try
        {
            expectedNext = checked(_originalSnapshot.Revision + 1);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "No forward settings revision is available.",
                exception);
        }

        if (newRevision != expectedNext)
        {
            throw new InvalidOperationException(
                "Committed revision must be exactly expected revision plus one.");
        }
    }
}
