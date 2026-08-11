using System.Text.Json;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class ProviderBackoffPolicyTests
{
    private static readonly DateTimeOffset DeadlineUtc =
        new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-CARD-071 [CRD-001/CRD-002/NFR-PERF-007] " +
        "Scheduled descriptor validates cadence, timeout and backoff guards")]
    public void ScheduledDescriptorValidatesCadenceTimeoutAndBackoffGuards()
    {
        ScheduledProviderDescriptor descriptor = CreateDescriptor();

        Assert.AreEqual("app.test.provider", descriptor.ProviderId);
        Assert.AreEqual("test.current", descriptor.Capability);
        Assert.AreEqual(TimeSpan.FromSeconds(10), descriptor.MinimumInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(20), descriptor.HiddenInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(30), descriptor.PowerSaverInterval);
        Assert.AreEqual(
            ProviderNetworkState.Unknown,
            EnumValue<ProviderNetworkState>(0));
        Assert.AreEqual(
            ProviderPowerState.Unknown,
            EnumValue<ProviderPowerState>(0));

        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScheduledProviderDescriptor(
                " ",
                descriptor.Capability,
                descriptor.MinimumInterval,
                descriptor.VisibleInterval,
                descriptor.HiddenInterval,
                descriptor.PowerSaverInterval,
                descriptor.RequestTimeout,
                descriptor.RequiresNetwork,
                descriptor.SupportsManualRefresh,
                descriptor.ManualRefreshMinimumInterval,
                descriptor.BackoffOptions));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ScheduledProviderDescriptor(
                descriptor.ProviderId,
                descriptor.Capability,
                descriptor.MinimumInterval,
                TimeSpan.FromSeconds(9),
                descriptor.HiddenInterval,
                descriptor.PowerSaverInterval,
                descriptor.RequestTimeout,
                descriptor.RequiresNetwork,
                descriptor.SupportsManualRefresh,
                descriptor.ManualRefreshMinimumInterval,
                descriptor.BackoffOptions));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ScheduledProviderDescriptor(
                descriptor.ProviderId,
                descriptor.Capability,
                descriptor.MinimumInterval,
                descriptor.VisibleInterval,
                descriptor.HiddenInterval,
                descriptor.PowerSaverInterval,
                TimeSpan.Zero,
                descriptor.RequiresNetwork,
                descriptor.SupportsManualRefresh,
                descriptor.ManualRefreshMinimumInterval,
                descriptor.BackoffOptions));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ProviderBackoffOptions(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                jitterRatio: 0.1));
    }

    [TestMethod(DisplayName =
        "UT-CARD-072 [CRD-001/CRD-002] " +
        "Provider request and result own JSON and normalize metadata")]
    public void ProviderRequestAndResultOwnJsonAndNormalizeMetadata()
    {
        ProviderRequestKey key = CreateKey();
        ProviderRefreshRequest request;
        ProviderRefreshResult result;
        using (JsonDocument requestDocument = JsonDocument.Parse(
                   "{\"city\":{\"name\":\"Singapore\"},\"units\":\"metric\"}"))
        using (JsonDocument resultDocument = JsonDocument.Parse(
                   "{\"temperature\":31.2,\"secret\":\"detached\"}"))
        {
            request = new(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                key,
                DeadlineUtc,
                requestDocument.RootElement,
                generation: 7);
            result = new(
                request.RequestId,
                (ProviderRefreshResultKind)999,
                resultDocument.RootElement,
                DeadlineUtc.AddSeconds(-1),
                DeadlineUtc.AddMinutes(15),
                "provider.failed",
                TimeSpan.FromSeconds(12));
        }

        Assert.AreEqual(request.RequestId, result.RequestId);
        Assert.AreEqual(7L, request.Generation);
        Assert.AreEqual("Singapore", request.Arguments
            .GetProperty("city").GetProperty("name").GetString());
        Assert.AreEqual("metric", request.Arguments.GetProperty("units").GetString());
        Assert.AreEqual(31.2, result.Payload
            .GetProperty("temperature").GetDouble(), 0.0001);
        Assert.AreEqual("detached", result.Payload.GetProperty("secret").GetString());
        Assert.AreEqual(
            ProviderRefreshResultKind.Failed,
            result.Kind);
        Assert.AreEqual(DeadlineUtc.AddMinutes(15), result.ValidUntilUtc);
        Assert.AreEqual("provider.failed", result.ErrorCode);
        Assert.AreEqual(TimeSpan.FromSeconds(12), result.RetryAfter);

        ProviderRefreshResult defaultKindResult = new(
            request.RequestId,
            default,
            default,
            DeadlineUtc);
        Assert.AreEqual(
            ProviderRefreshResultKind.Failed,
            defaultKindResult.Kind);
        Assert.AreNotEqual(
            ProviderRefreshResultKind.Success,
            defaultKindResult.Kind);

        ProviderManualRefreshOutcome defaultManualOutcome =
            EnumValue<ProviderManualRefreshOutcome>(0);
        Assert.AreEqual(
            ProviderManualRefreshOutcome.Unknown,
            defaultManualOutcome);
        Assert.AreNotEqual(
            ProviderManualRefreshOutcome.Started,
            defaultManualOutcome);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ProviderRefreshRequest(
                request.RequestId,
                null!,
                DeadlineUtc,
                request.Arguments,
                request.Generation));
    }

    [TestMethod(DisplayName =
        "UT-CARD-073 [NFR-PERF-007] " +
        "Provider request keys compare stable source and argument identity")]
    public void ProviderRequestKeysCompareStableSourceAndArgumentIdentity()
    {
        ProviderRequestKey first = CreateKey();
        ProviderRequestKey same = CreateKey();
        ProviderRequestKey differentArguments = new(
            first.ProviderId,
            first.Capability,
            first.DataSourceKey,
            "sha256:arguments-b");

        Assert.AreEqual(first, same);
        Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(first, differentArguments);

        ProviderRequestKey? defaultKey = default;
        Assert.IsNull(defaultKey);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ProviderRefreshRequest(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                defaultKey!,
                DeadlineUtc,
                JsonSerializer.SerializeToElement(new { value = 1 }),
                generation: 0));

        Assert.ThrowsExactly<ArgumentException>(() =>
            new ProviderRequestKey(
                first.ProviderId,
                first.Capability,
                first.DataSourceKey,
                ""));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ProviderRequestKey(
                first.ProviderId + "\n",
                first.Capability,
                first.DataSourceKey,
                first.ArgumentsFingerprint));
    }

    [TestMethod(DisplayName =
        "UT-CARD-074 [CRD-002/NFR-PERF-007] " +
        "Backoff uses exponential delay, cap and overflow-safe arithmetic")]
    public void BackoffUsesExponentialDelayCapAndOverflowSafeArithmetic()
    {
        ProviderBackoffOptions options = new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(8),
            jitterRatio: 0);

        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            ProviderBackoffPolicy.CalculateDelay(options, 1, 0));
        Assert.AreEqual(
            TimeSpan.FromSeconds(2),
            ProviderBackoffPolicy.CalculateDelay(options, 2, 0));
        Assert.AreEqual(
            TimeSpan.FromSeconds(4),
            ProviderBackoffPolicy.CalculateDelay(options, 3, 0));
        Assert.AreEqual(
            TimeSpan.FromSeconds(8),
            ProviderBackoffPolicy.CalculateDelay(options, 4, 0));
        Assert.AreEqual(
            TimeSpan.FromSeconds(8),
            ProviderBackoffPolicy.CalculateDelay(options, int.MaxValue, 0));
    }

    [TestMethod(DisplayName =
        "UT-CARD-075 [CRD-002] " +
        "Backoff jitter is deterministic, symmetric and bounded")]
    public void BackoffJitterIsDeterministicSymmetricAndBounded()
    {
        ProviderBackoffOptions options = new(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1),
            jitterRatio: 0.2);

        TimeSpan low = ProviderBackoffPolicy.CalculateDelay(options, 1, 0);
        TimeSpan center = ProviderBackoffPolicy.CalculateDelay(options, 1, 0.5);
        TimeSpan high = ProviderBackoffPolicy.CalculateDelay(options, 1, 0.999999);
        TimeSpan repeated = ProviderBackoffPolicy.CalculateDelay(options, 1, 0.999999);

        Assert.AreEqual(TimeSpan.FromSeconds(8), low);
        Assert.AreEqual(TimeSpan.FromSeconds(10), center);
        Assert.AreEqual(high, repeated);
        Assert.IsTrue(low <= center);
        Assert.IsTrue(center <= high);
        Assert.IsTrue(high < TimeSpan.FromSeconds(12));
    }

    [TestMethod(DisplayName =
        "UT-CARD-076 [CRD-002/CRD-005] " +
        "Backoff honors retry-after lower bound and rejects invalid inputs")]
    public void BackoffHonorsRetryAfterLowerBoundAndRejectsInvalidInputs()
    {
        ProviderBackoffOptions options = new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10),
            jitterRatio: 0);

        Assert.AreEqual(
            TimeSpan.FromSeconds(5),
            ProviderBackoffPolicy.CalculateDelay(
                options,
                consecutiveFailures: 1,
                jitterUnit: 0.5,
                retryAfter: TimeSpan.FromSeconds(5)));
        Assert.AreEqual(
            TimeSpan.FromSeconds(10),
            ProviderBackoffPolicy.CalculateDelay(
                options,
                consecutiveFailures: 1,
                jitterUnit: 0.5,
                retryAfter: TimeSpan.FromMinutes(2)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProviderBackoffPolicy.CalculateDelay(options, 0, 0.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProviderBackoffPolicy.CalculateDelay(options, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProviderBackoffPolicy.CalculateDelay(
                options,
                1,
                0.5,
                TimeSpan.FromSeconds(-1)));
    }

    private static ProviderRequestKey CreateKey() => new(
        "app.test.provider",
        "test.current",
        "weather:singapore",
        "sha256:arguments-a");

    private static ScheduledProviderDescriptor CreateDescriptor() =>
        new(
            "app.test.provider",
            "test.current",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            requiresNetwork: true,
            supportsManualRefresh: true,
            TimeSpan.FromSeconds(15),
            new ProviderBackoffOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                jitterRatio: 0.2));

    private static TEnum EnumValue<TEnum>(int value)
        where TEnum : struct, Enum =>
        (TEnum)Enum.ToObject(typeof(TEnum), value);
}
