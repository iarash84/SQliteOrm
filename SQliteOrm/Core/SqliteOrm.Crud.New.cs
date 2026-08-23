using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using SQliteOrm.Mapping;
using SQliteOrm.TypeMapping;
using SQliteOrm.Querying;
using SQliteOrm.Transactions;
using SQliteOrm.Persistence;
using SQliteOrm.Migrations;
using SQliteOrm.RawSql;

namespace SQliteOrm
{
    /// <summary>Provides metadata-driven, parameterized SQLite persistence for one database configuration.</summary>
    public partial class SqliteOrm : IDisposable
    {
        private const string MigrationHistoryTable = "__SQliteOrmMigrations";
        private static readonly HashSet<string> ForeignKeyActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "NO ACTION", "RESTRICT", "SET NULL", "SET DEFAULT", "CASCADE"
        };
        private readonly string _connectionString;
        private readonly SqliteOrmOptions _options;
        private bool _disposed;

        private readonly object _writeLock = new();
        private readonly AsyncLocal<TransactionContext?> _transactionContext = new();

        /// <summary>Starts a deferred strongly typed query.</summary>
        public SqliteQuery<T> Table<T>() where T : new() => new(this);

        /// <summary>Starts a parameterized partial update builder.</summary>
        public SqliteUpdate<T> Update<T>() => new(this);

        /// <summary>Returns the first entity matching a predicate, or the default value.</summary>
        public T? FirstOrDefault<T>(Expression<Func<T, bool>> predicate) where T : new() =>
            Table<T>().Where(predicate).FirstOrDefault();

        /// <summary>Determines whether any entity matches a predicate.</summary>
        public bool Any<T>(Expression<Func<T, bool>> predicate) where T : new() =>
            Table<T>().Where(predicate).Any();



        /// <summary>Creates an ORM instance for a SQLite connection string.</summary>
        public SqliteOrm(string connectionString) : this(new SqliteOrmOptions { ConnectionString = connectionString }) { }

        /// <summary>Creates an ORM instance with explicit connection and command options.</summary>
        public SqliteOrm(SqliteOrmOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(options));
            if (options.BusyTimeout is { } busyTimeout && busyTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "Busy timeout cannot be negative.");
            if (options.CommandTimeout is < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Command timeout cannot be negative.");
            _options = new SqliteOrmOptions
            {
                ConnectionString = options.ConnectionString,
                EnableForeignKeys = options.EnableForeignKeys,
                EnableWal = options.EnableWal,
                BusyTimeout = options.BusyTimeout,
                CommandTimeout = options.CommandTimeout
            };
            var builder = new SQLiteConnectionStringBuilder(options.ConnectionString)
            {
                ForeignKeys = options.EnableForeignKeys
            };
            _connectionString = builder.ConnectionString;
        }



        /// <summary>Creates the mapped table when it does not already exist.</summary>
        public void CreateTable<T>() where T : new()
        {
            ValidateType<T>();

            var map = EntityMapCache.Get<T>();
            var tableName = QuoteIdentifier(map.TableName);
            var properties = map.Properties;
            var columns = new List<string>();
            var tableConstraints = new List<string>();

            foreach (var property in properties)
            {
                var columnName = QuoteIdentifier(property.ColumnName);
                var columnType = property.SqliteType;
                var isPrimaryKey = property.IsPrimaryKey;
                var isRequired = property.IsRequired;
                var isUnique = property.IsUnique;
                var foreignKeyAttr = property.ForeignKey;

                var columnDefinition = $"{columnName} {columnType}" +
                                       (isPrimaryKey ? " PRIMARY KEY" : "") +
                                       (property.IsAutoIncrement ? " AUTOINCREMENT" : "") +
                                       (isRequired ? " NOT NULL" : "") +
                                       (isUnique ? " UNIQUE" : "");

                if (foreignKeyAttr != null)
                {
                    var referencedTable = QuoteIdentifier(foreignKeyAttr.Name);
                    var referencedKeyColumn = EntityMapCache.ResolveReferencedKeyColumn(typeof(T), foreignKeyAttr.Name);
                    var onDeleteAction = ValidateForeignKeyAction(foreignKeyAttr.OnDelete);
                    var onUpdateAction = ValidateForeignKeyAction(foreignKeyAttr.OnUpdate);
                    tableConstraints.Add(
                        $"FOREIGN KEY({columnName}) REFERENCES {referencedTable}({QuoteIdentifier(referencedKeyColumn)}) ON DELETE {onDeleteAction} ON UPDATE {onUpdateAction}");
                }

                columns.Add(columnDefinition);
            }

            var query = $"CREATE TABLE IF NOT EXISTS {tableName} ({string.Join(", ", columns.Concat(tableConstraints))});";
            ExecuteNonQuery(query);
        }


