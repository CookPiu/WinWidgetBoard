using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardRuntimeStatusPresentationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 11, 8, 30, 0, TimeSpan.Zero);
    private static readonly int[] SingleItemPayload = [1];

    [TestMethod(DisplayName = "UT-CARD-058 [CRD-001] Presentation maps every declared status to localized state fields")]
    public void PresentationMapsEveryDeclaredStatusToLocalizedStateFields()
    {
        CardRuntimeStatus[] statuses =
        [
            CardRuntimeStatus.Unknown,
            CardRuntimeStatus.Loading,
            CardRuntimeStatus.Ready,
            CardRuntimeStatus.Empty,
            CardRuntimeStatus.Stale,
            CardRuntimeStatus.Offline,
            CardRuntimeStatus.PermissionRequired,
            CardRuntimeStatus.Unavailable,
            CardRuntimeStatus.Error,
            CardRuntimeStatus.Disabled,
        ];

        foreach (CardRuntimeStatus status in statuses)
        {
            CardRuntimeStatusPresentation presentation = CreatePresentation(
                status,
                freshness: status == CardRuntimeStatus.Stale
                    ? CardRuntimeFreshness.Stale
                    : CardRuntimeFreshness.Fresh,
                resolver: key =>
                {
                    string[] segments = key.Split('.');
                    return segments.Length >= 3 &&
                        string.Equals(
                            segments[^1],
                            "Title",
                            StringComparison.Ordinal)
                        ? $"title:{segments[1]}"
                        : $"summary:{segments[1]}";
                });

            Assert.AreEqual(status, presentation.Status);
            Assert.AreEqual(
                $"title:{status}",
                presentation.Title);
            Assert.AreEqual(
                $"summary:{status}",
                presentation.Summary);
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                presentation.AutomationSummary));
        }
    }

    [TestMethod(DisplayName = "UT-CARD-059 [CRD-001] Unknown and invalid states fail safe with no content or actions")]
    public void UnknownAndInvalidStatesFailSafeWithNoContentOrActions()
    {
        CardRuntimeStatusPresentation unknown = CreatePresentation(
            CardRuntimeStatus.Unknown,
            payload: JsonSerializer.SerializeToElement(
                new { secret = "must-not-render" }),
            actions:
            [
                CardRuntimeActionIds.Retry,
                CardRuntimeActionIds.Edit,
            ]);
        CardRuntimeStatusPresentation invalid = CreatePresentation(
            (CardRuntimeStatus)999,
            payload: JsonSerializer.SerializeToElement(
                new { secret = "must-not-render" }),
            actions: [CardRuntimeActionIds.Refresh]);

        foreach (CardRuntimeStatusPresentation presentation in
                 new[] { unknown, invalid })
        {
            Assert.AreEqual(CardRuntimeStatus.Unknown, presentation.Status);
            Assert.IsTrue(presentation.IsStateVisible);
            Assert.IsFalse(presentation.IsContentVisible);
            Assert.IsFalse(presentation.IsLoading);
            Assert.IsFalse(presentation.IsCritical);
            Assert.IsFalse(presentation.CanRetry);
            Assert.IsFalse(presentation.CanRefresh);
            Assert.IsFalse(presentation.CanEdit);
            Assert.IsFalse(presentation.CanConfigure);
            Assert.IsFalse(presentation.CanOpenDiagnostics);
            Assert.IsFalse(presentation.CanDisable);
            Assert.IsFalse(presentation.AutomationSummary.Contains(
                "must-not-render",
                StringComparison.Ordinal));
        }
    }

    [TestMethod(DisplayName = "UT-CARD-060 [CRD-002] Stale precedence preserves content and uses injected timestamp formatting")]
    public void StalePrecedencePreservesContentAndUsesInjectedTimestampFormatting()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new { value = 42 });
        CardRuntimeStatusPresentation readyStale = CreatePresentation(
            CardRuntimeStatus.Ready,
            CardRuntimeFreshness.Stale,
            payload,
            timestampFormatter: timestamp =>
                $"formatted:{timestamp:O}");
        CardRuntimeStatusPresentation explicitStale = CreatePresentation(
            CardRuntimeStatus.Stale,
            CardRuntimeFreshness.Fresh,
            payload,
            timestampFormatter: timestamp =>
                $"formatted:{timestamp:O}");
        CardRuntimeStatusPresentation offline = CreatePresentation(
            CardRuntimeStatus.Offline,
            CardRuntimeFreshness.Unknown,
            payload,
            timestampFormatter: timestamp =>
                $"formatted:{timestamp:O}");
        CardRuntimeStatusPresentation errorStale = CreatePresentation(
            CardRuntimeStatus.Error,
            CardRuntimeFreshness.Stale,
            payload,
            errorCode: "provider.secret-code",
            timestampFormatter: timestamp =>
                $"formatted:{timestamp:O}");

        Assert.AreEqual(CardRuntimeStatus.Ready, readyStale.Status);
        Assert.IsTrue(readyStale.IsStateVisible);
        Assert.IsTrue(readyStale.IsContentVisible);
        Assert.IsTrue(readyStale.HasFreshnessText);
        StringAssert.Contains(readyStale.FreshnessText, "formatted:");

        Assert.AreEqual(CardRuntimeStatus.Stale, explicitStale.Status);
        Assert.IsTrue(explicitStale.IsStateVisible);
        Assert.IsTrue(explicitStale.IsContentVisible);
        Assert.IsTrue(explicitStale.HasFreshnessText);

        Assert.AreEqual(CardRuntimeStatus.Offline, offline.Status);
        Assert.IsTrue(offline.IsStateVisible);
        Assert.IsTrue(offline.IsContentVisible);
        Assert.IsTrue(offline.HasFreshnessText);

        Assert.AreEqual(CardRuntimeStatus.Error, errorStale.Status);
        Assert.IsTrue(errorStale.IsCritical);
        Assert.IsTrue(errorStale.IsContentVisible);
        Assert.IsTrue(errorStale.HasFreshnessText);
    }

    [TestMethod(DisplayName = "UT-CARD-061 [CRD-001] State and content visibility follow the status contract")]
    public void StateAndContentVisibilityFollowTheStatusContract()
    {
        var expected = new Dictionary<CardRuntimeStatus, (bool State, bool Content)>
        {
            [CardRuntimeStatus.Unknown] = (true, false),
            [CardRuntimeStatus.Loading] = (true, false),
            [CardRuntimeStatus.Ready] = (false, true),
            [CardRuntimeStatus.Empty] = (true, false),
            [CardRuntimeStatus.Stale] = (true, true),
            [CardRuntimeStatus.Offline] = (true, true),
            [CardRuntimeStatus.PermissionRequired] = (true, false),
            [CardRuntimeStatus.Unavailable] = (true, false),
            [CardRuntimeStatus.Error] = (true, true),
            [CardRuntimeStatus.Disabled] = (true, false),
        };

        foreach ((CardRuntimeStatus status, (bool State, bool Content) values)
                 in expected)
        {
            CardRuntimeStatusPresentation presentation = CreatePresentation(
                status,
                payload: status is CardRuntimeStatus.Stale or
                    CardRuntimeStatus.Offline or
                    CardRuntimeStatus.Error
                    ? JsonSerializer.SerializeToElement(new { value = 1 })
                    : default);

            Assert.AreEqual(values.State, presentation.IsStateVisible, status.ToString());
            Assert.AreEqual(values.Content, presentation.IsContentVisible, status.ToString());
            Assert.AreEqual(
                status == CardRuntimeStatus.Loading,
                presentation.IsLoading,
                status.ToString());
            Assert.AreEqual(
                status == CardRuntimeStatus.Error,
                presentation.IsCritical,
                status.ToString());
        }

        CardRuntimeStatusPresentation emptyError = CreatePresentation(
            CardRuntimeStatus.Error,
            payload: JsonSerializer.SerializeToElement(new { }));
        CardRuntimeStatusPresentation emptyStale = CreatePresentation(
            CardRuntimeStatus.Stale,
            payload: JsonSerializer.SerializeToElement(new { }));
        CardRuntimeStatusPresentation emptyOffline = CreatePresentation(
            CardRuntimeStatus.Offline,
            payload: JsonSerializer.SerializeToElement(new { }));
        Assert.IsFalse(emptyError.IsContentVisible);
        Assert.IsFalse(emptyStale.IsContentVisible);
        Assert.IsFalse(emptyOffline.IsContentVisible);
    }

    [TestMethod(DisplayName = "UT-CARD-062 [CRD-001] Empty, permission, unavailable and disabled states remain actionable only when allowed")]
    public void NonReadyStatesRemainActionableOnlyWhenAllowed()
    {
        string[] actions =
        [
            CardRuntimeActionIds.Retry,
            CardRuntimeActionIds.Refresh,
            CardRuntimeActionIds.Edit,
            CardRuntimeActionIds.Configure,
            CardRuntimeActionIds.OpenDiagnostics,
            CardRuntimeActionIds.Disable,
        ];

        foreach (CardRuntimeStatus status in new[]
                 {
                     CardRuntimeStatus.Empty,
                     CardRuntimeStatus.PermissionRequired,
                     CardRuntimeStatus.Unavailable,
                     CardRuntimeStatus.Disabled,
                 })
        {
            CardRuntimeStatusPresentation presentation = CreatePresentation(
                status,
                actions: actions);

            Assert.IsTrue(presentation.CanRetry, status.ToString());
            Assert.IsTrue(presentation.CanRefresh, status.ToString());
            Assert.IsTrue(presentation.CanEdit, status.ToString());
            Assert.IsTrue(presentation.CanConfigure, status.ToString());
            Assert.IsTrue(
                presentation.CanOpenDiagnostics,
                status.ToString());
            Assert.IsTrue(presentation.CanDisable, status.ToString());
        }
    }

    [TestMethod(DisplayName = "UT-CARD-063 [CRD-005] Action gates recognize only existing action identifiers")]
    public void ActionGatesRecognizeOnlyExistingActionIdentifiers()
    {
        CardRuntimeStatusPresentation presentation = CreatePresentation(
            CardRuntimeStatus.Ready,
            actions:
            [
                CardRuntimeActionIds.Refresh,
                "enable",
                "permission.allow",
                "permission.deny",
            ]);

        Assert.IsFalse(presentation.CanRetry);
        Assert.IsTrue(presentation.CanRefresh);
        Assert.IsFalse(presentation.CanEdit);
        Assert.IsFalse(presentation.CanConfigure);
        Assert.IsFalse(presentation.CanOpenDiagnostics);
        Assert.IsFalse(presentation.CanDisable);
    }

    [TestMethod(DisplayName = "UT-CARD-064 [CRD-005] Error presentation does not expose raw error code or payload text")]
    public void ErrorPresentationDoesNotExposeRawErrorCodeOrPayloadText()
    {
        CardRuntimeStatusPresentation presentation = CreatePresentation(
            CardRuntimeStatus.Error,
            payload: JsonSerializer.SerializeToElement(
                new { secret = "payload-secret" }),
            errorCode: "provider.internal-secret",
            actions:
            [
                CardRuntimeActionIds.Retry,
                CardRuntimeActionIds.OpenDiagnostics,
                CardRuntimeActionIds.Disable,
            ]);

        Assert.AreEqual(CardRuntimeStatus.Error, presentation.Status);
        Assert.IsTrue(presentation.IsCritical);
        Assert.IsTrue(presentation.CanRetry);
        Assert.IsTrue(presentation.CanOpenDiagnostics);
        Assert.IsTrue(presentation.CanDisable);
        Assert.IsFalse(presentation.Title.Contains(
            "provider.internal-secret",
            StringComparison.Ordinal));
        Assert.IsFalse(presentation.Summary.Contains(
            "provider.internal-secret",
            StringComparison.Ordinal));
        Assert.IsFalse(presentation.AutomationSummary.Contains(
            "provider.internal-secret",
            StringComparison.Ordinal));
        Assert.IsFalse(presentation.AutomationSummary.Contains(
            "payload-secret",
            StringComparison.Ordinal));
    }

    [TestMethod(DisplayName = "UT-CARD-065 [CRD-002/NFR-I18N] HasPayload recognizes non-empty JSON values and resolver output is deterministic")]
    public void HasPayloadRecognizesNonEmptyJsonValuesAndResolverOutputIsDeterministic()
    {
        JsonElement[] payloads =
        [
            JsonSerializer.SerializeToElement(new { value = 1 }),
            JsonSerializer.SerializeToElement(SingleItemPayload),
            JsonSerializer.SerializeToElement(0),
            JsonSerializer.SerializeToElement(false),
            JsonSerializer.SerializeToElement(""),
        ];

        foreach (JsonElement payload in payloads)
        {
            CardRuntimeStatusPresentation presentation = CreatePresentation(
                CardRuntimeStatus.Error,
                payload: payload,
                resolver: key => $"localized:{key}");

            Assert.IsTrue(presentation.IsContentVisible);
            StringAssert.StartsWith(
                presentation.Title,
                "localized:");
            StringAssert.StartsWith(
                presentation.Summary,
                "localized:");
        }

        CardRuntimeStatusPresentation emptyObject = CreatePresentation(
            CardRuntimeStatus.Error,
            payload: JsonSerializer.SerializeToElement(new { }));
        CardRuntimeStatusPresentation nullPayload = CreatePresentation(
            CardRuntimeStatus.Error,
            payload: JsonSerializer.SerializeToElement<object?>(null));

        Assert.IsFalse(emptyObject.IsContentVisible);
        Assert.IsFalse(nullPayload.IsContentVisible);
    }

    private static CardRuntimeStatusPresentation CreatePresentation(
        CardRuntimeStatus status,
        CardRuntimeFreshness freshness = CardRuntimeFreshness.Fresh,
        JsonElement payload = default,
        IEnumerable<string>? actions = null,
        string? errorCode = null,
        Func<string, string>? resolver = null,
        Func<DateTimeOffset, string>? timestampFormatter = null)
    {
        CardRuntimeSnapshot snapshot = new(
            "test.instance",
            "test.card",
            schemaVersion: 1,
            sequence: 1,
            Timestamp,
            freshness,
            status,
            payload,
            actions,
            errorCode);
        return CardRuntimeStatusPresentation.Create(
            snapshot,
            resolver,
            timestampFormatter);
    }
}
