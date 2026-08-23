using System.Linq.Expressions;
using System.Text;
using SQliteOrm.Mapping;

namespace SQliteOrm;

public partial class SqliteOrm
{
        /// <summary>Finds an entity by its mapped integer primary key.</summary>
        public T? FindById<T>(int id) where T : new() => Find<T, int>(id);

        /// <summary>Finds an entity by its mapped primary key.</summary>
        public T? Find<T, TKey>(TKey key) where T : new() =>
            FindOneByKey<T>(CreateKeySelector<T>(GetRequiredKey<T>().PropertyName), key);


        /// <summary>Compatibility lookup by a selected property and string value.</summary>
        public T? FindOneByKey<T>(Expression<Func<T, object>> keySelector, string keyValue) where T : new() =>
            FindOneByKey<T>(keySelector, (object?)keyValue);

        /// <summary>Compatibility lookup by a selected property and value.</summary>
        public T? FindOneByKey<T>(Expression<Func<T, object>> keySelector, object? keyValue) where T : new() =>
            FindOneByKey<T>(new Dictionary<Expression<Func<T, object>>, object> { { keySelector, keyValue! } });


        /// <summary>Compatibility lookup using dictionary conditions.</summary>
        public T? FindOneByKey<T>(Dictionary<Expression<Func<T, object>>, object> conditions,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();

            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var tableName = QuoteIdentifier(EntityMapCache.Get<T>().TableName);
            var query = $"SELECT * FROM {tableName}";
            Dictionary<string, object>? dictionaryParameter;
            if (conditions != null && conditions.Any())
            {
                var (conditionList, parameters) = BuildConditions(conditions);
                var whereClause = string.Join($" {logicalOperator} ", conditionList);
                query += " WHERE " + whereClause;
                dictionaryParameter = parameters;
            }
            else
            {
                dictionaryParameter = null;
            }

            var result = Query<T>(query, dictionaryParameter);
            return result.FirstOrDefault();
        }

        /// <summary>Compatibility count using dictionary conditions.</summary>
        public int Count<T>(Dictionary<Expression<Func<T, object>>, object> conditions) where T : new() =>
            Count(conditions, LogicalOperator.And);

        /// <summary>Compatibility count using dictionary conditions and a logical operator.</summary>
        public int Count<T>(Dictionary<Expression<Func<T, object>>, object> conditions, LogicalOperator conditionType)
            where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var tableName = QuoteIdentifier(EntityMapCache.Get<T>().TableName);

            // Base query
            var query = $"SELECT COUNT(*) FROM {tableName}";

            // Add conditions if provided
            if (conditions != null && conditions.Any())
            {
                var (conditionList, parameters) = BuildConditions(conditions);
                var whereClause = string.Join($" {logicalOperator} ", conditionList);
                query += " WHERE " + whereClause;

                // Use ExecuteScalar to get the count
                return ExecuteScalar<int>(query, parameters);
            }

            return Count<T>();
        }

        /// <summary>Returns the total number of mapped rows.</summary>
        public int Count<T>() where T : new()
        {
            ValidateType<T>();
            var tableName = QuoteIdentifier(EntityMapCache.Get<T>().TableName);
            return ExecuteScalar<int>($"SELECT COUNT(*) FROM {tableName};");
        }

        /// <summary>Checks for an entity by its mapped integer primary key.</summary>
        public bool Exists<T>(int id) where T : new() => Exists<T, int>(id);

        /// <summary>Checks for an entity by its mapped primary key.</summary>
        public bool Exists<T, TKey>(TKey key) where T : new() =>
            Exists<T>(CreateKeySelector<T>(GetRequiredKey<T>().PropertyName), key);


        /// <summary>Compatibility existence check by a selected property and string value.</summary>
        public bool Exists<T>(Expression<Func<T, object>> keySelector, string keyValue) where T : new()
            => Exists<T>(keySelector, (object?)keyValue);

        /// <summary>Compatibility existence check by a selected property and value.</summary>
        public bool Exists<T>(Expression<Func<T, object>> keySelector, object? keyValue) where T : new()
        {
            ValidateType<T>();

            var keyName = ExtractKeyName(keySelector);
            return ExistsByValue<T>(keyName, keyValue);
        }


}
