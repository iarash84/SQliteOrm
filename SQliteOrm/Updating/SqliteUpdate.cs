using System.Linq.Expressions;
using SQliteOrm.Mapping;
using SQliteOrm.Querying;

namespace SQliteOrm;

/// <summary>Builds and executes a parameterized partial update for one mapped entity type.</summary>
public sealed class SqliteUpdate<T>
{
    private readonly SqliteOrm _orm;
    private readonly List<Assignment> _assignments = new();
    private readonly List<Expression<Func<T, bool>>> _predicates = new();

    internal SqliteUpdate(SqliteOrm orm) => _orm = orm;

    /// <summary>Adds a parameterized assignment for one directly selected mapped property.</summary>
    public SqliteUpdate<T> Set<TProperty>(Expression<Func<T, TProperty>> selector, TProperty value)
    {
        var property = MappedSelector.Resolve(selector);
        if (property.IsDatabaseGenerated)
            throw new InvalidOperationException(
                $"Database-generated property '{property.Property.Name}' cannot be modified by a partial update.");
        if (_assignments.Any(assignment => assignment.Property == property))
            throw new InvalidOperationException($"Property '{property.Property.Name}' already has a Set operation.");
        _assignments.Add(new Assignment(property, value));
        return this;
    }

    /// <summary>Adds a required predicate; multiple predicates are combined with SQL <c>AND</c>.</summary>
    public SqliteUpdate<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates.Add(predicate);
        return this;
    }

    /// <summary>Executes the update and returns the number of affected rows.</summary>
    /// <exception cref="InvalidOperationException">No assignment or predicate has been configured.</exception>
    public int Execute()
    {
        if (_assignments.Count == 0)
            throw new InvalidOperationException("A partial update requires at least one Set operation.");
        if (_predicates.Count == 0)
            throw new InvalidOperationException("A partial update requires a Where predicate to prevent an accidental full-table update.");

        var predicate = PredicateCompiler<T>.Compile(_predicates);
        var parameters = new Dictionary<string, object>(predicate.Parameters);
        var setters = new string[_assignments.Count];
        for (var index = 0; index < _assignments.Count; index++)
        {
            var parameterName = $"@set{index}";
            var assignment = _assignments[index];
            setters[index] = $"{Quote(assignment.Property.ColumnName)} = {parameterName}";
            parameters[parameterName] = assignment.Value ?? DBNull.Value;
        }

        var table = Quote(EntityMapCache.Get<T>().TableName);
        return _orm.ExecuteNonQueryAffected(
            $"UPDATE {table} SET {string.Join(", ", setters)} WHERE {predicate.Sql};", parameters);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    private sealed record Assignment(PropertyMap Property, object? Value);
}
