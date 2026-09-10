using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The rules around choosing a weather source and storing its credential. These are the parts
/// a mistake in would either send a key somewhere it does not belong or silently change what
/// an existing installation reads from.
/// </summary>
[TestClass]
public sealed class WeatherProviderSelectionTests
{
    [TestMethod(DisplayName =
        "UT-WEA-088 [WEA-001/CRD-002] Silence still means Open-Meteo for an older client or row")]
    public void MissingProviderKeepsTheOriginalSource()
    {
        Assert.IsTrue(WeatherSettingsContract.TryNormalizeProviderId(null, out string absent));
        Assert.AreEqual(WeatherSettingsContract.OpenMeteoProviderId, absent);

        Assert.IsTrue(
            WeatherSettingsContract.TryNormalizeProviderId("qweather", out string chosen));
        Assert.AreEqual(WeatherSettingsContract.QWeatherProviderId, chosen);

        // An unknown token is refused rather than falling back: a build that does not know a
        // source must not quietly read from a different one than the row names.
        Assert.IsFalse(WeatherSettingsContract.TryNormalizeProviderId("caiyun", out _));

        Assert.IsTrue(
            WeatherSettingsContract.RequiresApiCredential(
                WeatherSettingsContract.QWeatherProviderId));
        Assert.IsFalse(
            WeatherSettingsContract.RequiresApiCredential(
                WeatherSettingsContract.OpenMeteoProviderId));
    }

    [TestMethod(DisplayName =
        "UT-WEA-089 [WEA-001/SEC-004] An API host reduces to a bare host or is refused")]
    public void ApiHostNormalizationKeepsOnlyTheHost()
    {
        Assert.IsTrue(
            WeatherSettingsContract.TryNormalizeApiHost(
                "  ABCdefg.QWeatherAPI.com ",
                out string trimmed));
        Assert.AreEqual("abcdefg.qweatherapi.com", trimmed);

        // The console shows the host inside a URL, so one is accepted - but only its host
        // survives, because a path or a port would be a different destination from the one
        // the allow-list checked.
        Assert.IsTrue(
            WeatherSettingsContract.TryNormalizeApiHost(
                "https://abcdefg.qweatherapi.com",
                out string fromUrl));
        Assert.AreEqual("abcdefg.qweatherapi.com", fromUrl);

        Assert.IsFalse(
            WeatherSettingsContract.TryNormalizeApiHost(
                "https://user:pass@abcdefg.qweatherapi.com",
                out _));
        Assert.IsFalse(
            WeatherSettingsContract.TryNormalizeApiHost(
                "https://abcdefg.qweatherapi.com:8443",
                out _));
        Assert.IsFalse(
            WeatherSettingsContract.TryNormalizeApiHost(
                "http://abcdefg.qweatherapi.com",
                out _));
        Assert.IsFalse(WeatherSettingsContract.TryNormalizeApiHost("no-dot", out _));
        Assert.IsFalse(WeatherSettingsContract.TryNormalizeApiHost("-bad.example.com", out _));

        // Null and empty are "no host", which is what a keyless source has.
        Assert.IsTrue(WeatherSettingsContract.TryNormalizeApiHost(null, out string none));
        Assert.AreEqual(string.Empty, none);
    }

    [TestMethod(DisplayName =
        "UT-WEA-090 [WEA-001/SEC-004] Only the vendor's own domains may receive the key")]
    public void OnlyVendorHostsAreAllowed()
    {
        Assert.IsTrue(
            WeatherSettingsContract.IsAllowedQWeatherHost("abcdefg.qweatherapi.com"));
        Assert.IsTrue(WeatherSettingsContract.IsAllowedQWeatherHost("devapi.qweather.com"));

        // A suffix match alone is not enough: the look-alike below ends in a different
        // registrable domain and would take the credential off-vendor.
        Assert.IsFalse(
            WeatherSettingsContract.IsAllowedQWeatherHost("qweatherapi.com.example.net"));
        Assert.IsFalse(WeatherSettingsContract.IsAllowedQWeatherHost("collector.example.com"));
        // The bare domain has no account prefix, so it is not an account's API host either.
        Assert.IsFalse(WeatherSettingsContract.IsAllowedQWeatherHost("qweatherapi.com"));
        Assert.IsFalse(WeatherSettingsContract.IsAllowedQWeatherHost(null));
    }

