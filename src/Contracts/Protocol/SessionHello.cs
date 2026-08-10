namespace WinWidgetBoard.Contracts.Protocol;

public static class SessionHelloContract
{
    public const string Method = "session.hello";
    public const string LauncherClientType = "launcher-host";
    public const string WorkspacePanelClientType = "workspace-panel";
    public const string PluginHostClientType = "plugin-host";
}

public sealed record ProtocolVersionRange
{
    public string Min { get; init; } = ProtocolConstants.CurrentVersion;

    public string Max { get; init; } = ProtocolConstants.CurrentVersion;
}

public sealed record SessionHelloRequest
{
    public string ClientType { get; init; } = string.Empty;

    public string ClientVersion { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public string Architecture { get; init; } = string.Empty;

    public string SessionToken { get; init; } = string.Empty;

    public ProtocolVersionRange? SupportedProtocolRange { get; init; }
}

public sealed record SessionHelloResponse
{
    public string AcceptedProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    public string ServerVersion { get; init; } = string.Empty;

    public Guid SessionId { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public int MaxMessageBytes { get; init; } = ProtocolConstants.MaxMessageBytes;
}
