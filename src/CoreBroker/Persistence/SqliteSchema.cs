namespace WinWidgetBoard.CoreBroker.Persistence;

public static class SqliteSchema
{
    public static IReadOnlyList<SqliteMigration> Migrations { get; } =
        new[]
        {
            new SqliteMigration(
                1,
                "initial-core-storage",
                """
                CREATE TABLE IF NOT EXISTS card_definitions (
                    card_type_id TEXT NOT NULL PRIMARY KEY,
                    display_name_key TEXT NOT NULL,
                    description_key TEXT NOT NULL,
                    version TEXT NOT NULL,
                    source TEXT NOT NULL CHECK (source IN ('builtin', 'brokered-plugin', 'full-trust-plugin')),
                    settings_schema_version INTEGER NOT NULL CHECK (settings_schema_version > 0),
                    ui_schema_version INTEGER NOT NULL CHECK (ui_schema_version > 0),
                    required_providers_json TEXT NOT NULL DEFAULT '[]',
                    required_permissions_json TEXT NOT NULL DEFAULT '[]',
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS card_instances (
                    instance_id TEXT NOT NULL PRIMARY KEY,
                    card_type_id TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
                    size_id TEXT NOT NULL CHECK (length(size_id) > 0),
                    settings_revision INTEGER NOT NULL DEFAULT 0 CHECK (settings_revision >= 0),
                    settings_json TEXT NOT NULL DEFAULT '{}',
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    FOREIGN KEY (card_type_id) REFERENCES card_definitions(card_type_id) ON DELETE RESTRICT
                );

                CREATE TABLE IF NOT EXISTS layouts (
                    layout_id TEXT NOT NULL PRIMARY KEY,
                    display_id TEXT NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                    small_columns INTEGER NOT NULL DEFAULT 2 CHECK (small_columns = 2),
                    normal_columns INTEGER NOT NULL DEFAULT 4 CHECK (normal_columns = 4),
                    wide_columns INTEGER NOT NULL DEFAULT 6 CHECK (wide_columns = 6),
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS layout_items (
                    layout_id TEXT NOT NULL,
                    instance_id TEXT NOT NULL,
                    order_index INTEGER NOT NULL CHECK (order_index >= 0),
                    column_span INTEGER NOT NULL CHECK (column_span > 0),
                    row_span INTEGER NOT NULL CHECK (row_span > 0),
                    preferred_column INTEGER NULL CHECK (preferred_column IS NULL OR preferred_column >= 0),
                    PRIMARY KEY (layout_id, instance_id),
                    FOREIGN KEY (layout_id) REFERENCES layouts(layout_id) ON DELETE CASCADE,
                    FOREIGN KEY (instance_id) REFERENCES card_instances(instance_id) ON DELETE CASCADE,
                    UNIQUE (layout_id, order_index)
                );

                CREATE TABLE IF NOT EXISTS notes (
                    note_id TEXT NOT NULL PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    body TEXT NOT NULL DEFAULT '',
                    body_format TEXT NOT NULL DEFAULT 'plain-text' CHECK (body_format IN ('plain-text', 'markdown')),
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS todos (
                    todo_id TEXT NOT NULL PRIMARY KEY,
                    title TEXT NOT NULL,
                    completed INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
                    due_at_utc TEXT NULL,
                    priority INTEGER NOT NULL DEFAULT 0 CHECK (priority >= 0),
                    notes TEXT NOT NULL DEFAULT '',
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS calendar_events (
                    event_id TEXT NOT NULL PRIMARY KEY,
                    title TEXT NOT NULL,
                    notes TEXT NOT NULL DEFAULT '',
                    start_at_utc TEXT NOT NULL,
                    end_at_utc TEXT NOT NULL,
                    all_day INTEGER NOT NULL DEFAULT 0 CHECK (all_day IN (0, 1)),
                    reminder_minutes INTEGER NULL CHECK (reminder_minutes IS NULL OR reminder_minutes >= 0),
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    CHECK (end_at_utc >= start_at_utc)
                );

                CREATE TABLE IF NOT EXISTS timers (
                    timer_id TEXT NOT NULL PRIMARY KEY,
                    label TEXT NOT NULL DEFAULT '',
                    state TEXT NOT NULL DEFAULT 'idle' CHECK (state IN ('idle', 'running', 'paused', 'completed', 'cancelled')),
                    target_at_utc TEXT NULL,
                    remaining_seconds INTEGER NULL CHECK (remaining_seconds IS NULL OR remaining_seconds >= 0),
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_card_instances_type ON card_instances(card_type_id);
                CREATE INDEX IF NOT EXISTS ix_layout_items_order ON layout_items(layout_id, order_index);
                CREATE INDEX IF NOT EXISTS ix_todos_due ON todos(due_at_utc);
                CREATE INDEX IF NOT EXISTS ix_calendar_events_start ON calendar_events(start_at_utc);
                CREATE INDEX IF NOT EXISTS ix_timers_state ON timers(state);
                PRAGMA user_version = 1;
                """),
            new SqliteMigration(
                2,
                "persist-logical-layout-row",
                """
                ALTER TABLE layout_items
                    ADD COLUMN preferred_row INTEGER NULL
                        CHECK (preferred_row IS NULL OR preferred_row >= 0);
                PRAGMA user_version = 2;
                """),
            new SqliteMigration(
                3,
                "persist-weather-settings",
                """
                CREATE TABLE IF NOT EXISTS weather_settings (
                    instance_id TEXT NOT NULL PRIMARY KEY,
                    label TEXT NOT NULL CHECK (length(label) > 0 AND length(label) <= 80),
                    latitude REAL NOT NULL CHECK (latitude >= -90 AND latitude <= 90),
                    longitude REAL NOT NULL CHECK (longitude >= -180 AND longitude <= 180),
                    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_weather_settings_revision
                    ON weather_settings(revision);
                PRAGMA user_version = 3;
                """),
            // The two item lists are stored as JSON text rather than as a child table. They
            // are short, ordered, and only ever read or written whole, so a child table would
            // buy nothing but an ordering column and a join.
            new SqliteMigration(
                4,
                "persist-system-monitor-settings",
                """
                CREATE TABLE IF NOT EXISTS sysmon_settings (
                    instance_id TEXT NOT NULL PRIMARY KEY,
                    card_items TEXT NOT NULL,
                    entry_items TEXT NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_sysmon_settings_revision
                    ON sysmon_settings(revision);
                PRAGMA user_version = 4;
                """),
            // Empty keeps the previous behaviour - every usable adapter summed - so existing
            // rows need no backfill and an upgrade changes nothing until the user chooses.
            new SqliteMigration(
                5,
                "persist-system-monitor-network-source",
                """
                ALTER TABLE sysmon_settings
                    ADD COLUMN network_interface_id TEXT NOT NULL DEFAULT '';
                PRAGMA user_version = 5;
                """),
            // Only the user's choice of which vendors to count. No usage figure and nothing
            // read out of a session transcript is ever persisted (ADR-0030). The vendor list
            // is JSON text for the same reason the monitor's item lists are: short, ordered,
            // and only ever read or written whole.
            new SqliteMigration(
                6,
                "persist-token-usage-vendor-selection",
                """
                CREATE TABLE IF NOT EXISTS token_usage_settings (
                    instance_id TEXT NOT NULL PRIMARY KEY,
                    enabled_vendors TEXT NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_token_usage_settings_revision
                    ON token_usage_settings(revision);
                PRAGMA user_version = 6;
                """),
            // Existing weather rows opt into the newly approved automatic-location mode.
            // Windows permission is never persisted here; only the user's mode choice is.
            new SqliteMigration(
                7,
                "persist-weather-device-location-mode",
                """
                ALTER TABLE weather_settings
                    ADD COLUMN use_device_location INTEGER NOT NULL DEFAULT 1
                        CHECK (use_device_location IN (0, 1));
                PRAGMA user_version = 7;
                """),
        };
}
