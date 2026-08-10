namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed record SqliteMigration
{
    public SqliteMigration(int version, string name, string commandText)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Migration version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        Version = version;
        Name = name;
        CommandText = commandText;
    }

    public int Version { get; }

    public string Name { get; }

    public string CommandText { get; }
}
