using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using SQliteOrm.Mapping;

namespace SQliteOrm.Querying;

internal sealed class ExpressionTranslator<T>
{
    private readonly EntityMap<T> _map = EntityMapCache.Get<T>();
    private ParameterExpression _entityParameter = null!;

    internal SqlExpression Translate(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _entityParameter = predicate.Parameters.Single();
        return TranslatePredicate(predicate.Body);
    }

    private SqlExpression TranslatePredicate(Expression expression)
    {
        expression = StripConvert(expression);
        return expression switch
        {
            BinaryExpression binary => TranslateBinary(binary),
            UnaryExpression { NodeType: ExpressionType.Not } unary => new SqlNotExpression(TranslatePredicate(unary.Operand)),
            MethodCallExpression method => TranslateMethodCall(method),
            MemberExpression member when IsEntityProperty(member) && member.Type == typeof(bool) =>
                new SqlBinaryExpression(Column(member), SqlBinaryOperator.Equal, new SqlParameterExpression(true)),
            ConstantExpression { Type: { } type } constant when type == typeof(bool) =>
                new SqlBooleanConstantExpression((bool)constant.Value!),
            _ => throw Unsupported(expression)
        };
    }

    private SqlExpression TranslateBinary(BinaryExpression expression)
    {
        if (expression.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return new SqlBinaryExpression(TranslatePredicate(expression.Left),
                expression.NodeType == ExpressionType.AndAlso ? SqlBinaryOperator.And : SqlBinaryOperator.Or,
                TranslatePredicate(expression.Right));

        var left = TranslateOperand(expression.Left);
        var right = TranslateOperand(expression.Right);
        var operation = expression.NodeType switch
        {
            ExpressionType.Equal => SqlBinaryOperator.Equal,
            ExpressionType.NotEqual => SqlBinaryOperator.NotEqual,
            ExpressionType.GreaterThan => SqlBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => SqlBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
            _ => throw Unsupported(expression)
        };

        if (left is SqlParameterExpression { Value: null } && right is SqlColumnExpression rightColumn)
            return NullComparison(rightColumn, operation);
        if (right is SqlParameterExpression { Value: null } && left is SqlColumnExpression leftColumn)
            return NullComparison(leftColumn, operation);
        if (left is not SqlColumnExpression && right is not SqlColumnExpression)
            throw new NotSupportedException("Comparisons must contain a mapped entity property.");
        return new SqlBinaryExpression(left, operation, right);
    }

    private static SqlExpression NullComparison(SqlColumnExpression column, SqlBinaryOperator operation) => operation switch
    {
        SqlBinaryOperator.Equal => new SqlNullExpression(column, false),
        SqlBinaryOperator.NotEqual => new SqlNullExpression(column, true),
        _ => throw new NotSupportedException("Only == and != comparisons are supported for null values.")
    };

    private SqlExpression TranslateOperand(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member && IsEntityProperty(member)) return Column(member);
        if (TryReadCapturedValue(expression, out var value)) return new SqlParameterExpression(value);
        throw Unsupported(expression);
    }

    private SqlExpression TranslateMethodCall(MethodCallExpression expression)
    {
        if (expression.Object is MemberExpression stringMember && IsEntityProperty(stringMember) &&
            expression.Method.DeclaringType == typeof(string) && expression.Arguments.Count == 1)
        {
            var operation = expression.Method.Name switch
            {
                nameof(string.Contains) => SqlLikeOperator.Contains,
                nameof(string.StartsWith) => SqlLikeOperator.StartsWith,
                nameof(string.EndsWith) => SqlLikeOperator.EndsWith,
                _ => throw Unsupported(expression)
            };
            if (!TryReadCapturedValue(expression.Arguments[0], out var value) || value is not string)
                throw new NotSupportedException($"String.{expression.Method.Name} requires a constant or captured string value.");
            return new SqlLikeExpression(Column(stringMember), operation, value);
        }

        Expression? collectionExpression = null;
        Expression? itemExpression = null;
        if (expression.Method.Name == nameof(Enumerable.Contains) && expression.Arguments.Count == 2)
        {
            collectionExpression = expression.Arguments[0]; itemExpression = expression.Arguments[1];
        }
        else if (expression.Method.Name == nameof(List<object>.Contains) && expression.Object != null && expression.Arguments.Count == 1)
        {
            collectionExpression = expression.Object; itemExpression = expression.Arguments[0];
        }
        if (collectionExpression != null && StripConvert(itemExpression!) is MemberExpression item && IsEntityProperty(item))
        {
            if (!TryReadCapturedValue(collectionExpression, out var collection) || collection is not IEnumerable values || collection is string)
                throw new NotSupportedException("Collection Contains requires a constant or captured collection.");
            return new SqlInExpression(Column(item), values.Cast<object?>().ToArray());
        }
        throw Unsupported(expression);
    }

    private SqlColumnExpression Column(MemberExpression member) =>
        new(_map.GetProperty(member.Member.Name));

    private bool IsEntityProperty(MemberExpression member) =>
        member.Expression != null && StripConvert(member.Expression) == _entityParameter && member.Member is PropertyInfo;

    private static bool TryReadCapturedValue(Expression expression, out object? value)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression constant) { value = constant.Value; return true; }
        if (expression is NewArrayExpression array)
        {
            var values = new object?[array.Expressions.Count];
            for (var i = 0; i < values.Length; i++)
                if (!TryReadCapturedValue(array.Expressions[i], out values[i])) { value = null; return false; }
            value = values; return true;
        }
        if (expression is MemberExpression { Member: FieldInfo field } member && member.Expression != null &&
            TryReadCapturedValue(member.Expression, out var owner))
        {
            value = field.GetValue(owner);
            return true;
        }
        value = null; return false;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            expression = unary.Operand;
        return expression;
    }

    private static NotSupportedException Unsupported(Expression expression) =>
        new($"Expression node '{expression.NodeType}' ({expression}) is not supported in SQLite predicates.");
}
