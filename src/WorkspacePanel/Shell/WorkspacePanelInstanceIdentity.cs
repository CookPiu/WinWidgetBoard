namespace WinWidgetBoard.WorkspacePanel.Shell;

internal static class WorkspacePanelInstanceIdentity
{
    internal const string ProductionMutexName =
        "Local\\WinWidgetBoard.WorkspacePanel";
    internal const string AcceptanceTestSwitch = "--acceptance-test";
    internal const string TestInstanceIdSwitch = "--test-instance-id";

    private static readonly string[] IsolationModes = [
        AcceptanceTestSwitch,
        "--smoke-test",
        "--broker-smoke-test",
    ];

    internal static string ResolveMutexName(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        bool isolationAllowed = IsolationModes.Any(mode =>
            arguments.Any(argument =>
                string.Equals(
                    argument,
                    mode,
                    StringComparison.OrdinalIgnoreCase)));
        if (!isolationAllowed)
        {
            return ProductionMutexName;
        }

        int idIndex = -1;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    TestInstanceIdSwitch,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (idIndex >= 0)
            {
                throw new ArgumentException(
                    $"{TestInstanceIdSwitch} must not be repeated.",
                    nameof(arguments));
            }

            idIndex = index;
        }

        if (idIndex < 0)
        {
            return ProductionMutexName;
        }

        if (idIndex + 1 >= arguments.Count ||
            !Guid.TryParse(arguments[idIndex + 1], out Guid instanceId))
        {
            throw new ArgumentException(
                $"{TestInstanceIdSwitch} requires a GUID value.",
                nameof(arguments));
        }

        return $"{ProductionMutexName}.Acceptance.{instanceId:N}";
    }
}
