using System.Globalization;

namespace SQliteOrm.TypeMapping;

internal static class SqliteTypeHandler
{
    internal static Type Normalize(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    internal static string GetAffinity(Type type)
    {
        var normalized = Normalize(type);
        if (normalized.IsEnum || normalized == typeof(byte) || normalized == typeof(short) ||
            normalized == typeof(int) || normalized == typeof(long) || normalized == typeof(bool))
            return "INTEGER";
        if (normalized == typeof(float) || normalized == typeof(double))
            return "REAL";
        if (normalized == typeof(decimal))
            return "NUMERIC";
        if (normalized == typeof(byte[]))
            return "BLOB";
        if (normalized == typeof(string) || normalized == typeof(DateTime) ||
            normalized == typeof(DateTimeOffset) || normalized == typeof(DateOnly) ||
            normalized == typeof(TimeOnly) || normalized == typeof(Guid))
            return "TEXT";
        return "TEXT";
    }

    internal static object ToDatabase(object? value) => value switch
    {
        null => DBNull.Value,
        bool boolean => boolean ? 1L : 0L,
        Guid guid => guid.ToString("D"),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        Enum enumValue => Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()), CultureInfo.InvariantCulture),
        _ => value
    };

    internal static object FromDatabase(object value, Type destinationType)
    {
        var targetType = Normalize(destinationType);
        if (targetType.IsInstanceOfType(value))
            return value;
        if (targetType.IsEnum)
        {
            if (value is string text && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return Enum.Parse(targetType, text, true);
            var underlying = Enum.GetUnderlyingType(targetType);
            return Enum.ToObject(targetType, Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture)!);
        }
        if (targetType == typeof(Guid))
        {
            if (value is byte[] bytes && bytes.Length == 16) return new Guid(bytes);
            if (Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid)) return guid;
            throw new InvalidCastException($"Cannot convert value '{value}' to Guid.");
        }
        if (targetType == typeof(DateTime))
            return value is DateTime dateTime ? dateTime : DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (targetType == typeof(DateTimeOffset))
            return value is DateTimeOffset offset ? offset : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (targetType == typeof(DateOnly))
            return value is DateOnly date ? date : DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        if (targetType == typeof(TimeOnly))
            return value is TimeOnly time ? time : TimeOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        if (targetType == typeof(bool))
            return value is string booleanText ? bool.Parse(booleanText) : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        if (targetType == typeof(byte[]))
            return value is byte[] blob ? blob : throw new InvalidCastException($"Cannot convert '{value.GetType()}' to byte[].");
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture)!;
    }
}
