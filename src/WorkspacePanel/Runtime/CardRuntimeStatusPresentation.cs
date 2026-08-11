using System.Globalization;
using System.Text.Json;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

public static class CardRuntimeStatusResourceKeys
{
    public const string UnknownTitle = "CardStatus.Unknown.Title";
    public const string UnknownSummary = "CardStatus.Unknown.Summary";
    public const string LoadingTitle = "CardStatus.Loading.Title";
    public const string LoadingSummary = "CardStatus.Loading.Summary";
    public const string ReadyTitle = "CardStatus.Ready.Title";
    public const string ReadySummary = "CardStatus.Ready.Summary";
    public const string EmptyTitle = "CardStatus.Empty.Title";
    public const string EmptySummary = "CardStatus.Empty.Summary";
    public const string StaleTitle = "CardStatus.Stale.Title";
    public const string StaleSummary = "CardStatus.Stale.Summary";
    public const string OfflineTitle = "CardStatus.Offline.Title";
    public const string OfflineSummary = "CardStatus.Offline.Summary";
    public const string PermissionRequiredTitle =
        "CardStatus.PermissionRequired.Title";
    public const string PermissionRequiredSummary =
        "CardStatus.PermissionRequired.Summary";
    public const string UnavailableTitle = "CardStatus.Unavailable.Title";
    public const string UnavailableSummary = "CardStatus.Unavailable.Summary";
    public const string ErrorTitle = "CardStatus.Error.Title";
    public const string ErrorSummary = "CardStatus.Error.Summary";
    public const string DisabledTitle = "CardStatus.Disabled.Title";
    public const string DisabledSummary = "CardStatus.Disabled.Summary";

    public const string FreshnessStale = "CardStatus.Freshness.Stale";
    public const string FreshnessOffline = "CardStatus.Freshness.Offline";

    public static string GetTitleKey(CardRuntimeStatus status) =>
        GetKeys(status).Title;

    public static string GetSummaryKey(CardRuntimeStatus status) =>
        GetKeys(status).Summary;

    private static (string Title, string Summary) GetKeys(
        CardRuntimeStatus status) =>
        status switch
        {
            CardRuntimeStatus.Loading =>
                (LoadingTitle, LoadingSummary),
            CardRuntimeStatus.Ready =>
                (ReadyTitle, ReadySummary),
            CardRuntimeStatus.Empty =>
                (EmptyTitle, EmptySummary),
            CardRuntimeStatus.Stale =>
                (StaleTitle, StaleSummary),
            CardRuntimeStatus.Offline =>
                (OfflineTitle, OfflineSummary),
            CardRuntimeStatus.PermissionRequired =>
                (PermissionRequiredTitle, PermissionRequiredSummary),
            CardRuntimeStatus.Unavailable =>
                (UnavailableTitle, UnavailableSummary),
            CardRuntimeStatus.Error =>
                (ErrorTitle, ErrorSummary),
            CardRuntimeStatus.Disabled =>
                (DisabledTitle, DisabledSummary),
            _ => (UnknownTitle, UnknownSummary),
        };
}

public sealed record CardRuntimeStatusPresentation
{
    private CardRuntimeStatusPresentation(
        CardRuntimeStatus status,
        CardRuntimeFreshness freshness,
        string title,
        string summary,
        string freshnessText,
        string automationSummary,
        bool hasPayload,
        bool isStateVisible,
        bool isContentVisible,
        bool canRetry,
        bool canRefresh,
        bool canEdit,
        bool canConfigure,
        bool canOpenDiagnostics,
        bool canDisable)
    {
        Status = status;
        Freshness = freshness;
        Title = title;
        Summary = summary;
        FreshnessText = freshnessText;
        AutomationSummary = automationSummary;
        HasPayload = hasPayload;
        IsStateVisible = isStateVisible;
        IsContentVisible = isContentVisible;
        IsLoading = status == CardRuntimeStatus.Loading;
        IsCritical = status == CardRuntimeStatus.Error;
        HasFreshnessText = freshnessText.Length > 0;
        CanRetry = canRetry;
        CanRefresh = canRefresh;
        CanEdit = canEdit;
        CanConfigure = canConfigure;
        CanOpenDiagnostics = canOpenDiagnostics;
        CanDisable = canDisable;
    }

    public CardRuntimeStatus Status { get; }

    public CardRuntimeFreshness Freshness { get; }

    public string Title { get; }

    public string Summary { get; }

    public string FreshnessText { get; }

    public string AutomationSummary { get; }

    public bool HasPayload { get; }

    public bool IsStateVisible { get; }

    public bool IsContentVisible { get; }

    public bool IsLoading { get; }

    public bool IsCritical { get; }

    public bool HasFreshnessText { get; }

    public bool CanRetry { get; }

    public bool CanRefresh { get; }

    public bool CanEdit { get; }

    public bool CanConfigure { get; }

    public bool CanOpenDiagnostics { get; }

    public bool CanDisable { get; }

