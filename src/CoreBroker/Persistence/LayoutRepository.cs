using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed class LayoutRepository
{
    private const string DemoCardTypeId = "app.winwidgetboard.demo";
    private readonly SqliteRepository _repository;

    public LayoutRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _repository = new SqliteRepository(database);
    }

    public LayoutRecord? Get(string layoutId, string displayId)
    {
        ValidateLayoutIdentity(layoutId, displayId);
        LayoutRecord? header = null;
        _repository.Query(
            "SELECT layout_id, display_id, revision, updated_at_utc " +
            "FROM layouts WHERE layout_id = @id AND display_id = @display;",
            statement =>
            {
                statement.BindText("@id", layoutId);
                statement.BindText("@display", displayId);
            },
            statement => header = new LayoutRecord(
                statement.ReadText(0) ?? throw new SqliteException(1, "layouts.layout_id is NULL."),
                statement.ReadText(1) ?? throw new SqliteException(1, "layouts.display_id is NULL."),
                statement.ReadInt(2),
                Array.Empty<LayoutItemRecord>(),
                statement.ReadText(3) ?? throw new SqliteException(1, "layouts.updated_at_utc is NULL.")));

        if (header is null)
        {
            return null;
        }

        var items = new List<LayoutItemRecord>();
        _repository.Query(
            "SELECT li.instance_id, li.order_index, li.column_span, li.row_span, " +
            "ci.size_id, li.preferred_column, li.preferred_row " +
            "FROM layout_items AS li " +
            "INNER JOIN card_instances AS ci ON ci.instance_id = li.instance_id " +
            "WHERE li.layout_id = @id ORDER BY li.order_index ASC;",
            statement => statement.BindText("@id", layoutId),
            statement => items.Add(ReadItem(statement)));

        return header with { Items = items.AsReadOnly() };
    }

    public LayoutRecord Save(
        string layoutId,
        string displayId,
        int expectedRevision,
        IReadOnlyList<LayoutItemRecord> items,
        DateTimeOffset? nowUtc = null)
    {
        ValidateLayoutIdentity(layoutId, displayId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ValidateItems(items);

        LayoutHeader? current = GetHeader(layoutId);
        if (current is not null &&
            !string.Equals(current.DisplayId, displayId, StringComparison.Ordinal))
        {
            throw new LayoutRevisionConflictException(
                layoutId,
                expectedRevision,
                current.Revision);
        }

        int actualRevision = current?.Revision ?? 0;
        if (actualRevision != expectedRevision)
        {
            throw new LayoutRevisionConflictException(
                layoutId,
                expectedRevision,
                actualRevision);
        }

        int nextRevision = checked(actualRevision + 1);
        string updatedAtUtc = NoteRecord.FormatTimestamp(
            nowUtc ?? DateTimeOffset.UtcNow);

        using SqliteTransaction transaction = _repository.BeginTransaction();
        if (current is null)
        {
            _repository.Execute(
                "INSERT INTO layouts (layout_id, display_id, revision, updated_at_utc) " +
                "VALUES (@id, @display, @revision, @updated);",
                statement =>
                {
                    statement.BindText("@id", layoutId);
                    statement.BindText("@display", displayId);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                });
        }
        else
        {
            int changes = _repository.Execute(
                "UPDATE layouts SET revision = @revision, updated_at_utc = @updated " +
                "WHERE layout_id = @id AND display_id = @display AND revision = @expected;",
                statement =>
                {
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                    statement.BindText("@id", layoutId);
                    statement.BindText("@display", displayId);
                    statement.BindInt("@expected", expectedRevision);
                });
            if (changes != 1)
            {
                throw new LayoutRevisionConflictException(
                    layoutId,
                    expectedRevision,
                    current.Revision);
            }
        }

        if (items.Count > 0)
        {
            EnsureDemoCardDefinition(updatedAtUtc);
        }

        foreach (LayoutItemRecord item in items)
        {
            UpsertCardInstance(item, updatedAtUtc);
        }

        _repository.Execute(
            "DELETE FROM layout_items WHERE layout_id = @id;",
            statement => statement.BindText("@id", layoutId));

        foreach (LayoutItemRecord item in items)
        {
            _repository.Execute(
                "INSERT INTO layout_items " +
                "(layout_id, instance_id, order_index, column_span, row_span, preferred_column, preferred_row) " +
                "VALUES (@layout, @instance, @order, @columns, @rows, @preferred, @preferred_row);",
                statement =>
                {
                    statement.BindText("@layout", layoutId);
                    statement.BindText("@instance", item.InstanceId);
                    statement.BindInt("@order", item.OrderIndex);
                    statement.BindInt("@columns", item.ColumnSpan);
                    statement.BindInt("@rows", item.RowSpan);
                    statement.BindNullableInt("@preferred", item.PreferredColumn);
                    statement.BindNullableInt("@preferred_row", item.PreferredRow);
                });
        }

        transaction.Commit();
        return new LayoutRecord(
            layoutId,
            displayId,
            nextRevision,
            items.ToArray(),
            updatedAtUtc);
    }

    private void EnsureDemoCardDefinition(string updatedAtUtc)
    {
        _repository.Execute(
            "INSERT INTO card_definitions " +
            "(card_type_id, display_name_key, description_key, version, source, " +
            "settings_schema_version, ui_schema_version, created_at_utc, updated_at_utc) " +
            "VALUES (@type, 'card.demo.name', 'card.demo.description', '0.1.0', " +
            "'builtin', 1, 1, @updated, @updated) " +
            "ON CONFLICT(card_type_id) DO NOTHING;",
            statement =>
            {
                statement.BindText("@type", DemoCardTypeId);
                statement.BindText("@updated", updatedAtUtc);
            });
    }

    private void UpsertCardInstance(LayoutItemRecord item, string updatedAtUtc)
    {
        _repository.Execute(
            "INSERT INTO card_instances " +
            "(instance_id, card_type_id, size_id, created_at_utc, updated_at_utc) " +
            "VALUES (@instance, @type, @size, @updated, @updated) " +
            "ON CONFLICT(instance_id) DO UPDATE SET " +
            "size_id = excluded.size_id, updated_at_utc = excluded.updated_at_utc;",
            statement =>
            {
                statement.BindText("@instance", item.InstanceId);
                statement.BindText("@type", DemoCardTypeId);
                statement.BindText("@size", item.SizeId);
                statement.BindText("@updated", updatedAtUtc);
            });
    }

    private LayoutHeader? GetHeader(string layoutId)
    {
        LayoutHeader? header = null;
        _repository.Query(
            "SELECT layout_id, display_id, revision " +
            "FROM layouts WHERE layout_id = @id;",
            statement => statement.BindText("@id", layoutId),
            statement => header = new LayoutHeader(
                statement.ReadText(0) ?? throw new SqliteException(1, "layouts.layout_id is NULL."),
                statement.ReadText(1) ?? throw new SqliteException(1, "layouts.display_id is NULL."),
                statement.ReadInt(2)));
        return header;
    }

    private static LayoutItemRecord ReadItem(SqliteStatement statement) =>
        new(
            statement.ReadText(0) ?? throw new SqliteException(1, "layout_items.instance_id is NULL."),
            statement.ReadInt(1),
            statement.ReadInt(2),
            statement.ReadInt(3),
            statement.ReadText(4) ?? throw new SqliteException(1, "card_instances.size_id is NULL."),
            statement.IsNull(5) ? null : statement.ReadInt(5),
            statement.IsNull(6) ? null : statement.ReadInt(6));

    private static void ValidateLayoutIdentity(string layoutId, string displayId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);
        if (layoutId.Length > LayoutContract.MaxLayoutIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(layoutId));
        }

        if (displayId.Length > LayoutContract.MaxDisplayIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(displayId));
        }
    }

    private static void ValidateItems(IReadOnlyList<LayoutItemRecord> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > LayoutContract.MaxItems)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (LayoutItemRecord item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.InstanceId);
            if (item.InstanceId.Length > LayoutContract.MaxInstanceIdLength ||
                !ids.Add(item.InstanceId) ||
                !orders.Add(item.OrderIndex) ||
                item.OrderIndex < 0 ||
                item.PreferredColumn is < 0 ||
                item.PreferredRow is < 0 ||
                !LayoutContract.TryGetDeclaredSpan(
                    item.SizeId,
                    out int declaredColumns,
                    out int declaredRows) ||
                item.ColumnSpan != declaredColumns ||
                item.RowSpan != declaredRows)
            {
                throw new ArgumentException(
                    "Layout items must have unique contiguous order, valid size spans, and non-negative grid preferences.",
                    nameof(items));
            }
        }

        if (!orders.SetEquals(Enumerable.Range(0, items.Count)))
        {
            throw new ArgumentException(
                "Layout item order must be contiguous starting at zero.",
                nameof(items));
        }
    }

    private sealed record LayoutHeader(
        string LayoutId,
        string DisplayId,
        int Revision);
}
