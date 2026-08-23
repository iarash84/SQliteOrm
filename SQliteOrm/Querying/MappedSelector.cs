using System.Linq.Expressions;
using SQliteOrm.Mapping;

namespace SQliteOrm.Querying;

internal static class MappedSelector
{
    internal static PropertyMap Resolve<T, TKey>(Expression<Func<T, TKey>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;
        if (body is not MemberExpression { Expression: ParameterExpression parameter } member ||
            parameter != selector.Parameters[0])
            throw new NotSupportedException(
                $"Selector '{selector}' is not supported. Select one mapped property directly.");
        return EntityMapCache.Get<T>().GetProperty(member.Member.Name);
    }
}
