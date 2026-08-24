using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// One built-in card as an identity plus its definition. The catalog's definitions carry a
/// card type but no instance ID, and the layout is keyed by instance, so the pair is what the
/// add-card picker actually needs.
/// </summary>
public sealed record BuiltInCardInstance(
    string InstanceId,
    ICardDefinition Definition);

public static class BuiltInCardCatalog
{
    public const string NotesInstanceId = "demo.notes";
    public const string WeatherInstanceId = "demo.weather";
    public const string TimerInstanceId = "demo.timer";
    public const string TodoInstanceId = "demo.todo";
    public const string CalendarInstanceId = "demo.calendar";
    public const string SystemMonitorInstanceId = "demo.sysmon";

    public const string NotesCardTypeId = "builtin.notes";
    public const string WeatherCardTypeId = "builtin.weather";
    public const string TimerCardTypeId = "builtin.timer";
    public const string TodoCardTypeId = "builtin.todo";
    public const string CalendarCardTypeId = "builtin.calendar";
    public const string SystemMonitorCardTypeId = "builtin.sysmon";
    public const string UnknownCardTypeId = "builtin.unknown";

    private static readonly CardSize[] StandardSizes =
    [
        CardSize.S,
        CardSize.M,
        CardSize.L,
        CardSize.W,
        CardSize.XL,
    ];

    public static ICardDefinition Notes { get; } = new CardDefinition(
        NotesCardTypeId,
        "NotesCardTitle.Text",
        CardSize.L,
        StandardSizes);

    public static ICardDefinition Weather { get; } = new CardDefinition(
        WeatherCardTypeId,
        "WeatherCardTitle.Text",
        CardSize.M,
        StandardSizes);

    public static ICardDefinition Timer { get; } = new CardDefinition(
        TimerCardTypeId,
        "TimerCardTitle.Text",
        CardSize.M,
        StandardSizes);

    public static ICardDefinition Todo { get; } = new CardDefinition(
        TodoCardTypeId,
        "TodoCardTitle.Text",
        CardSize.M,
        StandardSizes);

    public static ICardDefinition Calendar { get; } = new CardDefinition(
        CalendarCardTypeId,
        "CalendarCardTitle.Text",
        CardSize.M,
        StandardSizes);

    // Larger than the other built-ins by default: the card stacks one row per reading, and at
    // M the shipped five-metric default is clipped after the second row.
    public static ICardDefinition SystemMonitor { get; } = new CardDefinition(
        SystemMonitorCardTypeId,
        "SystemMonitorCardTitle.Text",
        CardSize.L,
        StandardSizes);

    public static ICardDefinition Unknown { get; } = new CardDefinition(
        UnknownCardTypeId,
        "UnknownCardTitle.Text",
        CardSize.M,
        StandardSizes);

    public static IReadOnlyList<ICardDefinition> All { get; } =
        Array.AsReadOnly(
        [
            Notes,
            Weather,
            Timer,
            Todo,
            Calendar,
            SystemMonitor,
            Unknown,
        ]);

    /// <summary>
    /// The instances the panel can put back on the board, in the order the picker offers
    /// them - the same order the panel ships with. Unknown is excluded: it is the fallback a
    /// stored layout resolves to when its card type is gone, not something to add on purpose.
    /// </summary>
    public static IReadOnlyList<BuiltInCardInstance> Addable { get; } =
        Array.AsReadOnly<BuiltInCardInstance>(
        [
            new BuiltInCardInstance(NotesInstanceId, Notes),
            new BuiltInCardInstance(WeatherInstanceId, Weather),
            new BuiltInCardInstance(TimerInstanceId, Timer),
            new BuiltInCardInstance(TodoInstanceId, Todo),
            new BuiltInCardInstance(CalendarInstanceId, Calendar),
            new BuiltInCardInstance(SystemMonitorInstanceId, SystemMonitor),
        ]);

    /// <summary>
    /// True for card types whose content is published by CoreBroker rather than produced in
    /// the panel. The panel must never manufacture a snapshot for one of these: the broker is
    /// the only source of their content, so anything the panel invents overwrites real data
    /// until the next broker event arrives.
    /// </summary>
    public static bool IsBrokerBacked(string cardTypeId) =>
        cardTypeId is WeatherCardTypeId or SystemMonitorCardTypeId;

