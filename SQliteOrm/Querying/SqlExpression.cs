using SQliteOrm.Mapping;

namespace SQliteOrm.Querying;

internal abstract record SqlExpression;

internal enum SqlBinaryOperator
{
    Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, And, Or
}

internal enum SqlLikeOperator { Contains, StartsWith, EndsWith }

internal sealed record SqlBinaryExpression(
    SqlExpression Left, SqlBinaryOperator Operator, SqlExpression Right) : SqlExpression;

internal sealed record SqlNotExpression(SqlExpression Operand) : SqlExpression;

internal sealed record SqlColumnExpression(PropertyMap Property, string? TableAlias = null) : SqlExpression;

internal sealed record SqlParameterExpression(object? Value) : SqlExpression;

internal sealed record SqlNullExpression(SqlColumnExpression Column, bool IsNegated) : SqlExpression;

internal sealed record SqlLikeExpression(
    SqlColumnExpression Column, SqlLikeOperator Operator, object? Value) : SqlExpression;

internal sealed record SqlInExpression(
    SqlColumnExpression Column, IReadOnlyList<object?> Values) : SqlExpression;

internal sealed record SqlBooleanConstantExpression(bool Value) : SqlExpression;
