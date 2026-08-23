using System.Linq.Expressions;

namespace SQliteOrm.Querying;

internal sealed record CompiledPredicate(string Sql, Dictionary<string, object> Parameters);

internal static class PredicateCompiler<T>
{
    internal static CompiledPredicate Compile(IEnumerable<Expression<Func<T, bool>>> predicates)
    {
        var translator = new ExpressionTranslator<T>();
        SqlExpression? root = null;
        foreach (var predicate in predicates)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            var translated = translator.Translate(predicate);
            root = root == null ? translated : new SqlBinaryExpression(root, SqlBinaryOperator.And, translated);
        }
        if (root == null)
            throw new ArgumentException("At least one predicate is required.", nameof(predicates));
        var compiled = new SqliteQueryCompiler().Compile(root);
        return new CompiledPredicate(compiled.Sql,
            compiled.Parameters.ToDictionary(item => item.Key, item => item.Value));
    }
}
