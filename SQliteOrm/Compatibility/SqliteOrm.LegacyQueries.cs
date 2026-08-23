using System.Linq.Expressions;
using System.Text;
using SQliteOrm.Mapping;

namespace SQliteOrm;

/// <summary>Source-compatible query and relation APIs retained for existing applications.</summary>
/// <remarks>New code should use Table&lt;T&gt;(), typed predicates, and typed joins.</remarks>
public partial class SqliteOrm
{
        /// <summary>Compatibility query using dictionary filters, ordering, and pagination.</summary>
        public List<T> GetAll<T>(
            Dictionary<Expression<Func<T, object>>, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And,
            int limit = 0,
            int offset = 0,
            Dictionary<Expression<Func<T, object>>, SortOrder>? orderBy = null
        ) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var tableName = QuoteIdentifier(EntityMapCache.Get<T>().TableName);
            Dictionary<string, object>? queryParameters = null;

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT * FROM {tableName}");


            if (conditions != null && conditions.Any())
            {
                var (conditionList, parameters) = BuildConditions(conditions);
                var whereClause = string.Join($" {logicalOperator} ", conditionList);

                queryBuilder.Append(" WHERE ");
                queryBuilder.Append(whereClause);

                queryParameters = parameters;
            }



            if (orderBy != null && orderBy.Any())
            {
                var orderByClause = string.Join(", ", orderBy.Select(o =>
                {
                    string columnName;
                    if (o.Key.Body is MemberExpression memberExpression)
                    {
                        columnName = QuoteIdentifier(EntityMapCache.Get<T>().GetProperty(memberExpression.Member.Name).ColumnName);
                    }
                    else if (o.Key.Body is UnaryExpression unaryExpression &&
                             unaryExpression.Operand is MemberExpression unaryMemberExpression)
                    {
                        columnName = QuoteIdentifier(EntityMapCache.Get<T>().GetProperty(unaryMemberExpression.Member.Name).ColumnName);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported expression type in orderBy.");
                    }

                    return $"{columnName} {o.Value}";
                }));

                queryBuilder.Append(" ORDER BY ");
                queryBuilder.Append(orderByClause);
            }



            if (limit > 0)
            {
                queryBuilder.Append(" LIMIT @__limit OFFSET @__offset");
                queryParameters ??= new Dictionary<string, object>();
                queryParameters["@__limit"] = limit;
                queryParameters["@__offset"] = offset;
            }


