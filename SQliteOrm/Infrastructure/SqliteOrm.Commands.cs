using System.Data.SQLite;
using SQliteOrm.Mapping;
using SQliteOrm.RawSql;
using SQliteOrm.TypeMapping;

namespace SQliteOrm;

public partial class SqliteOrm
{
    /// <summary>Executes raw SQL and materializes mapped rows.</summary>
    public List<T> Query<T>(string query, Dictionary<string, object>? parameters = null) where T : new()
    {
        var command = CreateCommand(query, parameters, out var ownedConnection);
        using (ownedConnection)
        using (command)
        using (var reader = command.ExecuteReader())
            return MapReaderToObjects<T>(reader);
    }

    /// <summary>Executes raw SQL with parameters read from an anonymous or regular object.</summary>
    public List<T> Query<T>(string query, object parameters) where T : new() =>
        Query<T>(query, RawSqlParameters.FromObject(parameters));

    /// <summary>Executes raw SQL and converts its first scalar value to <typeparamref name="T"/>.</summary>
    public T? ExecuteScalar<T>(string query, Dictionary<string, object>? parameters = null)
    {
        var command = CreateCommand(query, parameters, out var ownedConnection);
        using (ownedConnection)
        using (command)
        {
            var result = command.ExecuteScalar();
            return result is null or DBNull ? default : (T)SqliteTypeHandler.FromDatabase(result, typeof(T));
        }
    }

    /// <summary>Executes scalar raw SQL with parameters read from an anonymous or regular object.</summary>
    public T? ExecuteScalar<T>(string query, object parameters) =>
        ExecuteScalar<T>(query, RawSqlParameters.FromObject(parameters));

    /// <summary>Executes a parameterized raw SQL command that does not return rows.</summary>
    public void ExecuteNonQuery(string query, Dictionary<string, object>? parameters = null) =>
        _ = ExecuteNonQueryAffected(query, parameters);

    /// <summary>Executes raw SQL with parameters read from an anonymous or regular object.</summary>
    public void ExecuteNonQuery(string query, object parameters) =>
        _ = ExecuteNonQueryAffected(query, RawSqlParameters.FromObject(parameters));

    internal int ExecuteNonQueryAffected(string query, Dictionary<string, object>? parameters = null)
    {
        lock (_writeLock)
        {
            var command = CreateCommand(query, parameters, out var ownedConnection);
            using (ownedConnection)
            using (command)
                return command.ExecuteNonQuery();
        }
    }

    private SQLiteCommand CreateCommand(string sql, Dictionary<string, object>? parameters,
        out SQLiteConnection? ownedConnection)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL cannot be empty.", nameof(sql));
        var context = _transactionContext.Value is { IsActive: true } active ? active : null;
        ownedConnection = context == null ? OpenConnection() : null;
        SQLiteCommand? command = null;
        try
        {
            command = context == null
                ? new SQLiteCommand(sql, ownedConnection)
                : new SQLiteCommand(sql, context.Connection, context.Transaction);
            if (_options.CommandTimeout.HasValue) command.CommandTimeout = _options.CommandTimeout.Value;
            foreach (var parameter in RawSqlParameters.Normalize(parameters) ?? [])
                command.Parameters.AddWithValue(parameter.Key, SqliteTypeHandler.ToDatabase(parameter.Value));
            return command;
        }
        catch
        {
            command?.Dispose();
            ownedConnection?.Dispose();
            throw;
        }
    }

    private static List<T> MapReaderToObjects<T>(SQLiteDataReader reader) where T : new()
    {
        var results = new List<T>();
        var properties = EntityMapCache.Get<T>().Properties.Where(property => property.CanWrite).ToArray();
        var ordinals = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, index => index, StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var item = new T();
            foreach (var property in properties)
            {
                if ((!ordinals.TryGetValue(property.ColumnName, out var ordinal) &&
                     !ordinals.TryGetValue(property.PropertyName, out ordinal)) || reader.IsDBNull(ordinal))
                    continue;
                try
                {
                    property.SetValue(item!, SqliteTypeHandler.FromDatabase(reader.GetValue(ordinal), property.ClrType));
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Could not materialize property '{property.PropertyName}' on '{typeof(T).Name}'.", exception);
                }
            }
            results.Add(item);
        }
        return results;
    }

    private SQLiteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        using var command = new SQLiteCommand(connection);
        if (_options.CommandTimeout.HasValue) command.CommandTimeout = _options.CommandTimeout.Value;
        var pragmas = new List<string> { $"PRAGMA foreign_keys = {(_options.EnableForeignKeys ? "ON" : "OFF")}" };
        if (_options.BusyTimeout.HasValue)
            pragmas.Add($"PRAGMA busy_timeout = {(long)_options.BusyTimeout.Value.TotalMilliseconds}");
        if (_options.EnableWal) pragmas.Add("PRAGMA journal_mode = WAL");
        command.CommandText = string.Join("; ", pragmas) + ";";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    /// <summary>Marks the ORM instance disposed and prevents it from opening new connections.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_transactionContext.Value is { IsActive: true })
            throw new InvalidOperationException("Cannot dispose SqliteOrm during an active transaction callback.");
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
