using System.Linq.Expressions;
using SQliteOrm.Mapping;
using SQliteOrm.Querying.Joins;

namespace SQliteOrm;

/// <summary>Builds a strongly typed join between two mapped entity types.</summary>
public sealed class SqliteJoinQuery<TLeft, TRight>
    where TLeft : new() where TRight : new()
{
    private const string LeftAlias = "t0";
    private const string RightAlias = "t1";
    private readonly SqliteOrm _orm;
    private readonly SqlJoinExpression _join;
    private readonly IReadOnlyList<Expression<Func<TLeft, bool>>> _leftPredicates;
    private readonly IReadOnlyList<Expression<Func<TLeft, TRight, bool>>> _joinPredicates;

    internal SqliteJoinQuery(SqliteOrm orm, PropertyMap leftKey, PropertyMap rightKey,
        IReadOnlyList<Expression<Func<TLeft, bool>>> leftPredicates,
        IReadOnlyList<Expression<Func<TLeft, TRight, bool>>>? joinPredicates = null)
    {
        _orm = orm;
        _leftPredicates = leftPredicates;
        _joinPredicates = joinPredicates ?? Array.Empty<Expression<Func<TLeft, TRight, bool>>>();
        _join = new SqlJoinExpression(EntityMapCache.Get<TLeft>(), LeftAlias,
            EntityMapCache.Get<TRight>(), RightAlias, leftKey, rightKey, SqlJoinType.Inner);
    }

    /// <summary>Adds a predicate that can reference both sides of the join.</summary>
    public SqliteJoinQuery<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new SqliteJoinQuery<TLeft, TRight>(_orm, _join.LeftKey, _join.RightKey,
            _leftPredicates, _joinPredicates.Append(predicate).ToArray());
    }

    /// <summary>Selects mapped properties from both joined entities into a result object.</summary>
    public SqliteJoinProjectionQuery<TLeft, TRight, TResult> Select<TResult>(
        Expression<Func<TLeft, TRight, TResult>> projection) where TResult : new() =>
        new(_orm, _join, _leftPredicates, _joinPredicates, ParseProjection(projection));

    private static IReadOnlyList<SqlProjectionColumn> ParseProjection<TResult>(
        Expression<Func<TLeft, TRight, TResult>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Body is not MemberInitExpression initializer)
            throw new NotSupportedException("Join projection must use an object initializer with direct mapped properties.");
        var resultMap = EntityMapCache.Get<TResult>();
        var columns = new List<SqlProjectionColumn>();
        foreach (var binding in initializer.Bindings)
        {
            if (binding is not MemberAssignment { Expression: MemberExpression source } assignment ||
                source.Expression is not ParameterExpression parameter)
                throw new NotSupportedException("Join projection assignments must directly select mapped properties.");
            EntityMap sourceMap = parameter == projection.Parameters[0]
                ? EntityMapCache.Get<TLeft>()
                : parameter == projection.Parameters[1]
                    ? EntityMapCache.Get<TRight>()
                    : throw new NotSupportedException("Join projection contains an unknown parameter.");
            var alias = parameter == projection.Parameters[0] ? LeftAlias : RightAlias;
            var result = resultMap.GetProperty(assignment.Member.Name);
            columns.Add(new SqlProjectionColumn(alias, sourceMap.GetProperty(source.Member.Name), result.ColumnName));
        }
        if (columns.Count == 0) throw new NotSupportedException("Join projection must select at least one property.");
        return columns;
    }
}

/// <summary>Executes a typed projection over an inner join.</summary>
public sealed class SqliteJoinProjectionQuery<TLeft, TRight, TResult>
    where TLeft : new() where TRight : new() where TResult : new()
{
    private readonly SqliteOrm _orm;
    private readonly SqlJoinExpression _join;
    private readonly IReadOnlyList<Expression<Func<TLeft, bool>>> _leftPredicates;
    private readonly IReadOnlyList<Expression<Func<TLeft, TRight, bool>>> _joinPredicates;
    private readonly IReadOnlyList<SqlProjectionColumn> _columns;

    internal SqliteJoinProjectionQuery(SqliteOrm orm, SqlJoinExpression join,
        IReadOnlyList<Expression<Func<TLeft, bool>>> leftPredicates,
        IReadOnlyList<Expression<Func<TLeft, TRight, bool>>> joinPredicates,
        IReadOnlyList<SqlProjectionColumn> columns)
    { _orm = orm; _join = join; _leftPredicates = leftPredicates; _joinPredicates = joinPredicates; _columns = columns; }

    /// <summary>Executes the joined projection query and materializes all results.</summary>
    public List<TResult> ToList()
    {
        var command = BuildCommand();
        return _orm.Query<TResult>(command.Sql, command.Parameters);
    }

    internal SqliteQueryCommand BuildCommand()
    {
        var expressions = new List<Querying.SqlExpression>();
        var translator = new Querying.ExpressionTranslator<TLeft>();
        expressions.AddRange(_leftPredicates.Select(predicate => translator.Translate(predicate, _join.LeftAlias)));
        expressions.AddRange(_joinPredicates.Select(predicate =>
            translator.Translate<TRight>(predicate, _join.LeftAlias, _join.RightAlias)));
        return SqliteJoinCompiler.Compile(_join, _columns, expressions);
    }
}