            return Query<T>(queryBuilder.ToString(), queryParameters);
        }


        /// <summary>Compatibility single-relation query using string metadata.</summary>
        public List<T> GetAllWithRelation<T, TRelated>(
            string relationFieldName,
            string relatedFieldName,
            string aliasName = "RelatedField",
            Dictionary<Expression<Func<T, object>>, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var mainMap = EntityMapCache.Get<T>();
            var relatedMap = EntityMapCache.Get<TRelated>();
            var mainTableName = QuoteIdentifier(mainMap.TableName);
            var relatedTableName = QuoteIdentifier(relatedMap.TableName);
            relationFieldName = QuoteIdentifier(mainMap.GetProperty(relationFieldName).ColumnName);
            relatedFieldName = QuoteIdentifier(relatedMap.GetProperty(relatedFieldName).ColumnName);
            aliasName = QuoteIdentifier(aliasName);

            // Base query with INNER JOIN
            var query = $@"SELECT t.*, r.{relatedFieldName} AS {aliasName}
                    FROM {mainTableName} t
                    INNER JOIN {relatedTableName} r
                    ON t.{relationFieldName} = r.{QuoteIdentifier(GetRequiredKey<TRelated>().ColumnName)}";


            if (conditions != null && conditions.Any())
            {
                var parameters = new Dictionary<string, object>();
                var whereClause = string.Join(
                    $" {logicalOperator} ",
                    conditions.Select((condition, index) =>
                    {
                        var propertyName = mainMap.GetProperty(ExtractKeyName(condition.Key)).ColumnName;
                        if (condition.Value == null || condition.Value == DBNull.Value)
                            return $"t.{QuoteIdentifier(propertyName)} IS NULL";

                        var parameterName = $"@p{index}";
                        parameters[parameterName] = condition.Value;
                        return $"t.{QuoteIdentifier(propertyName)} = {parameterName}";
                    }));
                query += $" WHERE {whereClause}";
                return Query<T>(query, parameters);
            }


            return Query<T>(query);
        }

        /// <summary>Compatibility multi-relation query using string and tuple metadata.</summary>
        public List<T> GetAllWithRelations<T>(
            string mainTableAlias,
            Dictionary<string, (string relationFieldName, string relatedTableName, string tableRelationExistAlias)> relationships,
            List<(string tableAlias, string columnName, string aliasName)>? additionalColumns = null,
            Dictionary<string, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var mainTableName = QuoteIdentifier(EntityMapCache.Get<T>().TableName);
            mainTableAlias = QuoteIdentifier(mainTableAlias);

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT {mainTableAlias}.*");


            if (additionalColumns != null && additionalColumns.Any())
            {
                foreach (var column in additionalColumns)
                {
                    queryBuilder.Append($", {QuoteIdentifier(column.tableAlias)}.{QuoteIdentifier(column.columnName)} AS {QuoteIdentifier(column.aliasName)}");
                }
            }



            queryBuilder.Append($" FROM {mainTableName} {mainTableAlias}");



            if (relationships != null && relationships.Any())
            {
                foreach (var relationship in relationships)
                {
                    var tableAlias = relationship.Key;
                    var relationFieldName = relationship.Value.relationFieldName;
                    var relatedTableName = relationship.Value.relatedTableName;
                    var tableRelationExistAlias = relationship.Value.tableRelationExistAlias;

                    queryBuilder.Append($" INNER JOIN {QuoteIdentifier(relatedTableName)} {QuoteIdentifier(tableAlias)} " +
                                        $"ON {QuoteIdentifier(tableRelationExistAlias)}.{QuoteIdentifier(relationFieldName)} = {QuoteIdentifier(tableAlias)}.{QuoteIdentifier("Id")}");
                }
            }



            Dictionary<string, object>? parameters = null;
            if (conditions != null && conditions.Any())
            {
                parameters = new Dictionary<string, object>();
                var indexedConditions = conditions.Select((condition, index) =>
                {
                    if (condition.Value == null || condition.Value == DBNull.Value)
                        return $"{mainTableAlias}.{QuoteIdentifier(condition.Key)} IS NULL";

                    var parameterName = $"@p{index}";
                    parameters[parameterName] = condition.Value;
                    return $"{mainTableAlias}.{QuoteIdentifier(condition.Key)} = {parameterName}";
                });
                var whereClause = string.Join(
                    $" {logicalOperator} ",
                    indexedConditions);

                queryBuilder.Append($" WHERE {whereClause}");
            }


            return Query<T>(queryBuilder.ToString(), parameters);
        }



        /// <summary>Compatibility multi-relation query using expression and tuple metadata.</summary>
        public List<T> GetAllWithRelations<T>(
            string mainTableAlias,
            List<(Expression<Func<T, object>> relationExpression, string relatedTableName, string tableAlias)> relationships,
            List<(string tableAlias, Expression<Func<T, object>> columnExpression, string aliasName)>? additionalColumns = null,
            Dictionary<Expression<Func<T, object>>, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var map = EntityMapCache.Get<T>();
            var mainTableName = QuoteIdentifier(map.TableName);
            mainTableAlias = QuoteIdentifier(mainTableAlias);

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT {mainTableAlias}.*");


            if (additionalColumns != null && additionalColumns.Any())
            {
                foreach (var column in additionalColumns)
                {
                    string columnName;
                    if (column.columnExpression.Body is MemberExpression memberExpression)
                    {
                        columnName = map.GetProperty(memberExpression.Member.Name).ColumnName;
                    }
                    else if (column.columnExpression.Body is UnaryExpression unaryExpression &&
                             unaryExpression.Operand is MemberExpression unaryMemberExpression)
                    {
                        columnName = map.GetProperty(unaryMemberExpression.Member.Name).ColumnName;
                    }
                    else
                    {
                        throw new ArgumentException("Invalid lambda expression", nameof(column.columnExpression));
                    }

                    queryBuilder.Append($", {QuoteIdentifier(column.tableAlias)}.{QuoteIdentifier(columnName)} AS {QuoteIdentifier(column.aliasName)}");
                }
            }



            queryBuilder.Append($" FROM {mainTableName} {mainTableAlias}");



            if (relationships != null && relationships.Any())
            {
                foreach (var relationship in relationships)
                {
                    string relationFieldName;
                    if (relationship.relationExpression.Body is MemberExpression memberExpression)
                    {
                        relationFieldName = map.GetProperty(memberExpression.Member.Name).ColumnName;
                    }
                    else if (relationship.relationExpression.Body is UnaryExpression unaryExpression &&
                             unaryExpression.Operand is MemberExpression unaryMemberExpression)
                    {
                        relationFieldName = map.GetProperty(unaryMemberExpression.Member.Name).ColumnName;
                    }
                    else
                    {
                        throw new ArgumentException("Invalid lambda expression", nameof(relationship.relationExpression));
                    }

                    var tableAlias = QuoteIdentifier(relationship.tableAlias);
                    queryBuilder.Append($" INNER JOIN {QuoteIdentifier(relationship.relatedTableName)} {tableAlias} " +
                                        $"ON {mainTableAlias}.{QuoteIdentifier(relationFieldName)} = {tableAlias}.{QuoteIdentifier("Id")}");
                }
            }



            Dictionary<string, object>? parameters = null;
            if (conditions != null && conditions.Any())
            {
                parameters = new Dictionary<string, object>();
                var whereClause = string.Join(
                    $" {logicalOperator} ",
                    conditions.Select((condition, index) =>
                    {
                        var propertyName = map.GetProperty(ExtractKeyName(condition.Key)).ColumnName;
                        if (condition.Value == null || condition.Value == DBNull.Value)
                            return $"{mainTableAlias}.{QuoteIdentifier(propertyName)} IS NULL";

                        var parameterName = $"@p{index}";
                        parameters[parameterName] = condition.Value;
                        return $"{mainTableAlias}.{QuoteIdentifier(propertyName)} = {parameterName}";
                    }));

                queryBuilder.Append($" WHERE {whereClause}");
            }


            return Query<T>(queryBuilder.ToString(), parameters);
        }


}