    public static ICardDefinition ResolveInstance(string instanceId)
    {
        CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        return instanceId switch
        {
            NotesInstanceId => Notes,
            WeatherInstanceId => Weather,
            TimerInstanceId => Timer,
            TodoInstanceId => Todo,
            CalendarInstanceId => Calendar,
            SystemMonitorInstanceId => SystemMonitor,
            _ => Unknown,
        };
    }
}

public static class BuiltInCardRuntimeFactory
{
    public const int CurrentSchemaVersion = 1;

    public static CardRuntimeInstance Create(
        string instanceId,
        NoteEditorViewModel noteEditor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(noteEditor);
        ICardDefinition definition =
            BuiltInCardCatalog.ResolveInstance(instanceId);
        DateTimeOffset timestampUtc =
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        CardRuntimeSnapshot initialSnapshot =
            definition.CardTypeId switch
            {
                BuiltInCardCatalog.NotesCardTypeId =>
                    CreateNoteSnapshot(
                        instanceId,
                        noteEditor,
                        sequence: 0,
                        timestampUtc),
                BuiltInCardCatalog.UnknownCardTypeId =>
                    CreateUnknownSnapshot(
                        instanceId,
                        sequence: 0,
                        timestampUtc),
                _ when BuiltInCardCatalog.IsBrokerBacked(definition.CardTypeId) =>
                    CreateAwaitingBrokerSnapshot(
                        instanceId,
                        definition.CardTypeId,
                        sequence: 0,
                        timestampUtc),
                _ => CreatePlaceholderSnapshot(
                    instanceId,
                    definition.CardTypeId,
                    sequence: 0,
                    timestampUtc),
            };
        var runtime = new CardRuntimeInstance(
            definition,
            instanceId,
            initialSnapshot);
        runtime.Initialize();
        return runtime;
    }

    public static bool SynchronizeNote(
        CardRuntimeInstance runtime,
        NoteEditorViewModel noteEditor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(noteEditor);
        if (!string.Equals(
                runtime.Definition.CardTypeId,
                BuiltInCardCatalog.NotesCardTypeId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only the built-in notes runtime can synchronize a note editor.",
                nameof(runtime));
        }

        CardRuntimeSnapshot snapshot = CreateNoteSnapshot(
            runtime.InstanceId,
            noteEditor,
            checked(runtime.Snapshot.Sequence + 1),
            (timeProvider ?? TimeProvider.System).GetUtcNow());
        return runtime.ApplySnapshot(snapshot);
    }

    public static CardRuntimeSnapshot CreateFreshSnapshot(
        CardRuntimeInstance runtime,
        NoteEditorViewModel noteEditor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(noteEditor);
        long sequence = checked(runtime.Snapshot.Sequence + 1);
        DateTimeOffset timestampUtc =
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        return runtime.Definition.CardTypeId switch
        {
            BuiltInCardCatalog.NotesCardTypeId => CreateNoteSnapshot(
                runtime.InstanceId,
                noteEditor,
                sequence,
                timestampUtc),
            BuiltInCardCatalog.UnknownCardTypeId => CreateUnknownSnapshot(
                runtime.InstanceId,
                sequence,
                timestampUtc),
            // The panel has nothing fresher to say about a broker-backed card, so it says
            // nothing: returning the current snapshot leaves the sequence unchanged and
            // ApplySnapshot treats it as a no-op. Manufacturing a placeholder here is what
            // made the weather and hardware cards drop back to "unavailable" on every show -
            // the visibility scheduler refreshes each card as it becomes visible, and the
            // placeholder carried a higher local sequence than the broker data it replaced.
            _ when BuiltInCardCatalog.IsBrokerBacked(
                runtime.Definition.CardTypeId) => runtime.Snapshot,
            _ => CreatePlaceholderSnapshot(
                runtime.InstanceId,
                runtime.Definition.CardTypeId,
                sequence,
                timestampUtc),
        };
    }