    [TestMethod(DisplayName =
        "UT-WEA-091 [WEA-001/SEC-004] An API key must be something a header can carry")]
    public void ApiKeyShapeIsChecked()
    {
        Assert.IsTrue(WeatherSettingsContract.IsValidApiKey("ABCD1234efgh"));
        Assert.IsFalse(WeatherSettingsContract.IsValidApiKey(null));
        Assert.IsFalse(WeatherSettingsContract.IsValidApiKey(string.Empty));
        // A newline in a header value is header injection, not a typo to tolerate.
        Assert.IsFalse(WeatherSettingsContract.IsValidApiKey("ABCD\r\nX-Injected: 1"));
        Assert.IsFalse(WeatherSettingsContract.IsValidApiKey("has space"));
        Assert.IsFalse(
            WeatherSettingsContract.IsValidApiKey(
                new string('k', WeatherSettingsContract.MaxApiKeyLength + 1)));
    }

    [TestMethod(DisplayName =
        "UT-WEA-092 [WEA-001/SEC-004] A stored credential round-trips and unreadable ciphertext reads as absent")]
    public void ProtectedCredentialRoundTrips()
    {
        const string secret = "ABCD1234efgh5678";

        string protectedValue = LocalDataProtector.Protect(secret);

        // Whatever lands in the database must not be the key.
        Assert.AreNotEqual(secret, protectedValue);
        Assert.IsFalse(protectedValue.Contains(secret, StringComparison.Ordinal));
        Assert.AreEqual(secret, LocalDataProtector.TryUnprotect(protectedValue));

        // A row copied from another account or another machine cannot be decrypted here. It
        // reads as "no credential" - the card then asks for one - rather than taking the
        // whole settings read down as a storage fault.
        Assert.IsNull(LocalDataProtector.TryUnprotect("not-base64!!"));
        Assert.IsNull(LocalDataProtector.TryUnprotect(Convert.ToBase64String([1, 2, 3, 4])));
        Assert.IsNull(LocalDataProtector.TryUnprotect(null));
        Assert.IsNull(LocalDataProtector.TryUnprotect(string.Empty));
    }

    [TestMethod(DisplayName =
        "UT-WEA-093 [WEA-001/CRD-002] The settings record exposes credential presence, never the key")]
    public void SettingsRecordReportsPresenceOnly()
    {
        var configured = new WeatherSettingsRecord(
            WeatherSettingsContract.DefaultInstanceId,
            "Beijing",
            39.92,
            116.41,
            useDeviceLocation: false,
            WeatherSettingsContract.MetricUnitSystem,
            revision: 3,
            updatedAtUtc: null,
            WeatherSettingsContract.QWeatherProviderId,
            "ABCdefg.qweatherapi.com",
            "ABCD1234");

        Assert.AreEqual("abcdefg.qweatherapi.com", configured.ApiHost);
        Assert.IsTrue(configured.HasApiCredential);

        WeatherSettingsRecord keyless = new(
            WeatherSettingsContract.DefaultInstanceId,
            "Singapore",
            1.3521,
            103.8198,
            useDeviceLocation: true,
            WeatherSettingsContract.MetricUnitSystem,
            revision: 0,
            updatedAtUtc: null);

        // The defaults reproduce the single-source behaviour exactly, so an upgraded row
        // that has never been touched keeps meaning what it meant.
        Assert.AreEqual(WeatherSettingsContract.OpenMeteoProviderId, keyless.ProviderId);
        Assert.AreEqual(string.Empty, keyless.ApiHost);
        Assert.IsFalse(keyless.HasApiCredential);

        Assert.ThrowsExactly<ArgumentException>(() => new WeatherSettingsRecord(
            WeatherSettingsContract.DefaultInstanceId,
            "Beijing",
            39.92,
            116.41,
            useDeviceLocation: false,
            WeatherSettingsContract.MetricUnitSystem,
            revision: 0,
            updatedAtUtc: null,
            "caiyun"));
    }
}
