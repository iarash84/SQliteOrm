using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace SQliteOrm.Mapping;

internal static class EntityMapCache
{
    private static readonly ConcurrentDictionary<Type, EntityMap> Maps = new();
    private static readonly NullabilityInfoContext Nullability = new();
    internal static EntityMap<T> Get<T>() => (EntityMap<T>)Get(typeof(T));
    internal static EntityMap Get(Type type) => Maps.GetOrAdd(type, Create);

    internal static string ResolveReferencedKeyColumn(Type declaringType, string entityOrTableName)
    {
        var referencedType = declaringType.Assembly.GetTypes().FirstOrDefault(type =>
            type.Name.Equals(entityOrTableName, StringComparison.OrdinalIgnoreCase) ||
            (type.GetCustomAttribute<TableAttribute>()?.Name?.Equals(
                entityOrTableName, StringComparison.OrdinalIgnoreCase) ?? false));
        return referencedType == null
            ? "Id"
            : Get(referencedType).Key?.ColumnName ?? throw new InvalidOperationException(
                $"Referenced type '{referencedType.Name}' does not define a [Key] property.");
    }

    private static EntityMap Create(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(NotMappedAttribute), false))
            .Select(CreatePropertyMap).ToArray();
        var tableName = type.GetCustomAttribute<TableAttribute>()?.Name ?? type.Name;
        return (EntityMap)Activator.CreateInstance(typeof(EntityMap<>).MakeGenericType(type),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { tableName, properties }, null)!;
    }

    private static PropertyMap CreatePropertyMap(PropertyInfo property)
    {
        var required = property.IsDefined(typeof(RequiredAttribute), false);
        var key = property.IsDefined(typeof(KeyAttribute), false);
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var integerKey = underlying == typeof(int) || underlying == typeof(long);
        var hasAutoIncrement = property.IsDefined(typeof(AutoIncrementAttribute), true);
        if (hasAutoIncrement && (!key || !integerKey))
            throw new InvalidOperationException(
                $"[AutoIncrement] property '{property.Name}' must also be an int or long [Key].");
        var autoIncrement = key && integerKey &&
            (hasAutoIncrement || property.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
        var nullable = !required && (Nullable.GetUnderlyingType(property.PropertyType) != null ||
            (!property.PropertyType.IsValueType && Nullability.Create(property).ReadState != NullabilityState.NotNull));
        return new PropertyMap(property, property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name,
            GetSqliteType(underlying), nullable, key, autoIncrement, autoIncrement,
            property.IsDefined(typeof(UniqueAttribute), false), required, property.GetCustomAttribute<ForeignKeyAttribute>());
    }

    private static string GetSqliteType(Type type) => type switch
    {
        { } when type == typeof(int) || type == typeof(long) || type == typeof(bool) || type.IsEnum => "INTEGER",
        { } when type == typeof(double) || type == typeof(float) => "REAL",
        { } when type == typeof(byte[]) => "BLOB",
        _ => "TEXT"
    };
}