    private static CardRuntimeSnapshot CreateNoteSnapshot(
        string instanceId,
        NoteEditorViewModel noteEditor,
        long sequence,
        DateTimeOffset timestampUtc)
    {
        CardRuntimeStatus status = MapNoteStatus(noteEditor.Status);
        CardRuntimeFreshness freshness = status switch
        {
            CardRuntimeStatus.Ready => CardRuntimeFreshness.Fresh,
            CardRuntimeStatus.Error => CardRuntimeFreshness.Stale,
            _ => CardRuntimeFreshness.Unknown,
        };
        string? errorCode = status switch
        {
            CardRuntimeStatus.Unavailable => NormalizeErrorCode(
                noteEditor.ErrorCode,
                CardRuntimeErrorCodes.NoteUnavailable),
            CardRuntimeStatus.Error => NormalizeErrorCode(
                noteEditor.ErrorCode,
                CardRuntimeErrorCodes.NoteFailed),
            CardRuntimeStatus.Unknown =>
                CardRuntimeErrorCodes.NoteStatusUnknown,
            _ => null,
        };
        string[] allowedActions = status switch
        {
            CardRuntimeStatus.Ready =>
                [CardRuntimeActionIds.Edit],
            CardRuntimeStatus.Unavailable or CardRuntimeStatus.Error =>
            [
                CardRuntimeActionIds.Retry,
                CardRuntimeActionIds.OpenDiagnostics,
                CardRuntimeActionIds.Disable,
            ],
            _ => [],
        };
        JsonElement payload = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["noteId"] = noteEditor.NoteId,
                ["editorStatus"] = noteEditor.Status.ToString(),
            });

        return new CardRuntimeSnapshot(
            instanceId,
            BuiltInCardCatalog.NotesCardTypeId,
            CurrentSchemaVersion,
            sequence,
            timestampUtc,
            freshness,
            status,
            payload,
            allowedActions,
            errorCode);
    }

    // A card whose content is on its way from CoreBroker. Loading rather than Unavailable:
    // the card can provide content, it just has none yet, and Unavailable states the
    // opposite. It carries no error code and offers no action - there is nothing wrong to
    // retry or disable.
    private static CardRuntimeSnapshot CreateAwaitingBrokerSnapshot(
        string instanceId,
        string cardTypeId,
        long sequence,
        DateTimeOffset timestampUtc) =>
        new(
            instanceId,
            cardTypeId,
            CurrentSchemaVersion,
            sequence,
            timestampUtc,
            CardRuntimeFreshness.Unknown,
            CardRuntimeStatus.Loading);

    // A card the shipped scope defers (ADR-0022). Unlike the broker-backed cards there is no
    // content coming, so Unavailable is the truthful state.
    private static CardRuntimeSnapshot CreatePlaceholderSnapshot(
        string instanceId,
        string cardTypeId,
        long sequence,
        DateTimeOffset timestampUtc) =>
        new(
            instanceId,
            cardTypeId,
            CurrentSchemaVersion,
            sequence,
            timestampUtc,
            CardRuntimeFreshness.Unknown,
            CardRuntimeStatus.Unavailable,
            allowedActionIds: [CardRuntimeActionIds.Disable],
            errorCode: CardRuntimeErrorCodes.NotImplemented);

    private static CardRuntimeSnapshot CreateUnknownSnapshot(
        string instanceId,
        long sequence,
        DateTimeOffset timestampUtc) =>
        new(
            instanceId,
            BuiltInCardCatalog.UnknownCardTypeId,
            CurrentSchemaVersion,
            sequence,
            timestampUtc,
            CardRuntimeFreshness.Unknown,
            CardRuntimeStatus.Unknown,
            errorCode: CardRuntimeErrorCodes.UnknownCard);

    internal static CardRuntimeStatus MapNoteStatus(
        NoteEditorStatus status) =>
        status switch
        {
            NoteEditorStatus.Unavailable =>
                CardRuntimeStatus.Unavailable,
            NoteEditorStatus.Loading =>
                CardRuntimeStatus.Loading,
            NoteEditorStatus.Ready or
                NoteEditorStatus.PendingSave or
                NoteEditorStatus.Saving or
                NoteEditorStatus.Saved =>
                CardRuntimeStatus.Ready,
            NoteEditorStatus.Error =>
                CardRuntimeStatus.Error,
            _ => CardRuntimeStatus.Unknown,
        };

    private static string NormalizeErrorCode(
        string? errorCode,
        string fallback) =>
        CardRuntimeContractGuards.IsValidIdentifier(errorCode)
            ? errorCode!
            : fallback;
}
