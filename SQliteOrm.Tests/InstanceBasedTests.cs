using System.ComponentModel.DataAnnotations;
using System.Data.SQLite;

namespace SQliteOrm.Tests;

public sealed class InstanceBasedTests
{
    [Fact]
    public async Task Independent_instances_use_different_databases_in_parallel()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"sqlite-orm-first-{Guid.NewGuid():N}.db");
        var secondPath = Path.Combine(Path.GetTempPath(), $"sqlite-orm-second-{Guid.NewGuid():N}.db");
        try
        {
            using var first = new SqliteOrm($"Data Source={firstPath}");
            using var second = new SqliteOrm($"Data Source={secondPath}");

            await Task.WhenAll(
                Task.Run(() => { first.CreateTable<InstanceRecord>(); first.Insert(new InstanceRecord { Value = "first" }); }),
                Task.Run(() => { second.CreateTable<InstanceRecord>(); second.Insert(new InstanceRecord { Value = "second" }); }));

            Assert.Equal("first", first.Table<InstanceRecord>().Single().Value);
            Assert.Equal("second", second.Table<InstanceRecord>().Single().Value);
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
            if (File.Exists(firstPath)) File.Delete(firstPath);
            if (File.Exists(secondPath)) File.Delete(secondPath);
        }
    }

    [Fact]
    public void Options_configure_connection_pragmas_and_dispose_is_enforced()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlite-orm-options-{Guid.NewGuid():N}.db");
        var db = new SqliteOrm(new SqliteOrmOptions
        {
            ConnectionString = $"Data Source={path}",
            EnableForeignKeys = true,
            EnableWal = true,
            BusyTimeout = TimeSpan.FromSeconds(3),
            CommandTimeout = 5
        });
        try
        {
            Assert.Equal(1, db.ExecuteScalar<int>("PRAGMA foreign_keys"));
            Assert.Equal(3000, db.ExecuteScalar<int>("PRAGMA busy_timeout"));
            Assert.Equal("wal", db.ExecuteScalar<string>("PRAGMA journal_mode"));
            db.Dispose();
            Assert.Throws<ObjectDisposedException>(() => db.ExecuteScalar<int>("SELECT 1"));
        }
        finally
        {
            db.Dispose();
            SQLiteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class InstanceRecord
    {
        [Key, AutoIncrement] public long RecordKey { get; set; }
        public string Value { get; set; } = string.Empty;
    }
}
