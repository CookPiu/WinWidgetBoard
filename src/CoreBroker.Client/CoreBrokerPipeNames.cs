namespace WinWidgetBoard.CoreBroker.Client;

public static class CoreBrokerPipeNames
{
    public const string Production = "WinWidgetBoard.CoreBroker.v1";
    public const string SessionTokenEnvironmentVariable =
        "WINWIDGETBOARD_COREBROKER_SESSION_TOKEN";

    public static string CreateTestName() =>
        $"{Production}.test.{Guid.NewGuid():N}";
}
