using System.ComponentModel.DataAnnotations;
using System.Data.SQLite;
using SQliteOrm.Migrations;

namespace SQliteOrm.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sqlite-orm-migrations-{Guid.NewGuid():N}.db");
    private readonly SqliteOrm _db;

    public MigrationTests() => _db = new SqliteOrm($"Data Source={_path}");

    [Fact]
    public void First_migration_is_applied_once_and_history_persists()
    {
        var upCalls = 0;
        var migration = new TestMigration("001_CreateWidgets", builder =>
        {
            upCalls++;
            builder.CreateTable<MigrationWidget>();
        }, builder => builder.DropTable<MigrationWidget>());

        _db.Migrate(migration);
        _db.Migrate(migration);

        Assert.Equal(1, upCalls);
        Assert.Equal(1, HistoryCount());
        _db.Dispose();
        using var reopened = new SqliteOrm($"Data Source={_path}");
        reopened.Migrate(migration);
        Assert.Equal(1, upCalls);
        Assert.Equal(1, reopened.ExecuteScalar<int>("SELECT COUNT(*) FROM \"__SQliteOrmMigrations\""));
    }

    [Fact]
    public void Multiple_migrations_run_in_deterministic_id_order()
    {
        var second = new TestMigration("002_InsertLog",
            builder => builder.ExecuteSql("INSERT INTO \"MigrationLog\" (\"Value\") VALUES ('second');"));
        var first = new TestMigration("001_CreateLog",
            builder => builder.ExecuteSql("CREATE TABLE \"MigrationLog\" (\"Value\" TEXT NOT NULL);"));

        _db.Migrate(second, first);

        Assert.Equal("second", _db.ExecuteScalar<string>("SELECT \"Value\" FROM \"MigrationLog\""));
        Assert.Equal(new[] { "001_CreateLog", "002_InsertLog" }, _db.Query<HistoryRow>(
            "SELECT \"MigrationId\" FROM \"__SQliteOrmMigrations\" ORDER BY \"MigrationId\"")
            .Select(row => row.MigrationId));
    }

    [Fact]
    public void Failed_migration_rolls_back_schema_and_history_without_partial_changes()
    {
        var successful = new TestMigration("001_Success",
            builder => builder.ExecuteSql("CREATE TABLE \"StableTable\" (\"Id\" INTEGER);"));
        var failing = new TestMigration("002_Failure", builder =>
        {
            builder.ExecuteSql("CREATE TABLE \"RolledBackTable\" (\"Id\" INTEGER);");
            builder.ExecuteSql("INSERT INTO \"MissingTable\" VALUES (1);");
        });

        Assert.Throws<SQLiteException>(() => _db.Migrate(failing, successful));

        Assert.Equal(1, HistoryCount());
        Assert.Equal(0, _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RolledBackTable'"));
        Assert.Equal(1, _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='StableTable'"));
    }

    [Fact]
    public void Builder_schema_operations_and_down_migration_are_executable()
    {
        var migration = new TestMigration("001_Widget", builder =>
        {
            builder.CreateTable<MigrationWidget>();
            builder.ExecuteSql("CREATE TABLE \"ExistingWidgets\" (\"Id\" INTEGER PRIMARY KEY);");
            builder.AddColumn<ExistingWidget>(widget => widget.Email, nullable: true);
            builder.CreateIndex<ExistingWidget>(widget => widget.Email);
            builder.RenameColumn<ExistingWidget>(widget => widget.Email, "Contact");
            builder.RenameTable<ExistingWidget>("RenamedExistingWidgets");
        }, builder =>
        {
            builder.DropIndex("IX_ExistingWidgets_Email");
            builder.ExecuteSql("DROP TABLE \"RenamedExistingWidgets\";");
            builder.DropTable<MigrationWidget>();
        });

        _db.Migrate(migration);
        var columns = _db.Query<ColumnRow>("PRAGMA table_info(\"RenamedExistingWidgets\")");
        Assert.Contains(columns, column => column.name == "Contact");

        _db.RollbackLastMigration(migration);
        Assert.Equal(0, HistoryCount());
        Assert.Equal(0, _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RenamedExistingWidgets'"));
    }

    private int HistoryCount() => _db.ExecuteScalar<int>("SELECT COUNT(*) FROM \"__SQliteOrmMigrations\"");

    public void Dispose()
    {
        _db.Dispose();
        SQLiteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class TestMigration(
        string id, Action<MigrationBuilder> up, Action<MigrationBuilder>? down = null) : Migration
    {
        public override string Id => id;
        public override void Up(MigrationBuilder migration) => up(migration);
        public override void Down(MigrationBuilder migration) => down?.Invoke(migration);
    }

    private sealed class MigrationWidget
    {
        [Key, AutoIncrement] public long WidgetKey { get; set; }
        public string Name { get; set; } = string.Empty;
    }
    [System.ComponentModel.DataAnnotations.Schema.Table("ExistingWidgets")]
    private sealed class ExistingWidget
    {
        public int Id { get; set; }
        public string? Email { get; set; }
    }
    private sealed class HistoryRow { public string MigrationId { get; set; } = string.Empty; }
    private sealed class ColumnRow { public string name { get; set; } = string.Empty; }
}
