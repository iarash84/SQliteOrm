using SQliteOrm.Mapping;
using SQliteOrm.TypeMapping;

namespace SQliteOrm.Persistence;

internal sealed record UpsertCommand(
    string Sql, Dictionary<string, object> Parameters, PropertyMap? GeneratedKey);

internal static class UpsertCommandBuilder
{
    internal static UpsertCommand Build<T>(T entity, string conflictPropertyName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var map = EntityMapCache.Get<T>();
        var conflict = map.GetProperty(conflictPropertyName);
        if (!conflict.IsPrimaryKey && !conflict.IsUnique)
            throw new InvalidOperationException(
                $"Upsert conflict property '{conflict.PropertyName}' must be a primary key or unique column.");

        var properties = map.Properties.Where(property => !property.IsDatabaseGenerated ||
            property == conflict && !IsDefaultValue(property.GetValue(entity!), property.ClrType)).ToArray();
        var useGeneratedDefault = properties.Length == 0;
        if (useGeneratedDefault)
            properties = new[] { conflict };

        var parameters = properties.ToDictionary(property => $"@{property.PropertyName}",
            property => SqliteTypeHandler.ToDatabase(property.GetValue(entity!)));
        if (useGeneratedDefault)
            parameters[$"@{conflict.PropertyName}"] = DBNull.Value;
        var columns = string.Join(", ", properties.Select(property => Quote(property.ColumnName)));
        var values = string.Join(", ", properties.Select(property => $"@{property.PropertyName}"));
        var updates = map.Properties.Where(property => !property.IsPrimaryKey && !property.IsDatabaseGenerated &&
                                                       property != conflict).ToArray();
        var action = updates.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updates.Select(property =>
                $"{Quote(property.ColumnName)} = excluded.{Quote(property.ColumnName)}"));
        var returning = map.Key is { IsDatabaseGenerated: true } key
            ? $" RETURNING {Quote(key.ColumnName)}"
            : string.Empty;
        var sql = $"INSERT INTO {Quote(map.TableName)} ({columns}) VALUES ({values}) " +
                  $"ON CONFLICT ({Quote(conflict.ColumnName)}) {action}{returning};";
        return new UpsertCommand(sql, parameters, map.Key is { IsDatabaseGenerated: true } ? map.Key : null);
    }

    private static bool IsDefaultValue(object? value, Type type)
    {
        if (value == null) return true;
        var normalized = Nullable.GetUnderlyingType(type) ?? type;
        return normalized.IsValueType && value.Equals(Activator.CreateInstance(normalized));
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
