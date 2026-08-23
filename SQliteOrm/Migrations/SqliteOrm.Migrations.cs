using SQliteOrm.Migrations;

namespace SQliteOrm;

public partial class SqliteOrm
{
    /// <summary>Applies pending migrations once, in deterministic ordinal ID order.</summary>
    public void Migrate(params Migration[] migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        EnsureMigrationHistoryTable();
        var ordered = migrations.Select(migration => migration ?? throw new ArgumentException(
                "Migration collection cannot contain null values.", nameof(migrations)))
            .OrderBy(migration => migration.Id, StringComparer.Ordinal).ToArray();
        if (ordered.Any(migration => string.IsNullOrWhiteSpace(migration.Id)))
            throw new ArgumentException("Migration IDs cannot be empty.", nameof(migrations));
        var duplicate = ordered.GroupBy(migration => migration.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"Duplicate migration ID '{duplicate.Key}'.", nameof(migrations));

        var applied = Query<MigrationHistoryRow>(
                $"SELECT \"MigrationId\", \"AppliedAtUtc\" FROM {QuoteIdentifier(MigrationHistoryTable)};")
            .Select(row => row.MigrationId).ToHashSet(StringComparer.Ordinal);
        foreach (var migration in ordered.Where(migration => !applied.Contains(migration.Id)))
        {
            Transaction(tx =>
            {
                var builder = new MigrationBuilder();
                migration.Up(builder);
                builder.Apply(tx);
                tx.Execute($"INSERT INTO {QuoteIdentifier(MigrationHistoryTable)} " +
                           "(\"MigrationId\", \"AppliedAtUtc\") VALUES (@id, @appliedAt);",
                    new { id = migration.Id, appliedAt = DateTimeOffset.UtcNow });
            });
        }
    }

    /// <summary>Runs <see cref="Migration.Down"/> for the latest applied supplied migration.</summary>
    public void RollbackLastMigration(params Migration[] migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        EnsureMigrationHistoryTable();
        var latest = Query<MigrationHistoryRow>(
            $"SELECT \"MigrationId\", \"AppliedAtUtc\" FROM {QuoteIdentifier(MigrationHistoryTable)} " +
            "ORDER BY \"AppliedAtUtc\" DESC, \"MigrationId\" DESC LIMIT 1;").FirstOrDefault();
        if (latest == null) return;
        var migration = migrations.FirstOrDefault(item => item != null &&
            item.Id.Equals(latest.MigrationId, StringComparison.Ordinal)) ?? throw new InvalidOperationException(
            $"Applied migration '{latest.MigrationId}' was not supplied for rollback.");
        Transaction(tx =>
        {
            var builder = new MigrationBuilder();
            migration.Down(builder);
            builder.Apply(tx);
            tx.Execute($"DELETE FROM {QuoteIdentifier(MigrationHistoryTable)} WHERE \"MigrationId\" = @id;",
                new { id = migration.Id });
        });
    }

    private void EnsureMigrationHistoryTable() => ExecuteNonQuery(
        $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(MigrationHistoryTable)} (" +
        "\"MigrationId\" TEXT PRIMARY KEY, \"AppliedAtUtc\" TEXT NOT NULL);");
}
