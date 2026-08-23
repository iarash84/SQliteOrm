using System.Linq.Expressions;

namespace SQliteOrm;

/// <summary>Convenience methods for strongly typed ORM queries.</summary>
public static class SqLiteOrmQueryExtensions
{
    /// <summary>Counts entities matching a strongly typed predicate.</summary>
    public static int Count<T>(this SqliteOrm orm, Expression<Func<T, bool>> predicate) where T : new()
    {
        ArgumentNullException.ThrowIfNull(orm);
        return orm.Table<T>().Where(predicate).Count();
    }
}
