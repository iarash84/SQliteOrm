using System.Linq.Expressions;
using SQliteOrm.Mapping;

namespace SQliteOrm;

/// <summary>Builds and executes a strongly typed query for one mapped entity type.</summary>
public sealed class SqliteQuery<T> where T : new()
{
    private readonly SqLiteOrm _orm;
    private readonly IReadOnlyList<Expression<Func<T, bool>>> _predicates;

    internal SqliteQuery(SqLiteOrm orm, IReadOnlyList<Expression<Func<T, bool>>>? predicates = null)
    {
        _orm = orm;
        _predicates = predicates ?? Array.Empty<Expression<Func<T, bool>>>();
    }

    /// <summary>Adds a predicate. Execution is deferred until a terminal method is called.</summary>
    public SqliteQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new SqliteQuery<T>(_orm, _predicates.Append(predicate).ToArray());
    }

    /// <summary>Returns all matching entities.</summary>
    public List<T> ToList()
    {
        var command = BuildSelect();
        return _orm.Query<T>(command.Sql, command.Parameters);
    }

    /// <summary>Returns the first matching entity, or throws when no entity matches.</summary>
    public T First() => FirstOrDefault() ?? throw new InvalidOperationException("Sequence contains no elements.");

    /// <summary>Returns the first matching entity or the default value.</summary>
    public T? FirstOrDefault()
    {
        var command = BuildSelect(1);
        return _orm.Query<T>(command.Sql, command.Parameters).FirstOrDefault();
    }

    /// <summary>Returns the only matching entity and throws unless exactly one entity matches.</summary>
    public T Single()
    {
        var values = TakeForSingle();
        return values.Count switch
        {
            0 => throw new InvalidOperationException("Sequence contains no elements."),
            1 => values[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element.")
        };
    }

    /// <summary>Returns the only matching entity, default when none match, and throws when multiple match.</summary>
    public T? SingleOrDefault()
    {
        var values = TakeForSingle();
        return values.Count switch
        {
            0 => default,
            1 => values[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element.")
        };
    }

    /// <summary>Efficiently determines whether any matching row exists.</summary>
    public bool Any()
    {
        var predicate = BuildPredicate();
        var table = Quote(EntityMapCache.Get<T>().TableName);
        var where = predicate == null ? string.Empty : $" WHERE {predicate.Sql}";
        return _orm.ExecuteScalar<int>($"SELECT EXISTS(SELECT 1 FROM {table}{where} LIMIT 1);",
            predicate?.Parameters) != 0;
    }

    /// <summary>Counts matching rows in SQLite.</summary>
    public int Count()
    {
        var predicate = BuildPredicate();
        var table = Quote(EntityMapCache.Get<T>().TableName);
        var where = predicate == null ? string.Empty : $" WHERE {predicate.Sql}";
        return _orm.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}{where};", predicate?.Parameters);
    }

    private List<T> TakeForSingle()
    {
        var command = BuildSelect(2);
        return _orm.Query<T>(command.Sql, command.Parameters);
    }

    internal SqliteQueryCommand BuildSelect(int? limit = null)
    {
        var predicate = BuildPredicate();
        var table = Quote(EntityMapCache.Get<T>().TableName);
        var where = predicate == null ? string.Empty : $" WHERE {predicate.Sql}";
        var limitClause = limit.HasValue ? $" LIMIT {limit.Value}" : string.Empty;
        return new SqliteQueryCommand($"SELECT * FROM {table}{where}{limitClause};",
            predicate?.Parameters);
    }

    private SqlitePredicate? BuildPredicate()
    {
        if (_predicates.Count == 0) return null;
        var translator = new Querying.ExpressionTranslator<T>();
        Querying.SqlExpression? root = null;
        foreach (var predicate in _predicates)
        {
            var translated = translator.Translate(predicate);
            root = root == null ? translated : new Querying.SqlBinaryExpression(
                root, Querying.SqlBinaryOperator.And, translated);
        }
        var compiled = new Querying.SqliteQueryCompiler().Compile(root!);
        return new SqlitePredicate(compiled.Sql, compiled.Parameters.ToDictionary(item => item.Key, item => item.Value));
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record SqlitePredicate(string Sql, Dictionary<string, object> Parameters);
}

internal sealed record SqliteQueryCommand(string Sql, Dictionary<string, object>? Parameters);
