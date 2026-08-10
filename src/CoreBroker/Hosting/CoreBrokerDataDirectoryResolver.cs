namespace WinWidgetBoard.CoreBroker.Hosting;

public static class CoreBrokerDataDirectoryResolver
{
    private const string AcceptanceArgument = "--acceptance-test";
    private const string DataDirectoryArgument = "--data-directory";

    public static bool TryResolve(
        IReadOnlyList<string> arguments,
        string localApplicationData,
        string temporaryDirectory,
        out string? dataDirectory,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);

        dataDirectory = null;
        error = null;
        bool acceptanceTest = arguments.Any(argument =>
            string.Equals(
                argument,
                AcceptanceArgument,
                StringComparison.OrdinalIgnoreCase));
        if (!TryReadSingleValue(
                arguments,
                DataDirectoryArgument,
                out string? requestedDirectory,
                out error))
        {
            return false;
        }

        if (requestedDirectory is null)
        {
            dataDirectory = Path.Combine(
                Path.GetFullPath(localApplicationData),
                "WinWidgetBoard");
            return true;
        }
        if (!acceptanceTest)
        {
            error =
                $"{DataDirectoryArgument} is allowed only with {AcceptanceArgument}";
            return false;
        }
        if (!Path.IsPathFullyQualified(requestedDirectory))
        {
            error = $"{DataDirectoryArgument} must be an absolute path";
            return false;
        }

        string normalizedTemporaryDirectory = EnsureTrailingSeparator(
            Path.GetFullPath(temporaryDirectory));
        string normalizedRequestedDirectory =
            Path.GetFullPath(requestedDirectory);
        if (!normalizedRequestedDirectory.StartsWith(
                normalizedTemporaryDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                $"{DataDirectoryArgument} must stay within the system temporary directory";
            return false;
        }

        dataDirectory = normalizedRequestedDirectory;
        return true;
    }

    private static bool TryReadSingleValue(
        IReadOnlyList<string> arguments,
        string name,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (value is not null)
            {
                error = $"{name} must not be repeated";
                return false;
            }
            if (index == arguments.Count - 1 ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{name} requires a directory value";
                return false;
            }

            value = arguments[index + 1];
            index++;
        }

        return true;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ||
        path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