        /// <summary>Inserts one entity and assigns a generated integer key when configured.</summary>
        public int Insert<T>(T obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            ValidateType<T>();
            var map = EntityMapCache.Get<T>();
            var tableName = QuoteIdentifier(map.TableName);
            // Get properties of the type and exclude those marked with [Key] or [NotMapped]
            var properties = map.Properties.Where(p => !p.IsDatabaseGenerated)
                .ToArray();

            var insert = properties.Length == 0
                ? $"INSERT INTO {tableName} DEFAULT VALUES"
                : $"INSERT INTO {tableName} ({string.Join(", ", properties.Select(p => QuoteIdentifier(p.ColumnName)))}) " +
                  $"VALUES ({string.Join(", ", properties.Select(p => $"@{p.PropertyName}"))})";
            var query = $"{insert}; SELECT last_insert_rowid();";
            var parameters = properties.ToDictionary(p => $"@{p.PropertyName}", p => p.GetValue(obj!) ?? DBNull.Value);
            var insertedId = ExecuteScalar<long>(query, parameters);
            if (map.Key is { IsDatabaseGenerated: true } generatedKey)
                generatedKey.SetValue(obj!, SqliteTypeHandler.FromDatabase(insertedId, generatedKey.ClrType));
            return checked((int)insertedId);
        }

        /// <summary>Inserts a list atomically, reusing an active transaction when present.</summary>
        public void Insert<T>(List<T> objectList)
        {
            if (objectList == null) throw new ArgumentNullException(nameof(objectList));
            if (objectList.Count == 0) return;
            void InsertItems()
            {
                foreach (var obj in objectList)
                {
                    if (obj == null) throw new ArgumentException("The list cannot contain null items.", nameof(objectList));
                    Insert(obj);
                }
            }
            if (_transactionContext.Value is { IsActive: true }) InsertItems();
            else Transaction(_ => InsertItems());
        }


        /// <summary>Compatibility overload for atomic upsert using a selected conflict target.</summary>
        public void Upsert<T>(Expression<Func<T, object>> keySelector, T obj) where T : new() =>
            Upsert(obj, keySelector);

        /// <summary>Atomically inserts or updates using a mapped key or unique conflict target.</summary>
        public void Upsert<T, TConflict>(T obj, Expression<Func<T, TConflict>> conflictOn) where T : new()
        {
            ArgumentNullException.ThrowIfNull(obj);
            ArgumentNullException.ThrowIfNull(conflictOn);
            var conflictName = ExtractMemberName(conflictOn);
            var command = UpsertCommandBuilder.Build(obj, conflictName);
            if (command.GeneratedKey == null)
            {
                ExecuteNonQueryAffected(command.Sql, command.Parameters);
                return;
            }
            var key = ExecuteScalar<long>(command.Sql, command.Parameters);
            command.GeneratedKey.SetValue(obj!, SqliteTypeHandler.FromDatabase(key, command.GeneratedKey.ClrType));
        }

        /// <summary>Atomically upserts using the mapped primary key.</summary>
        public void Upsert<T>(T obj) where T : new() =>
            Upsert(obj, CreateKeySelector<T>(GetRequiredKey<T>().PropertyName));


