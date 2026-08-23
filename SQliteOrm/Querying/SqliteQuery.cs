using System.Linq.Expressions;
using SQliteOrm.Mapping;

namespace SQliteOrm;

/// <summary>Builds and executes a strongly typed query for one mapped entity type.</summary>
public sealed class SqliteQuery<T> where T : new()
{
    private readonly SqliteOrm _orm;
    private readonly IReadOnlyList<Expression<Func<T, bool>>> _predicates;
    private readonly IReadOnlyList<Ordering> _orderings;
    private readonly int? _skip;
    private readonly int? _take;

    internal SqliteQuery(SqliteOrm orm, IReadOnlyList<Expression<Func<T, bool>>>? predicates = null,
        IReadOnlyList<Ordering>? orderings = null, int? skip = null, int? take = null)
    {
        _orm = orm;
        _predicates = predicates ?? Array.Empty<Expression<Func<T, bool>>>();
        _orderings = orderings ?? Array.Empty<Ordering>();
        _skip = skip;
        _take = take;
    }

    /// <summary>Adds a predicate. Execution is deferred until a terminal method is called.</summary>
    public SqliteQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return New(predicates: _predicates.Append(predicate).ToArray());
    }

    /// <summary>Replaces existing ordering with an ascending property ordering.</summary>
    public SqliteQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector) =>
        New(orderings: new[] { CreateOrdering(selector, false) });

    /// <summary>Replaces existing ordering with a descending property ordering.</summary>
    public SqliteQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector) =>
        New(orderings: new[] { CreateOrdering(selector, true) });

    /// <summary>Adds an ascending property to the existing ordering.</summary>
    public SqliteQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector) =>
        AppendOrdering(CreateOrdering(selector, false));

    /// <summary>Adds a descending property to the existing ordering.</summary>
    public SqliteQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector) =>
        AppendOrdering(CreateOrdering(selector, true));

    /// <summary>Skips a non-negative number of matching rows.</summary>
    public SqliteQuery<T> Skip(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return New(skip: count);
    }

    /// <summary>Limits the query to a non-negative number of matching rows.</summary>
    public SqliteQuery<T> Take(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return New(take: count);
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
        var selection = BuildSelection("1", 1);
        return _orm.ExecuteScalar<int>($"SELECT EXISTS({selection.Sql});", selection.Parameters) != 0;
    }

    /// <summary>Counts matching rows in SQLite.</summary>
    public int Count()
    {
        if (_skip.HasValue || _take.HasValue)
        {
            var selection = BuildSelection("1");
            return _orm.ExecuteScalar<int>($"SELECT COUNT(*) FROM ({selection.Sql});", selection.Parameters);
        }
        var direct = BuildSelection("COUNT(*)", includeOrdering: false);
        return _orm.ExecuteScalar<int>($"{direct.Sql};", direct.Parameters);
    }

    private List<T> TakeForSingle()
    {
        var command = BuildSelect(2);
        return _orm.Query<T>(command.Sql, command.Parameters);
    }

    internal SqliteQueryCommand BuildSelect(int? limit = null)
    {
        var selection = BuildSelection("*", limit);
        return new SqliteQueryCommand($"{selection.Sql};", selection.Parameters);
    }

    private SqliteQueryCommand BuildSelection(string columns, int? terminalLimit = null, bool includeOrdering = true)
    {
        var predicate = BuildPredicate();
        var table = Quote(EntityMapCache.Get<T>().TableName);
        var where = predicate == null ? string.Empty : $" WHERE {predicate.Sql}";
        var parameters = predicate?.Parameters ?? new Dictionary<string, object>();
        var ordering = includeOrdering && _orderings.Count > 0
            ? $" ORDER BY {string.Join(", ", _orderings.Select(item => $"{Quote(item.Property.ColumnName)} {(item.Descending ? "DESC" : "ASC")}"))}"
            : string.Empty;
        var effectiveTake = terminalLimit.HasValue && _take.HasValue
            ? Math.Min(terminalLimit.Value, _take.Value)
            : terminalLimit ?? _take;
        var pagination = string.Empty;
        if (effectiveTake.HasValue)
        {
            parameters["@__limit"] = effectiveTake.Value;
            pagination = " LIMIT @__limit";
        }
        else if (_skip.HasValue)
        {
            pagination = " LIMIT -1";
        }
        if (_skip.HasValue)
        {
            parameters["@__offset"] = _skip.Value;
            pagination += " OFFSET @__offset";
        }
        return new SqliteQueryCommand($"SELECT {columns} FROM {table}{where}{ordering}{pagination}", parameters);
    }

    private SqliteQuery<T> AppendOrdering(Ordering ordering)
    {
        if (_orderings.Count == 0)
            throw new InvalidOperationException("ThenBy requires a preceding OrderBy or OrderByDescending call.");
        return New(orderings: _orderings.Append(ordering).ToArray());
    }

    private static Ordering CreateOrdering<TKey>(Expression<Func<T, TKey>> selector, bool descending)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;
        if (body is not MemberExpression { Expression: ParameterExpression } member ||
            member.Expression != selector.Parameters[0])
            throw new NotSupportedException(
                $"Ordering selector '{selector}' is not supported. Select one mapped property directly.");
        return new Ordering(EntityMapCache.Get<T>().GetProperty(member.Member.Name), descending);
    }

    private SqliteQuery<T> New(IReadOnlyList<Expression<Func<T, bool>>>? predicates = null,
        IReadOnlyList<Ordering>? orderings = null, int? skip = null, int? take = null) =>
        new(_orm, predicates ?? _predicates, orderings ?? _orderings, skip ?? _skip, take ?? _take);

    private SqlitePredicate? BuildPredicate()
    {
        if (_predicates.Count == 0) return null;
        var compiled = Querying.PredicateCompiler<T>.Compile(_predicates);
        return new SqlitePredicate(compiled.Sql, compiled.Parameters);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record SqlitePredicate(string Sql, Dictionary<string, object> Parameters);
    internal sealed record Ordering(PropertyMap Property, bool Descending);
}

internal sealed record SqliteQueryCommand(string Sql, Dictionary<string, object>? Parameters);
