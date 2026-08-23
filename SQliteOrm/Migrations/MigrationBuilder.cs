using System.Linq.Expressions;
using SQliteOrm.Mapping;
using SQliteOrm.Querying;
using SQliteOrm.RawSql;

namespace SQliteOrm.Migrations;

/// <summary>Collects SQLite-focused schema operations for a migration.</summary>
public sealed class MigrationBuilder
{
    private readonly List<Action<SqliteTransactionSession>> _operations = new();

    public void ExecuteSql(string sql, Dictionary<string, object>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL cannot be empty.", nameof(sql));
        var snapshot = parameters == null ? null : new Dictionary<string, object>(parameters);
        _operations.Add(tx => tx.Execute(sql, snapshot));
    }

    public void ExecuteSql(string sql, object parameters) =>
        ExecuteSql(sql, RawSqlParameters.FromObject(parameters));

    public void CreateTable<T>() where T : new() => _operations.Add(tx => tx.CreateTable<T>());

    public void DropTable<T>() => _operations.Add(tx =>
        tx.Execute($"DROP TABLE {Quote(EntityMapCache.Get<T>().TableName)};"));

    public void AddColumn<T>(Expression<Func<T, object?>> selector, bool? nullable = null)
    {
        var property = MappedSelector.Resolve(selector);
        if (property.IsPrimaryKey || property.IsAutoIncrement || property.IsUnique)
            throw new NotSupportedException("SQLite cannot add primary-key, auto-increment, or unique columns with ALTER TABLE ADD COLUMN.");
        var allowsNull = nullable ?? property.IsNullable;
        _operations.Add(tx => tx.Execute(
            $"ALTER TABLE {Quote(EntityMapCache.Get<T>().TableName)} ADD COLUMN " +
            $"{Quote(property.ColumnName)} {property.SqliteType}{(allowsNull ? string.Empty : " NOT NULL")};"));
    }

    public void CreateIndex<T>(Expression<Func<T, object?>> selector,
        string? indexName = null, bool unique = false)
    {
        var map = EntityMapCache.Get<T>();
        var property = MappedSelector.Resolve(selector);
        var name = indexName ?? $"IX_{map.TableName}_{property.ColumnName}";
        _operations.Add(tx => tx.Execute(
            $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {Quote(name)} " +
            $"ON {Quote(map.TableName)} ({Quote(property.ColumnName)});"));
    }

    public void DropIndex(string indexName)
    {
        if (string.IsNullOrWhiteSpace(indexName)) throw new ArgumentException("Index name cannot be empty.", nameof(indexName));
        _operations.Add(tx => tx.Execute($"DROP INDEX {Quote(indexName)};"));
    }

    public void RenameTable<T>(string newTableName)
    {
        if (string.IsNullOrWhiteSpace(newTableName)) throw new ArgumentException("Table name cannot be empty.", nameof(newTableName));
        _operations.Add(tx => tx.Execute(
            $"ALTER TABLE {Quote(EntityMapCache.Get<T>().TableName)} RENAME TO {Quote(newTableName)};"));
    }

    public void RenameColumn<T>(Expression<Func<T, object?>> selector, string newColumnName)
    {
        if (string.IsNullOrWhiteSpace(newColumnName)) throw new ArgumentException("Column name cannot be empty.", nameof(newColumnName));
        var property = MappedSelector.Resolve(selector);
        _operations.Add(tx => tx.Execute(
            $"ALTER TABLE {Quote(EntityMapCache.Get<T>().TableName)} " +
            $"RENAME COLUMN {Quote(property.ColumnName)} TO {Quote(newColumnName)};"));
    }

    internal void Apply(SqliteTransactionSession transaction)
    {
        foreach (var operation in _operations) operation(transaction);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
