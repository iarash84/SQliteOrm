using SQliteOrm.TypeMapping;

namespace SQliteOrm.Querying;

internal sealed record SqlitePredicate(string Sql, IReadOnlyDictionary<string, object> Parameters);

internal sealed class SqliteQueryCompiler
{
    private readonly Dictionary<string, object> _parameters = new();

    internal SqlitePredicate Compile(SqlExpression expression)
    {
        _parameters.Clear();
        return new SqlitePredicate(CompileExpression(expression),
            new Dictionary<string, object>(_parameters));
    }

    private string CompileExpression(SqlExpression expression) => expression switch
    {
        SqlColumnExpression column => column.TableAlias == null
            ? Quote(column.Property.ColumnName)
            : $"{Quote(column.TableAlias)}.{Quote(column.Property.ColumnName)}",
        SqlParameterExpression parameter => AddParameter(parameter.Value),
        SqlBinaryExpression binary => $"({CompileExpression(binary.Left)} {Operator(binary.Operator)} {CompileExpression(binary.Right)})",
        SqlNotExpression not => $"(NOT {CompileExpression(not.Operand)})",
        SqlNullExpression nullCheck => $"({CompileExpression(nullCheck.Column)} IS {(nullCheck.IsNegated ? "NOT " : "")}NULL)",
        SqlLikeExpression like => CompileLike(like),
        SqlInExpression @in => CompileIn(@in),
        SqlBooleanConstantExpression constant => constant.Value ? "(1 = 1)" : "(1 = 0)",
        _ => throw new NotSupportedException($"SQL AST node '{expression.GetType().Name}' is not supported.")
    };

    private string CompileLike(SqlLikeExpression expression)
    {
        var escaped = EscapeLike((string)expression.Value!);
        var pattern = expression.Operator switch
        {
            SqlLikeOperator.Contains => $"%{escaped}%",
            SqlLikeOperator.StartsWith => $"{escaped}%",
            SqlLikeOperator.EndsWith => $"%{escaped}",
            _ => throw new NotSupportedException()
        };
        return $"({CompileExpression(expression.Column)} LIKE {AddParameter(pattern)} ESCAPE '\\')";
    }

    private string CompileIn(SqlInExpression expression)
    {
        if (expression.Values.Count == 0) return "(1 = 0)";
        return $"({CompileExpression(expression.Column)} IN ({string.Join(", ", expression.Values.Select(AddParameter))}))";
    }

    private string AddParameter(object? value)
    {
        var name = $"@p{_parameters.Count}";
        _parameters[name] = SqliteTypeHandler.ToDatabase(value);
        return name;
    }

    private static string Operator(SqlBinaryOperator operation) => operation switch
    {
        SqlBinaryOperator.Equal => "=", SqlBinaryOperator.NotEqual => "<>",
        SqlBinaryOperator.GreaterThan => ">", SqlBinaryOperator.GreaterThanOrEqual => ">=",
        SqlBinaryOperator.LessThan => "<", SqlBinaryOperator.LessThanOrEqual => "<=",
        SqlBinaryOperator.And => "AND", SqlBinaryOperator.Or => "OR",
        _ => throw new NotSupportedException()
    };

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
