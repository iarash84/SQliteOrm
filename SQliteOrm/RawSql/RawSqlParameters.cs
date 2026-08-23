using System.Collections.Concurrent;
using System.Reflection;

namespace SQliteOrm.RawSql;

internal static class RawSqlParameters
{
    private sealed record Accessor(string Name, PropertyInfo Property);

    private static readonly ConcurrentDictionary<Type, Accessor[]> AccessorCache = new();

    internal static Dictionary<string, object> FromObject(object parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters is Dictionary<string, object> dictionary)
            return Normalize(dictionary)!;

        var accessors = AccessorCache.GetOrAdd(parameters.GetType(), CreateAccessors);
        var result = new Dictionary<string, object>(accessors.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var accessor in accessors)
            Add(result, accessor.Name, accessor.Property.GetValue(parameters));
        return result;
    }

    internal static Dictionary<string, object>? Normalize(Dictionary<string, object>? parameters)
    {
        if (parameters == null) return null;
        var result = new Dictionary<string, object>(parameters.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
            Add(result, parameter.Key, parameter.Value);
        return result;
    }

    private static Accessor[] CreateAccessors(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property => new Accessor(property.Name, property))
            .ToArray();
        if (properties.Length == 0)
            throw new ArgumentException($"Parameter object type '{type.Name}' has no readable public properties.");
        return properties;
    }

    private static void Add(Dictionary<string, object> destination, string name, object? value)
    {
        var normalized = NormalizeName(name);
        if (!destination.TryAdd(normalized, value ?? DBNull.Value))
            throw new ArgumentException($"Duplicate SQL parameter name '{normalized}'.", nameof(name));
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("SQL parameter names cannot be empty.", nameof(name));
        var unprefixed = name[0] is '@' or ':' or '$' ? name[1..] : name;
        if (unprefixed.Length == 0 || !(char.IsLetter(unprefixed[0]) || unprefixed[0] == '_') ||
            unprefixed.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            throw new ArgumentException($"Invalid SQL parameter name '{name}'.", nameof(name));
        return $"@{unprefixed}";
    }
}
