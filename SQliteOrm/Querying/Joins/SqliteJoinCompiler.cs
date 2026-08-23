namespace SQliteOrm.Querying.Joins;

internal static class SqliteJoinCompiler
{
    internal static SqliteQueryCommand Compile(SqlJoinExpression join,
        IReadOnlyList<SqlProjectionColumn> columns, IReadOnlyList<SqlExpression> predicates)
    {
        var selections = string.Join(", ", columns.Select(column =>
            $"{Quote(column.TableAlias)}.{Quote(column.Source.ColumnName)} AS {Quote(column.ResultColumnName)}"));
        var joinKeyword = join.JoinType switch
        {
            SqlJoinType.Inner => "INNER JOIN",
            SqlJoinType.Left => "LEFT JOIN",
            _ => throw new NotSupportedException($"Join type '{join.JoinType}' is not supported.")
        };
        var sql = $"SELECT {selections} FROM {Quote(join.LeftMap.TableName)} AS {Quote(join.LeftAlias)} " +
                  $"{joinKeyword} {Quote(join.RightMap.TableName)} AS {Quote(join.RightAlias)} " +
                  $"ON {Quote(join.LeftAlias)}.{Quote(join.LeftKey.ColumnName)} = " +
                  $"{Quote(join.RightAlias)}.{Quote(join.RightKey.ColumnName)}";
        Dictionary<string, object>? parameters = null;
        if (predicates.Count > 0)
        {
            var root = predicates.Aggregate((left, right) =>
                new SqlBinaryExpression(left, SqlBinaryOperator.And, right));
            var compiled = new SqliteQueryCompiler().Compile(root);
            sql += $" WHERE {compiled.Sql}";
            parameters = compiled.Parameters.ToDictionary(item => item.Key, item => item.Value);
        }
        return new SqliteQueryCommand(sql + ";", parameters);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