    public static CardRuntimeStatusPresentation Create(
        CardRuntimeSnapshot snapshot,
        Func<string, string?>? resourceResolver = null,
        Func<DateTimeOffset, string>? timestampFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        resourceResolver ??= static key => key;
        timestampFormatter ??= static timestamp => timestamp
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);

        CardRuntimeStatus status = Enum.IsDefined(snapshot.Status)
            ? snapshot.Status
            : CardRuntimeStatus.Unknown;
        CardRuntimeFreshness freshness = Enum.IsDefined(snapshot.Freshness)
            ? snapshot.Freshness
            : CardRuntimeFreshness.Unknown;
        bool hasPayload = HasPayloadValue(snapshot.Payload);
        bool isReadyStale = status == CardRuntimeStatus.Ready &&
            freshness == CardRuntimeFreshness.Stale;
        CardRuntimeStatus visualStatus = isReadyStale
            ? CardRuntimeStatus.Stale
            : status;
        string title = Resolve(
            resourceResolver,
            CardRuntimeStatusResourceKeys.GetTitleKey(visualStatus));
        string summary = Resolve(
            resourceResolver,
            CardRuntimeStatusResourceKeys.GetSummaryKey(visualStatus));
        string freshnessText = CreateFreshnessText(
            status,
            freshness,
            snapshot.TimestampUtc,
            resourceResolver,
            timestampFormatter);
        bool isStateVisible = status != CardRuntimeStatus.Ready ||
            freshness == CardRuntimeFreshness.Stale;
        bool isContentVisible = status == CardRuntimeStatus.Ready ||
            ((status is CardRuntimeStatus.Stale or CardRuntimeStatus.Offline or
                CardRuntimeStatus.Error) && hasPayload);
        if (status == CardRuntimeStatus.Unknown)
        {
            isContentVisible = false;
        }

        string automationSummary = JoinNonEmpty(
            title,
            summary,
            freshnessText);

        return new CardRuntimeStatusPresentation(
            status,
            freshness,
            title,
            summary,
            freshnessText,
            automationSummary,
            hasPayload,
            isStateVisible,
            isContentVisible,
            AllowsAction(snapshot, status, CardRuntimeActionIds.Retry),
            AllowsAction(snapshot, status, CardRuntimeActionIds.Refresh),
            AllowsAction(snapshot, status, CardRuntimeActionIds.Edit),
            AllowsAction(snapshot, status, CardRuntimeActionIds.Configure),
            AllowsAction(
                snapshot,
                status,
                CardRuntimeActionIds.OpenDiagnostics),
            AllowsAction(snapshot, status, CardRuntimeActionIds.Disable));
    }

    public static bool HasPayloadValue(JsonElement payload) =>
        payload.ValueKind switch
        {
            JsonValueKind.Object => payload.EnumerateObject().MoveNext(),
            JsonValueKind.Array => payload.GetArrayLength() > 0,
            JsonValueKind.String or
                JsonValueKind.Number or
                JsonValueKind.True or
                JsonValueKind.False => true,
            _ => false,
        };

    private static bool AllowsAction(
        CardRuntimeSnapshot snapshot,
        CardRuntimeStatus status,
        string actionId) =>
        status != CardRuntimeStatus.Unknown &&
        snapshot.AllowsAction(actionId);

    private static string CreateFreshnessText(
        CardRuntimeStatus status,
        CardRuntimeFreshness freshness,
        DateTimeOffset timestampUtc,
        Func<string, string?> resourceResolver,
        Func<DateTimeOffset, string> timestampFormatter)
    {
        string? resourceKey = status switch
        {
            CardRuntimeStatus.Offline =>
                CardRuntimeStatusResourceKeys.FreshnessOffline,
            CardRuntimeStatus.Stale =>
                CardRuntimeStatusResourceKeys.FreshnessStale,
            CardRuntimeStatus.Ready when
                freshness == CardRuntimeFreshness.Stale =>
                CardRuntimeStatusResourceKeys.FreshnessStale,
            CardRuntimeStatus.Error when
                freshness == CardRuntimeFreshness.Stale =>
                CardRuntimeStatusResourceKeys.FreshnessStale,
            _ => null,
        };
        if (resourceKey is null)
        {
            return string.Empty;
        }

        string label = Resolve(resourceResolver, resourceKey);
        string timestamp = timestampFormatter(timestampUtc);
        if (label.Contains("{0}", StringComparison.Ordinal))
        {
            try
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    label,
                    timestamp);
            }
            catch (FormatException)
            {
                // A malformed resource must not crash a card. The fallback
                // still gives the user a localized key and timestamp.
            }
        }

        return JoinNonEmpty(label, timestamp);
    }

    private static string Resolve(
        Func<string, string?> resolver,
        string key)
    {
        string? value = resolver(key);
        return string.IsNullOrWhiteSpace(value)
            ? key
            : value;
    }

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(
            " · ",
            values.Where(value => !string.IsNullOrWhiteSpace(value)));
}
