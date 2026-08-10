namespace WinWidgetBoard.CoreBroker.Hosting;

public static class CoreBrokerInstanceIdentity
{
    public const string ProductionMutexName =
        "Local\\WinWidgetBoard.CoreBroker";
    public const string AcceptanceTestSwitch = "--acceptance-test";
    public const string TestInstanceIdSwitch = "--test-instance-id";

    public static string ResolveMutexName(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        bool acceptanceTest = arguments.Any(argument =>
            string.Equals(
                argument,
                AcceptanceTestSwitch,
                StringComparison.OrdinalIgnoreCase));
        if (!acceptanceTest)
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