        /// <summary>Updates an entity using a selected mapped property as the match key.</summary>
        public void Update<T>(Expression<Func<T, object>> keySelector, T obj)
        {
            ValidateType<T>();
            var map = EntityMapCache.Get<T>();
            var tableName = QuoteIdentifier(map.TableName);

            var keyName = ExtractKeyName(keySelector);

            // Get properties of the type
            var propertyInfos = map.Properties;
            var idProperty = propertyInfos.FirstOrDefault(p => p.PropertyName.Equals(keyName, StringComparison.OrdinalIgnoreCase));
            if (idProperty == null) throw new ArgumentException($"Property '{keyName}' is not mapped.", nameof(keySelector));

            // Prepare update query excluding [NotMapped] and Id and checkColumnName
            var updates = string.Join(", ", propertyInfos
                .Where(p => !p.IsDatabaseGenerated &&
                            !p.PropertyName.Equals(keyName, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{QuoteIdentifier(p.ColumnName)} = @{p.PropertyName}"));

            if (string.IsNullOrEmpty(updates))
                throw new InvalidOperationException("No updatable properties were found.");

            var keyProperty = map.GetProperty(keyName);
            var query = $"UPDATE {tableName} SET {updates} WHERE {QuoteIdentifier(keyProperty.ColumnName)} = @{keyName};";

            var parameters = propertyInfos.ToDictionary(p => $"@{p.PropertyName}", p => p.GetValue(obj!) ?? DBNull.Value);
            ExecuteNonQuery(query, parameters);
        }

        /// <summary>Updates an entity using its mapped primary key.</summary>
        public void Update<T>(T obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var key = GetRequiredKey<T>();
            Update(CreateKeySelector<T>(key.PropertyName), obj);
        }


        /// <summary>Deletes by the compatibility integer primary-key overload.</summary>
        public void Delete<T>(int id) => Delete<T, int>(id);

        /// <summary>Deletes by the mapped primary key.</summary>
        public void Delete<T, TKey>(TKey key) => Delete(CreateKeySelector<T>(GetRequiredKey<T>().PropertyName), key);

        /// <summary>Deletes rows matching a required parameterized predicate.</summary>
        public int Delete<T>(Expression<Func<T, bool>> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ValidateType<T>();
            var compiled = PredicateCompiler<T>.Compile(new[] { predicate });
            var tableName = QuoteIdentifier(EntityMapCache.Get<T>().TableName);
            return ExecuteNonQueryAffected($"DELETE FROM {tableName} WHERE {compiled.Sql};", compiled.Parameters);
        }

        /// <summary>Explicitly deletes every row from the mapped table.</summary>
        public int DeleteAll<T>()
        {
            ValidateType<T>();
            return ExecuteNonQueryAffected($"DELETE FROM {QuoteIdentifier(EntityMapCache.Get<T>().TableName)};");
        }

        /// <summary>Compatibility overload that deletes by a selected property and string value.</summary>
        public void Delete<T>(Expression<Func<T, object>> keySelector, string keyValue)
            => Delete<T>(keySelector, (object?)keyValue);

        /// <summary>Compatibility overload that deletes by a selected property and value.</summary>
        public void Delete<T>(Expression<Func<T, object>> keySelector, object? keyValue)
        {
            ValidateType<T>();
            var keyName = ExtractKeyName(keySelector);

            // Build the SQL DELETE query with the extracted property name
            var map = EntityMapCache.Get<T>();
            var tableName = QuoteIdentifier(map.TableName);
            var query = $"DELETE FROM {tableName} WHERE {QuoteIdentifier(map.GetProperty(keyName).ColumnName)} = @{keyName};";

            // Execute the query with the appropriate parameter
            ExecuteNonQuery(query, new Dictionary<string, object> { { $"@{keyName}", keyValue ?? DBNull.Value } });
        }

        private static void ValidateType<T>() => _ = EntityMapCache.Get<T>().Properties.Any()
            ? true
            : throw new InvalidOperationException("Type T must have at least one property.");

        private static PropertyMap GetRequiredKey<T>() => EntityMapCache.Get<T>().Key ??
            throw new InvalidOperationException($"Type '{typeof(T).Name}' does not define a [Key] property.");


        private bool ExistsByValue<T>(string keyName, object? keyValue) where T : new()
        {
            var map = EntityMapCache.Get<T>();
            var tableName = QuoteIdentifier(map.TableName);
            var columnName = QuoteIdentifier(map.GetProperty(keyName).ColumnName);
            var query = keyValue is null or DBNull
                ? $"SELECT 1 FROM {tableName} WHERE {columnName} IS NULL LIMIT 1;"
                : $"SELECT 1 FROM {tableName} WHERE {columnName} = @{keyName} LIMIT 1;";
            var parameters = keyValue is null or DBNull
                ? null
                : new Dictionary<string, object> { [$"@{keyName}"] = keyValue };
            return ExecuteScalar<int>(query, parameters) > 0;
        }

        private static string GetSqlLogicalOperator(LogicalOperator conditionType) => conditionType switch
        {
            LogicalOperator.And => "AND",
            LogicalOperator.Or => "OR",
            _ => throw new ArgumentOutOfRangeException(nameof(conditionType), conditionType, "Unsupported logical operator.")
        };

        private static string ValidateForeignKeyAction(string action)
        {
            var normalized = string.IsNullOrWhiteSpace(action)
                ? "NO ACTION"
                : string.Join(" ", action.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (!ForeignKeyActions.Contains(normalized))
                throw new ArgumentException($"Unsupported foreign-key action '{action}'.", nameof(action));
            return normalized.ToUpperInvariant();
        }

        private static Expression<Func<T, object>> CreateKeySelector<T>(string propertyName)
        {
            var parameter = Expression.Parameter(typeof(T), "g");
            var property = Expression.Property(parameter, propertyName);
            var convertedProperty = Expression.Convert(property, typeof(object));
            var lambda = Expression.Lambda<Func<T, object>>(convertedProperty, parameter);
            return lambda;
        }

        private static (List<string> conditionList, Dictionary<string, object> parameters) BuildConditions<T>(
            Dictionary<Expression<Func<T, object>>, object> conditions)
        {
            var conditionList = new List<string>();
            var parameters = new Dictionary<string, object>();

            foreach (var condition in conditions)
            {
                if (condition.Key.Body is MemberExpression memberExpression)
                {
                    var columnName = QuoteIdentifier(EntityMapCache.Get<T>().GetProperty(memberExpression.Member.Name).ColumnName);
                    var parameterName = $"@{memberExpression.Member.Name}";
                    if (condition.Value == null || condition.Value == DBNull.Value)
                        conditionList.Add($"{columnName} IS NULL");
                    else
                    {
                        conditionList.Add($"{columnName} = {parameterName}");
                        parameters[parameterName] = condition.Value;
                    }
                }
                else if (condition.Key.Body is UnaryExpression unaryExpression &&
                         unaryExpression.Operand is MemberExpression unaryMemberExpression)
                {
                    var columnName = QuoteIdentifier(EntityMapCache.Get<T>().GetProperty(unaryMemberExpression.Member.Name).ColumnName);
                    var parameterName = $"@{unaryMemberExpression.Member.Name}";
                    if (condition.Value == null || condition.Value == DBNull.Value)
                        conditionList.Add($"{columnName} IS NULL");
                    else
                    {
                        conditionList.Add($"{columnName} = {parameterName}");
                        parameters[parameterName] = condition.Value;
                    }
                }
                else
                {
                    throw new InvalidOperationException("Unsupported expression type in conditions.");
                }
            }

            return (conditionList, parameters);
        }

        private static string ExtractKeyName<T>(Expression<Func<T, object>> keySelector)
        {
            if (keySelector == null)
                throw new ArgumentNullException(nameof(keySelector), "Key selector must be provided.");
            return ExtractMemberName(keySelector);
        }

        private static string ExtractMemberName<T, TMember>(Expression<Func<T, TMember>> selector)
        {
            ArgumentNullException.ThrowIfNull(selector);
            return selector.Body switch
            {
                // Extract the property name from a MemberExpression (direct property access)
                MemberExpression memberExpression => memberExpression.Member.Name,

                // Handle UnaryExpression (casting) where the operand is a MemberExpression
                UnaryExpression { Operand: MemberExpression unaryMemberExpression } => unaryMemberExpression.Member.Name,

                // Throw exception for any invalid expression type
                _ => throw new ArgumentException("Selector must directly reference a mapped property.", nameof(selector))
            };
        }

    }
}




