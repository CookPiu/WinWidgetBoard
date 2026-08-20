using System.Diagnostics;
using System.Globalization;

namespace WinWidgetBoard.WorkspacePanel.Shell;

/// <summary>
/// Opt-in startup timing marks, measured from the OS process start so the numbers include
/// runtime and framework initialisation that happens before any of our code runs. Disabled
/// unless explicitly switched on, and it writes to stderr only - there is no product
/// behaviour attached to it.
/// </summary>
internal static class StartupTrace
{
    internal const string EnableSwitch = "--startup-trace";
    internal const string EnableEnvironmentVariable = "WINWIDGETBOARD_STARTUP_TRACE";

    private static readonly object Gate = new();
    private static bool _enabled;
    private static DateTime _processStartUtc;
    private static Action<string>? _sink;

    internal static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }
    }

    internal static bool ShouldEnable(
        IReadOnlyList<string>? arguments,
        string? environmentValue)
    {
        if (arguments is not null &&
            arguments.Any(argument => string.Equals(
                argument,
                EnableSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(environmentValue) &&
            !string.Equals(environmentValue, "0", StringComparison.Ordinal);
    }

    internal static void Initialize(
        IReadOnlyList<string>? arguments,
        string? environmentValue,
        DateTime processStartUtc,
        Action<string>? sink = null)
    {
        lock (Gate)
        {
            _enabled = ShouldEnable(arguments, environmentValue);
            _processStartUtc = processStartUtc;
            _sink = sink;
        }
    }

    internal static void Initialize()
    {
        DateTime processStartUtc;
        try
        {
            processStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or PlatformNotSupportedException)
        {
            processStartUtc = DateTime.UtcNow;
        }

        Initialize(
            Environment.GetCommandLineArgs(),
            Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
            processStartUtc);
    }

    internal static void Mark(string stage)
    {
        double elapsedMilliseconds;
        Action<string>? sink;
        lock (Gate)
        {
            if (!_enabled)
            {
                return;
            }

            elapsedMilliseconds = (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;
            sink = _sink;
        }

        string line = string.Format(
            CultureInfo.InvariantCulture,
            "STARTUP-TRACE {0} {1:F1}",
            stage,
            elapsedMilliseconds);
        if (sink is not null)
        {
            sink(line);
            return;
        }

        Console.Error.WriteLine(line);
    }
}
