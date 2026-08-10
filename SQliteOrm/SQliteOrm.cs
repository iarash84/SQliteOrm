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

namespace SQliteOrm
{
    // Sample ForeignKey attribute
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ForeignKeyAttribute : Attribute
    {
        public string Name { get; }
        public string OnDelete { get; set; } = "NO ACTION";
        public string OnUpdate { get; set; } = "NO ACTION";
        public ForeignKeyAttribute(string name) => Name = name;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class UniqueAttribute : Attribute { }

    public enum SortOrder
    {
        ASC,
        DESC
    }

    /// <summary>Specifies how multiple query conditions are combined.</summary>
    public enum LogicalOperator
    {
        And,
        Or
    }


    /// <summary>
    /// امکانات ایجاد جدول و انجام عملیات متداول CRUD را برای پایگاه داده SQLite فراهم می‌کند.
    /// </summary>
    public class SqLiteOrm
    {
        /// <summary>
        /// ویژگی‌های نگاشت‌شونده هر نوع را برای جلوگیری از بازتاب مکرر ذخیره می‌کند.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> MappedPropertiesCache = new();

        /// <summary>
        /// مجموعه عملیات معتبر برای بخش‌های <c>ON DELETE</c> و <c>ON UPDATE</c> کلید خارجی است.
        /// </summary>
        private static readonly HashSet<string> ForeignKeyActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "NO ACTION", "RESTRICT", "SET NULL", "SET DEFAULT", "CASCADE"
        };
        /// <summary>
        /// نمونه سراسری و مقداردهی‌شده کلاس را نگه می‌دارد.
        /// </summary>
        private static SqLiteOrm? _instance;

        /// <summary>
        /// رشته اتصال مورد استفاده برای باز کردن اتصال‌های SQLite است.
        /// </summary>
        private readonly string _connectionString;

        /// <summary>
        /// دسترسی هم‌زمان به عملیات نوشتن در پایگاه داده را همگام‌سازی می‌کند.
        /// </summary>
        private readonly object _writeLock = new();


        /// <summary>
        /// نمونه مقداردهی‌شده <see cref="SqLiteOrm"/> را بازمی‌گرداند.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// اگر پیش از فراخوانی <see cref="Initialize(string)"/> به این ویژگی دسترسی شود، پرتاب می‌شود.
        /// </exception>
        public static SqLiteOrm Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException(
                        "SqLiteOrm is not initialized. Call SqLiteOrm.Initialize(databasePath) first.");

                return _instance;
            }
        }

        /// <summary>
        /// نمونه سراسری ORM را با مسیر پایگاه داده مشخص‌شده مقداردهی می‌کند.
        /// </summary>
        /// <param name="databasePath">مسیر فایل پایگاه داده SQLite.</param>
        /// <exception cref="ArgumentException">اگر مسیر پایگاه داده خالی یا فقط شامل فاصله باشد، پرتاب می‌شود.</exception>
        public static void Initialize(string databasePath)
        {
            Interlocked.Exchange(ref _instance, new SqLiteOrm(databasePath));
        }

        /// <summary>
        /// یک نمونه جدید از ORM را ایجاد و رشته اتصال SQLite آن را پیکربندی می‌کند.
        /// </summary>
        /// <param name="databasePath">مسیر فایل پایگاه داده SQLite.</param>
        /// <exception cref="ArgumentException">اگر مسیر پایگاه داده خالی یا فقط شامل فاصله باشد، پرتاب می‌شود.</exception>
        private SqLiteOrm(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException(
                    "Database path cannot be null or empty.",
                    nameof(databasePath));

            _connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = databasePath,
                Version = 3,
                ForeignKeys = true
            }.ConnectionString;
        }



        /// <summary>
        /// متد برای ایجاد جدول برای نوع داده‌ای خاص
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که برای ایجاد جدول استفاده می‌شود</typeparam>
        /// <example>
        /// <code>
        /// public class User
        /// {
        ///    [Key]
        ///    public int Id { get; set; }
        ///    [Required]
        ///    [Unique]
        ///    [MaxLength(50)]
        ///    public string Name { get; set; }
        /// }
        /// public class Order
        /// {
        ///    [Key]
        ///    public int Id { get; set; }
        ///    [ForeignKey("User", OnDelete = "CASCADE", OnUpdate = "CASCADE")]
        ///    public int UserId { get; set; }
        ///    public double Amount { get; set; }
        ///    [NotMapped]
        ///    public string UserName { get; set; }
        /// }
        /// //Create tables:
        /// CreateTable<User>();
        /// CreateTable<Order>();
        /// </code>
        /// </example>
        public void CreateTable<T>() where T : new()
        {
            ValidateType<T>();

            var tableName = QuoteIdentifier(typeof(T).Name);
            var properties = GetMappedProperties(typeof(T));
            var columns = new List<string>();
            var tableConstraints = new List<string>();

            foreach (var property in properties)
            {
                var columnName = QuoteIdentifier(property.Name);
                var columnType = GetSqLiteType(property.PropertyType);
                var isPrimaryKey = property.GetCustomAttributes(typeof(KeyAttribute), false).Any();
                var isRequired = property.GetCustomAttributes(typeof(RequiredAttribute), false).Any();
                var isUnique = property.GetCustomAttributes(typeof(UniqueAttribute), false).Any();
                var foreignKeyAttr = property.GetCustomAttributes(typeof(ForeignKeyAttribute), false)
                    .FirstOrDefault() as ForeignKeyAttribute;

                if (isPrimaryKey && GetNonNullableType(property.PropertyType) != typeof(int) && GetNonNullableType(property.PropertyType) != typeof(long))
                    throw new InvalidOperationException($"Key property '{property.Name}' must be an int or long to use SQLite AUTOINCREMENT.");

                var columnDefinition = $"{columnName} {columnType}" +
                                       (isPrimaryKey ? " PRIMARY KEY AUTOINCREMENT" : "") +
                                       (isRequired ? " NOT NULL" : "") +
                                       (isUnique ? " UNIQUE" : "");

                if (foreignKeyAttr != null)
                {
                    var referencedTable = QuoteIdentifier(foreignKeyAttr.Name);
                    var onDeleteAction = ValidateForeignKeyAction(foreignKeyAttr.OnDelete);
                    var onUpdateAction = ValidateForeignKeyAction(foreignKeyAttr.OnUpdate);
                    tableConstraints.Add(
                        $"FOREIGN KEY({columnName}) REFERENCES {referencedTable}({QuoteIdentifier("Id")}) ON DELETE {onDeleteAction} ON UPDATE {onUpdateAction}");
                }

                columns.Add(columnDefinition);
            }

            var query = $"CREATE TABLE IF NOT EXISTS {tableName} ({string.Join(", ", columns.Concat(tableConstraints))});";
            ExecuteNonQuery(query);
        }


        /// <summary>
        /// متد برای درج داده‌ها در جدول مربوطه
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که قرار است در جدول درج شود</typeparam>
        /// <param name="obj">شیء داده‌ای که باید در جدول ذخیره شود</param>
        public int Insert<T>(T obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            ValidateType<T>();
            var tableName = QuoteIdentifier(typeof(T).Name);
            // Get properties of the type and exclude those marked with [Key] or [NotMapped]
            var properties = GetMappedProperties(typeof(T))
                .Where(p => !Attribute.IsDefined(p, typeof(KeyAttribute)))
                .ToArray();

            if (properties.Length == 0)
                throw new InvalidOperationException("No insertable properties were found.");

            var columns = string.Join(", ", properties.Select(p => QuoteIdentifier(p.Name)));
            var values = string.Join(", ", properties.Select(p => $"@{p.Name}"));

            // SQL query to insert and retrieve the ID of the inserted row
            var query = $@"INSERT INTO {tableName} ({columns}) VALUES ({values}); SELECT last_insert_rowid();";
            var parameters = properties.ToDictionary(p => $"@{p.Name}", p => p.GetValue(obj) ?? DBNull.Value);
            return ExecuteScalar<int>(query, parameters);
        }

        /// <summary>
        /// این متد یک لیست از اشیا را در ورودی دریاقت کرده و تمامی آنها را در جدول مربوطه درج میکند
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که قرار است در جدول درج شود</typeparam>
        /// <param name="objectList">لیست اشیا داده ای که باید در جدول ذخیره شود</param>
        public void Insert<T>(List<T> objectList)
        {
            if (objectList == null) throw new ArgumentNullException(nameof(objectList));
            if (objectList.Count == 0) return;

            ValidateType<T>();
            var tableName = QuoteIdentifier(typeof(T).Name);
            var properties = GetMappedProperties(typeof(T))
                .Where(p => !Attribute.IsDefined(p, typeof(KeyAttribute))).ToArray();
            if (properties.Length == 0)
                throw new InvalidOperationException("No insertable properties were found.");

            var query = $"INSERT INTO {tableName} ({string.Join(", ", properties.Select(p => QuoteIdentifier(p.Name)))}) " +
                        $"VALUES ({string.Join(", ", properties.Select(p => $"@{p.Name}"))});";

            lock (_writeLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = new SQLiteCommand(query, connection, transaction);
                foreach (var property in properties)
                    command.Parameters.Add(new SQLiteParameter($"@{property.Name}"));

                try
                {
                    foreach (var obj in objectList)
                    {
                        if (obj == null) throw new ArgumentException("The list cannot contain null items.", nameof(objectList));
                        foreach (var property in properties)
                            command.Parameters[$"@{property.Name}"].Value = property.GetValue(obj) ?? DBNull.Value;
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }


        /// <summary>
        /// این متد یک شیء از نوع T را در دیتابیس وارد یا به‌روزرسانی می‌کند.
        /// انتخاب ستون برای بررسی وجود قبلی، به صورت یک عبارت لامبدا ارائه می‌شود.
        /// </summary>
        /// <typeparam name="T">نوع شیء مورد نظر</typeparam>
        /// <param name="keySelector">
        /// یک عبارت لامبدا برای تعیین ستونی که باید بررسی شود.
        /// به عنوان مثال: o => o.Id
        /// </param>
        /// <param name="obj">شیء ورودی برای درج یا به‌روزرسانی</param>
        /// <exception cref="ArgumentNullException">
        /// زمانی که عبارت لامبدا نامعتبر باشد (به‌عنوان مثال، شامل یک فیلد یا مقدار محاسبه‌شده باشد).
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// زمانی که ستونی که مشخص شده است در نوع T وجود نداشته باشد.
        /// </exception>
        /// <example>
        /// <code>
        /// // نمونه‌ای از استفاده برای بررسی ستون Id
        /// Upsert(myObject, o =&gt; o.Id);
        ///
        /// // نمونه‌ای از استفاده برای بررسی ستون Name
        /// Upsert(myObject, o =&gt; o.Name);
        /// </code>
        /// </example>
        public void Upsert<T>(Expression<Func<T, object>> keySelector, T obj) where T : new()
        {
            // Extract the column name from the lambda expression
            var memberExpression = keySelector.Body as MemberExpression ??
                                   (keySelector.Body as UnaryExpression)?.Operand as MemberExpression;

            if (memberExpression == null)
            {
                throw new ArgumentNullException("Invalid column selector expression. Must be a property selector like 'o => o.Id'.");
            }

            var checkColumnName = memberExpression.Member.Name;
            var checkColumnProperty = typeof(T).GetProperties()
                .FirstOrDefault(p => !Attribute.IsDefined(p, typeof(KeyAttribute)) &&
                                     !Attribute.IsDefined(p, typeof(NotMappedAttribute)) &&
                                     p.Name.Equals(checkColumnName, StringComparison.OrdinalIgnoreCase));

            if (checkColumnProperty == null)
            {
                throw new ArgumentNullException($"InsertOrUpdate requires a '{checkColumnName}' property.");
            }

            var checkColumnValue = checkColumnProperty.GetValue(obj);

            if (checkColumnValue == null)
                throw new ArgumentException("The selected key value cannot be null.", nameof(obj));

            lock (_writeLock)
            { 
                if (ExistsByValue<T>(checkColumnName, checkColumnValue))                
                    Update(keySelector, obj);                
                else                
                    Insert(obj);                
            }
        }


        /// <summary>
        /// این متد یک رکورد موجود را در دیتابیس بر اساس کلید اصلی یا ستون مشخص‌شده به‌روزرسانی می‌کند.
        /// </summary>
        /// <typeparam name="T">نوع شیء مورد نظر برای به‌روزرسانی</typeparam>
        /// <param name="keySelector">
        /// عبارت لامبدا برای تعیین ستون کلید. به طور پیش‌فرض، ستون "Id" استفاده می‌شود.
        /// به عنوان مثال: o => o.Id
        /// </param>
        /// <param name="obj">
        /// شیء شامل داده‌هایی که باید به‌روزرسانی شوند. تمامی مقادیر ویژگی‌های شیء به جز مقادیر مشخص‌شده در 
        /// [NotMapped]
        /// در دیتابیس به‌روزرسانی خواهند شد.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// زمانی که عبارت لامبدا برای انتخاب کلید نامعتبر باشد.
        /// </exception>
        /// <example>
        /// <code>
        /// // به‌روزرسانی رکوردی بر اساس ستون Name
        /// Update( o =&gt; o.Name, myObject);
        /// </code>
        /// </example>
        public void Update<T>(Expression<Func<T, object>> keySelector, T obj)
        {
            ValidateType<T>();
            var tableName = QuoteIdentifier(typeof(T).Name);

            keySelector ??= CreateKeySelector<T>("Id");

            var keyName = ExtractKeyName(keySelector);

            // Get properties of the type
            var updateProperties = GetMappedProperties(typeof(T));

            // Identify the primary key property
            var propertyInfos = updateProperties as PropertyInfo[] ?? updateProperties.ToArray();
            var idProperty =
                propertyInfos.FirstOrDefault(p => p.Name.Equals(keyName, StringComparison.OrdinalIgnoreCase));
            if (idProperty == null) throw new ArgumentNullException("Update requires an 'Id' property.");

            // Prepare update query excluding [NotMapped] and Id and checkColumnName
            var updates = string.Join(", ", propertyInfos
                .Where(p => !p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) &&
                            !(keyName != null && !keyName.Equals("Id", StringComparison.OrdinalIgnoreCase) &&
                              p.Name.Equals(keyName,
                                  StringComparison.OrdinalIgnoreCase))) // Exclude checkColumnName if not "Id"
                .Select(p => $"{QuoteIdentifier(p.Name)} = @{p.Name}"));

            if (string.IsNullOrEmpty(updates))
                throw new InvalidOperationException("No updatable properties were found.");

            var query = $"UPDATE {tableName} SET {updates} WHERE {QuoteIdentifier(keyName)} = @{keyName};";

            var parameters = propertyInfos.ToDictionary(p => $"@{p.Name}", p => p.GetValue(obj) ?? DBNull.Value);
            ExecuteNonQuery(query, parameters);
        }

        /// <summary>
        /// این متد یک رکورد موجود را در دیتابیس بر اساس کلید اصلی به‌روزرسانی می‌کند.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="obj"></param>
        public void Update<T>(T obj) => Update(CreateKeySelector<T>("Id"), obj);


        /// <summary>
        /// متد برای حذف داده‌ها
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که باید حذف شود</typeparam>
        /// <param name="id">شناسه رکورد برای حذف</param>
        public void Delete<T>(int id) => Delete(CreateKeySelector<T>("Id"), id);

        /// <summary>
        /// این متد یک رکورد را بر اساس یک مقدار کلید از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <typeparam name="T">نوع موجودیت که باید یک کلاس جدید باشد.</typeparam>
        /// <param name="keyValue">مقدار کلید برای جستجو.</param>
        /// <param name="keySelector">عبارت لامبدا برای مشخص کردن ستون کلید. اگر مقدار نداشته باشد، به طور پیش‌فرض "Id" استفاده می‌شود.</param>
        /// <exception cref="ArgumentNullException">اگر <paramref name="keySelector"/> مقدار <c>null</c> باشد.</exception>
        /// <exception cref="ArgumentException">اگر فرمت عبارت لامبدا نادرست باشد.</exception>
        public void Delete<T>(Expression<Func<T, object>> keySelector, string keyValue)
            => Delete<T>(keySelector, (object?)keyValue);

        /// <summary>Deletes records matching the selected property and value.</summary>
        public void Delete<T>(Expression<Func<T, object>> keySelector, object? keyValue)
        {
            ValidateType<T>();
            var keyName = ExtractKeyName(keySelector);

            // Build the SQL DELETE query with the extracted property name
            var tableName = QuoteIdentifier(typeof(T).Name);
            var query = $"DELETE FROM {tableName} WHERE {QuoteIdentifier(keyName)} = @{keyName};";

            // Execute the query with the appropriate parameter
            ExecuteNonQuery(query, new Dictionary<string, object> { { $"@{keyName}", keyValue ?? DBNull.Value } });
        }

        /// <summary>
        /// این متد برای دریافت یک لیست از رکوردها از پایگاه داده استفاده می‌شود.
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که از جدول بازیابی می‌شود.</typeparam>
        /// <param name="conditions">
        /// شرایطی که برای فیلتر کردن داده‌ها استفاده می‌شوند. این شرایط باید به صورت یک دیکشنری از 
        /// <see cref="Expression{TDelegate}"/> 
        /// و مقدار مرتبط ارائه شوند.
        /// <example>
        /// <code>
        /// var conditions = new Dictionary<Expression<Func<MyEntity, object>>, object>
        /// {
        ///     { o =&gt; o.Id, 1 },
        ///     { o =&gt; o.Name, "John" }
        /// };
        /// </code>
        /// </example>
        /// </param>
        /// <param name="conditionType">نحوه ترکیب شرایط با <see cref="LogicalOperator"/>.</param>
        /// <param name="limit">تعداد رکوردهای محدودشده برای بازیابی.</param>
        /// <param name="offset">تعداد رکوردهایی که باید رد شوند.</param>
        /// <param name="orderBy">
        /// دیکشنری‌ای که ترتیب رکوردها را مشخص می‌کند. کلید آن یک 
        /// <see cref="Expression{TDelegate}"/> 
        /// است که ستون را تعریف می‌کند و مقدار آن 
        /// <see cref="SortOrder"/> 
        /// (ASC یا DESC) است.
        /// <example>
        /// <code>
        /// var orderBy = new Dictionary<Expression<Func<MyEntity, object>>, SortOrder>
        /// {
        ///     { o =&gt; o.Name, SortOrder.ASC },
        ///     { o =&gt; o.CreatedDate, SortOrder.DESC }
        /// };
        /// </code>
        /// </example>
        /// </param>
        /// <returns>لیستی از اشیاء نوع T که با شرایط و ترتیب مشخص بازیابی شده‌اند.</returns>
        /// <example>
        /// <code>
        /// var conditions = new new Dictionary<Expression<Func<MyEntity, object>>, object>
        /// {
        ///    { o =&gt; o.Id, 123 },
        ///    { o =&gt; o.Name, "John" }
        /// };
        /// 
        /// var orderBy = new Dictionary<Expression<Func<MyEntity, object>>, SortOrder>
        /// {
        ///    { o =&gt; o.Name, SortOrder.ASC     },
        ///    { o =&gt; o.CreatedDate, SortOrder.DESC    }
        /// };
        /// 
        /// var results = GetAll<MyEntity>(
        ///     conditions: conditions,
        ///     conditionType: LogicalOperator.And,
        ///     limit: 10,
        ///     offset: 0,
        ///     orderBy: orderBy
        /// );
        /// </code>
        /// </example>
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
            var tableName = QuoteIdentifier(typeof(T).Name);
            Dictionary<string, object>? queryParameters = null;

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT * FROM {tableName}");

            #region Build conditions if provided

            if (conditions != null && conditions.Any())
            {
                var (conditionList, parameters) = BuildConditions(conditions);
                var whereClause = string.Join($" {logicalOperator} ", conditionList);

                queryBuilder.Append(" WHERE ");
                queryBuilder.Append(whereClause);

                queryParameters = parameters;
            }

            #endregion

            #region Add ORDER BY if specified

            if (orderBy != null && orderBy.Any())
            {
                var orderByClause = string.Join(", ", orderBy.Select(o =>
                {
                    string columnName;
                    if (o.Key.Body is MemberExpression memberExpression)
                    {
                        columnName = QuoteIdentifier(memberExpression.Member.Name);
                    }
                    else if (o.Key.Body is UnaryExpression unaryExpression &&
                             unaryExpression.Operand is MemberExpression unaryMemberExpression)
                    {
                        columnName = QuoteIdentifier(unaryMemberExpression.Member.Name);
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

            #endregion

            #region Add LIMIT and OFFSET if specified

            if (limit > 0)
            {
                queryBuilder.Append(" LIMIT @__limit OFFSET @__offset");
                queryParameters ??= new Dictionary<string, object>();
                queryParameters["@__limit"] = limit;
                queryParameters["@__offset"] = offset;
            }

            #endregion

            return Query<T>(queryBuilder.ToString(), queryParameters);
        }


        /// <summary>
        /// این متد برای بازیابی داده‌ها از جدول اصلی به همراه فیلدی از یک جدول مرتبط با قابلیت اعمال شرایط جستجو استفاده می‌شود.
        /// از طریق یک عملیات INNER JOIN بین دو جدول، داده‌ها استخراج شده و شرایط جستجو بر روی آن‌ها اعمال می‌شود.
        /// </summary>
        /// <typeparam name="T">نوع مدل مربوط به جدول اصلی</typeparam>
        /// <typeparam name="TRelated">نوع مدل مربوط به جدول مرتبط</typeparam>
        /// <param name="relationFieldName">نام فیلد در جدول اصلی که به جدول مرتبط اشاره دارد (مثلاً کلید خارجی)</param>
        /// <param name="relatedFieldName">نام فیلد در جدول مرتبط که باید در نتیجه درج شود</param>
        /// <param name="aliasName">نام مستعار برای فیلد مرتبط در خروجی (پیش‌فرض: "RelatedField")</param>
        /// <param name="conditions">
        /// شرایطی که برای فیلتر کردن داده‌ها استفاده می‌شوند. این شرایط باید به صورت یک دیکشنری از 
        /// <see cref="Expression{TDelegate}"/> 
        /// و مقدار مرتبط ارائه شوند.
        /// <example>
        /// <code>
        /// var conditions = new Dictionary<Expression<Func<MyEntity, object>>, object>
        /// {
        ///     { o =&gt; o.Id, 1 },
        ///     { o =&gt; o.Name, "John" }
        /// };
        /// </code>
        /// </example>
        /// </param>
        /// <param name="conditionType">نحوه ترکیب شرایط با <see cref="LogicalOperator"/>.</param>
        /// <returns>لیستی از داده‌ها شامل اطلاعات جدول اصلی و فیلد مرتبط از جدول مرتبط که مطابق با شرایط مشخص شده است</returns>
        /// <example>
        /// <code>
        /// // Define the conditions for filtering the Orders table.
        /// var conditions = new Dictionary<Expression<Func<Order, object>>, object>
        /// {
        ///    { o =&gt; o.Id, 1 }, // Fetch only completed orders
        ///    { o =&gt; o.Name, "John" }          // Fetch orders where the amount is exactly 100
        /// };
        /// // Call GetAllWithRelation to retrieve orders with related customer names.
        /// var results = GetAllWithRelation<Order, Customer>(
        ///    relationFieldName: "CustomerId",    // Foreign key in Orders table
        ///    relatedFieldName: "CustomerName",  // Field in Customers table to include in the result
        ///    aliasName: "CustomerNameAlias",    // Alias for the related field in the result
        ///    conditions: conditions,            // Conditions to filter the Orders table
        ///    conditionType: LogicalOperator.And  // Combine conditions using AND
        /// );
        /// </code>
        /// </example>
        public List<T> GetAllWithRelation<T, TRelated>(
            string relationFieldName,
            string relatedFieldName,
            string aliasName = "RelatedField",
            Dictionary<Expression<Func<T, object>>, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var mainTableName = QuoteIdentifier(typeof(T).Name);
            var relatedTableName = QuoteIdentifier(typeof(TRelated).Name);
            relationFieldName = QuoteIdentifier(relationFieldName);
            relatedFieldName = QuoteIdentifier(relatedFieldName);
            aliasName = QuoteIdentifier(aliasName);

            // Base query with INNER JOIN
            var query = $@"SELECT t.*, r.{relatedFieldName} AS {aliasName}
                    FROM {mainTableName} t
                    INNER JOIN {relatedTableName} r
                    ON t.{relationFieldName} = r.{QuoteIdentifier("Id")}";

            #region Add condition if provided

            if (conditions != null && conditions.Any())
            {
                var parameters = new Dictionary<string, object>();
                var whereClause = string.Join(
                    $" {logicalOperator} ",
                    conditions.Select((condition, index) =>
                    {
                        var propertyName = ExtractKeyName(condition.Key);
                        if (condition.Value == null || condition.Value == DBNull.Value)
                            return $"t.{QuoteIdentifier(propertyName)} IS NULL";

                        var parameterName = $"@p{index}";
                        parameters[parameterName] = condition.Value;
                        return $"t.{QuoteIdentifier(propertyName)} = {parameterName}";
                    }));
                query += $" WHERE {whereClause}";
                return Query<T>(query, parameters);
            }

            #endregion

            return Query<T>(query);
        }

        /// <summary>
        /// این تابع برای دریافت تمامی رکوردهای یک جدول اصلی به همراه روابط آن با جداول دیگر استفاده می‌شود.
        /// این تابع به شما امکان می‌دهد تا با استفاده از JOIN‌ها، داده‌های مرتبط از جداول دیگر را نیز دریافت کنید.
        /// همچنین می‌توانید ستون‌های اضافی و شرایط (WHERE) را نیز به کوئری اضافه کنید.
        /// </summary>
        /// <typeparam name="T">نوع مدل جدول اصلی که باید یک کلاس با constructor بدون پارامتر باشد.</typeparam>
        /// <param name="mainTableAlias">نام مستعار (Alias) برای جدول اصلی.</param>
        /// <param name="relationships">دیکشنری شامل روابط بین جدول اصلی و جداول دیگر. هر رابطه شامل نام مستعار جدول مرتبط، نام فیلد رابطه و نام جدول مرتبط است.</param>
        /// <param name="additionalColumns">لیست ستون‌های اضافی که باید به کوئری اضافه شوند. هر آیتم شامل نام مستعار جدول، نام ستون و نام مستعار ستون است.</param>
        /// <param name="conditions">دیکشنری شامل شرایط (WHERE) برای فیلتر کردن داده‌ها. کلیدها نام فیلدها و مقادیر، مقادیر فیلتر هستند.</param>
        /// <param name="conditionType">نحوه ترکیب شرایط با <see cref="LogicalOperator"/>.</param>
        /// <returns>لیستی از اشیاء نوع T که شامل داده‌های جدول اصلی و جداول مرتبط است.</returns>
        /// <example>
        /// مثال زیر نحوه استفاده از این تابع را نشان می‌دهد:
        /// <code>
        /// var relationships = new Dictionary<string, (string, string, string)>
        /// {
        ///     { "u", ("UserId", "Users", "p") } // رابطه بین جدول Posts و Users
        /// };
        ///
        /// var additionalColumns = new List<(string, string, string)>
        /// {
        ///     ("u", "Username", "AuthorName") // اضافه کردن ستون Username از جدول Users با نام مستعار AuthorName
        /// };
        ///
        /// var conditions = new Dictionary<string, object>
        /// {
        ///     { "IsPublished", true } // شرط WHERE برای فیلتر کردن پست‌های منتشر شده
        /// };
        ///
        /// var posts = GetAllWithRelations<Post>("p", relationships, additionalColumns, conditions);
        /// </code>
        /// کوئری تولید شده به صورت زیر خواهد بود:
        /// <code>
        /// SELECT p.*, u.Username AS AuthorName
        /// FROM Posts p
        /// INNER JOIN Users u ON p.UserId = u.Id
        /// WHERE p.IsPublished = @IsPublished
        /// </code>
        /// </example>
        public List<T> GetAllWithRelations<T>(
            string mainTableAlias,
            Dictionary<string, (string relationFieldName, string relatedTableName, string tableRelationExistAlias)> relationships,
            List<(string tableAlias, string columnName, string aliasName)>? additionalColumns = null,
            Dictionary<string, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var mainTableName = QuoteIdentifier(typeof(T).Name);
            mainTableAlias = QuoteIdentifier(mainTableAlias);

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT {mainTableAlias}.*");

            #region Add additional columns if specified

            if (additionalColumns != null && additionalColumns.Any())
            {
                foreach (var column in additionalColumns)
                {
                    queryBuilder.Append($", {QuoteIdentifier(column.tableAlias)}.{QuoteIdentifier(column.columnName)} AS {QuoteIdentifier(column.aliasName)}");
                }
            }

            #endregion

            #region FROM clause with the main table alias

            queryBuilder.Append($" FROM {mainTableName} {mainTableAlias}");

            #endregion

            #region Add JOINs for each relationship

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

            #endregion

            #region Add conditions if provided

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

            #endregion

            return Query<T>(queryBuilder.ToString(), parameters);
        }



        /// <summary>
        /// این تابع تمام رکوردهای موجود در جدول اصلی را همراه با روابط و ستون‌های اضافی برمی‌گرداند.
        /// </summary>
        /// <typeparam name="T">نوع مدل اصلی که باید داده‌ها از آن استخراج شوند.</typeparam>
        /// <param name="mainTableAlias">نام مستعار جدول اصلی در کوئری.</param>
        /// <param name="relationships">لیستی از روابط بین جدول اصلی و جداول مرتبط. هر رابطه شامل یک عبارت لامبدا برای فیلد رابطه، نام جدول مرتبط و نام مستعار جدول مرتبط است.</param>
        /// <param name="additionalColumns">لیستی از ستون‌های اضافی که باید به نتیجه اضافه شوند. هر آیتم شامل نام مستعار جدول، عبارت لامبدا برای ستون و نام مستعار ستون است.</param>
        /// <param name="conditions">شرایط فیلتر کردن داده‌ها به صورت یک دیکشنری از عبارات لامبدا و مقادیر مربوطه.</param>
        /// <param name="conditionType">نحوه ترکیب شرایط با <see cref="LogicalOperator"/>.</param>
        /// <returns>لیستی از رکوردهای استخراج شده از نوع T.</returns>
        /// <example>
        /// <code>
        /// var nodes = SqLiteOrm.Instance.GetAllWithRelations<Node>(
        ///     "t",
        ///     new List<(Expression<Func<Node, object>>, string, string)>
        ///     {
        ///         (n =&gt; n.BuildId, "Build", "b"), // رابطه با جدول Build از طریق فیلد BuildId
        ///         (n =&gt; n.GroupId, "NodeGroup", "g") // رابطه با جدول NodeGroup از طریق فیلد GroupId
        ///     },
        ///     new List<(string, Expression<Func<Node, object>>, string)>
        ///     {
        ///         ("b", b =&gt; b.BuildName, "BuildName"), // اضافه کردن ستون BuildName از جدول Build
        ///         ("g", g =&gt; g.GroupName, "GroupName") // اضافه کردن ستون GroupName از جدول NodeGroup
        ///     },
        ///     new Dictionary<Expression<Func<Node, object>>, object>
        ///     {
        ///         { n =&gt; n.IsDeleted, 0 }, // شرط فیلتر: فقط رکوردهایی که IsDeleted برابر 0 دارند
        ///     }
        /// );
        /// </code>
        /// کوئری اجرا شده توسط کد بالا به صورت زیر است:
        /// <code>
        /// SELECT t.*, b.BuildName AS BuildName, g.GroupName AS GroupName
        /// FROM Node t
        /// INNER JOIN Build b ON t.BuildId = b.Id
        /// INNER JOIN NodeGroup g ON t.GroupId = g.Id
        /// WHERE t.IsDeleted = 0;
        /// </code>
        /// </example>
        public List<T> GetAllWithRelations<T>(
            string mainTableAlias,
            List<(Expression<Func<T, object>> relationExpression, string relatedTableName, string tableAlias)> relationships,
            List<(string tableAlias, Expression<Func<T, object>> columnExpression, string aliasName)>? additionalColumns = null,
            Dictionary<Expression<Func<T, object>>, object>? conditions = null,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var mainTableName = QuoteIdentifier(typeof(T).Name);
            mainTableAlias = QuoteIdentifier(mainTableAlias);

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT {mainTableAlias}.*");

            #region Add additional columns if specified

            if (additionalColumns != null && additionalColumns.Any())
            {
                foreach (var column in additionalColumns)
                {
                    string columnName;
                    if (column.columnExpression.Body is MemberExpression memberExpression)
                    {
                        columnName = memberExpression.Member.Name;
                    }
                    else if (column.columnExpression.Body is UnaryExpression unaryExpression &&
                             unaryExpression.Operand is MemberExpression unaryMemberExpression)
                    {
                        columnName = unaryMemberExpression.Member.Name;
                    }
                    else
                    {
                        throw new ArgumentException("Invalid lambda expression", nameof(column.columnExpression));
                    }

                    queryBuilder.Append($", {QuoteIdentifier(column.tableAlias)}.{QuoteIdentifier(columnName)} AS {QuoteIdentifier(column.aliasName)}");
                }
            }

            #endregion

            #region FROM clause with the main table alias

            queryBuilder.Append($" FROM {mainTableName} {mainTableAlias}");

            #endregion

            #region Add JOINs for each relationship

            if (relationships != null && relationships.Any())
            {
                foreach (var relationship in relationships)
                {
                    string relationFieldName;
                    if (relationship.relationExpression.Body is MemberExpression memberExpression)
                    {
                        relationFieldName = memberExpression.Member.Name;
                    }
                    else if (relationship.relationExpression.Body is UnaryExpression unaryExpression &&
                             unaryExpression.Operand is MemberExpression unaryMemberExpression)
                    {
                        relationFieldName = unaryMemberExpression.Member.Name;
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

            #endregion

            #region Add conditions if provided

            Dictionary<string, object>? parameters = null;
            if (conditions != null && conditions.Any())
            {
                parameters = new Dictionary<string, object>();
                var whereClause = string.Join(
                    $" {logicalOperator} ",
                    conditions.Select((condition, index) =>
                    {
                        var propertyName = ExtractKeyName(condition.Key);
                        if (condition.Value == null || condition.Value == DBNull.Value)
                            return $"{mainTableAlias}.{QuoteIdentifier(propertyName)} IS NULL";

                        var parameterName = $"@p{index}";
                        parameters[parameterName] = condition.Value;
                        return $"{mainTableAlias}.{QuoteIdentifier(propertyName)} = {parameterName}";
                    }));

                queryBuilder.Append($" WHERE {whereClause}");
            }

            #endregion

            return Query<T>(queryBuilder.ToString(), parameters);
        }

        /// <summary>
        /// متد برای یافتن رکورد بر اساس شناسه
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که باید دریافت شود</typeparam>
        /// <param name="id">شناسه رکورد مورد نظر</param>
        /// <returns>رکورد با شناسه مشخص</returns>
        public T? FindById<T>(int id) where T : new() => FindOneByKey<T>(CreateKeySelector<T>("Id"), id);


        /// <summary>
        /// این متد یک رکورد را بر اساس یک مقدار کلید از پایگاه داده جستجو می‌کند.
        /// </summary>
        /// <typeparam name="T">نوع موجودیت که باید یک کلاس جدید باشد.</typeparam> 
        /// <param name="keySelector">عبارت لامبدا برای مشخص کردن ستون کلید. اگر مقدار نداشته باشد، به طور پیش‌فرض "Id" استفاده می‌شود.</param>
        /// <param name="keyValue">مقدار کلید برای جستجو.</param>
        /// <returns>اولین رکوردی که با شرط تطابق دارد یا مقدار پیش‌فرض اگر رکوردی یافت نشد.</returns>
        /// <example>
        /// <code>
        /// var result = FindOneByKey<MyEntity>("123", e =&gt; e.Id);
        /// </code>
        /// </example>
        public T? FindOneByKey<T>(Expression<Func<T, object>> keySelector, string keyValue) where T : new() =>
            FindOneByKey<T>(keySelector, (object?)keyValue);

        /// <summary>Finds the first record matching the selected property and value.</summary>
        public T? FindOneByKey<T>(Expression<Func<T, object>> keySelector, object? keyValue) where T : new() =>
            FindOneByKey<T>(new Dictionary<Expression<Func<T, object>>, object> { { keySelector, keyValue! } });


        /// <summary>
        /// این متد یک رکورد را با استفاده از چندین شرط از پایگاه داده جستجو می‌کند.
        /// </summary>
        /// <typeparam name="T">نوع موجودیت که باید یک کلاس جدید باشد.</typeparam>
        /// <param name="conditions">یک دیکشنری شامل شروط به صورت زوج کلید-مقدار. کلیدها باید عبارات لامبدا باشند که ستون‌ها را مشخص می‌کنند.</param>
        /// <param name="conditionType">نحوه ترکیب شرایط با <see cref="LogicalOperator"/>.</param>
        /// <returns>اولین رکوردی که با شروط تطابق دارد یا مقدار پیش‌فرض اگر رکوردی یافت نشد.</returns>
        /// <example>
        /// <code>
        /// var conditions = new Dictionary<Expression<Func<MyEntity, object>>, object>
        /// {
        ///     { e =&gt; e.Name, "John" },
        ///     { e =&gt; e.Age, 30 }
        /// };
        /// var result = FindOneByKey<MyEntity>(conditions, "AND");
        /// </code>
        /// </example>
        public T? FindOneByKey<T>(Dictionary<Expression<Func<T, object>>, object> conditions,
            LogicalOperator conditionType = LogicalOperator.And) where T : new()
        {
            ValidateType<T>();

            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var tableName = QuoteIdentifier(typeof(T).Name);
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

        /// <summary>
        /// متد برای شمارش تعداد رکوردهای یک جدول
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که در جدول موجود است</typeparam>
        /// <returns>تعداد رکوردهای موجود در جدول</returns>
        public int Count<T>(Dictionary<Expression<Func<T, object>>, object> conditions, LogicalOperator conditionType = LogicalOperator.And)
            where T : new()
        {
            ValidateType<T>();
            var logicalOperator = GetSqlLogicalOperator(conditionType);
            var tableName = QuoteIdentifier(typeof(T).Name);

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

        /// <summary>
        /// متد برای شمارش تعداد رکوردهای یک جدول
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که در جدول موجود است</typeparam>
        /// <returns>تعداد رکوردهای موجود در جدول</returns>
        public int Count<T>() where T : new()
        {
            ValidateType<T>();
            var tableName = QuoteIdentifier(typeof(T).Name);
            return ExecuteScalar<int>($"SELECT COUNT(*) FROM {tableName};");
        }

        /// <summary>
        /// متد برای بررسی وجود رکورد با شناسه خاص
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که باید بررسی شود</typeparam>
        /// <param name="id">شناسه رکورد مورد نظر</param>
        /// <returns>آیا رکورد با شناسه مشخص وجود دارد؟</returns>
        public bool Exists<T>(int id) where T : new() => Exists<T>(CreateKeySelector<T>("Id"), id);


        /// <summary>
        /// بررسی می‌کند که آیا رکوردی با مقدار مشخص شده برای ویژگی انتخاب شده در پایگاه داده وجود دارد یا خیر.
        /// </summary>
        /// <typeparam name="T">نوع شیء که ویژگی مورد نظر در آن قرار دارد.</typeparam> 
        /// <param name="keySelector">عبارت لامبدا که ویژگی مورد نظر برای جستجو را مشخص می‌کند (مثلاً x => x.Name).</param>
        /// <param name="keyValue">مقدار کلیدی که به دنبال آن هستیم (مثلاً شناسه رکورد).</param>
        /// <returns>در صورتی که رکوردی با مقدار مشخص شده وجود داشته باشد، <c>true</c> باز می‌گرداند. در غیر این صورت، <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">اگر <paramref name="keySelector"/> مقدار <c>null</c> باشد.</exception>
        /// <exception cref="ArgumentException">اگر فرمت عبارت لامبدا نادرست باشد.</exception>
        /// <example>
        /// فرض کنید می‌خواهید بررسی کنید که آیا رکوردی با شناسه مشخص وجود دارد.
        /// برای این کار می‌توانید از کد زیر استفاده کنید:
        /// <code>
        /// bool exists = SqLiteOrm.Instance.Exists<MyEntity>("123", x =&gt; x.Id);
        /// </code>
        /// این کد بررسی می‌کند که آیا رکوردی با شناسه "123" در جدول <c>MyEntity</c> وجود دارد یا خیر.
        /// </example>
        public bool Exists<T>(Expression<Func<T, object>> keySelector, string keyValue) where T : new()
            => Exists<T>(keySelector, (object?)keyValue);

        /// <summary>Determines whether a record matching the selected property and value exists.</summary>
        public bool Exists<T>(Expression<Func<T, object>> keySelector, object? keyValue) where T : new()
        {
            ValidateType<T>();

            var keyName = ExtractKeyName(keySelector);
            return ExistsByValue<T>(keyName, keyValue);
        }

        /// <summary>
        /// متد برای نگاشت داده‌های خوانده شده از SQLiteDataReader به اشیاء
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که باید نگاشت شود</typeparam>
        /// <param name="reader">SQLiteDataReader برای خواندن داده‌ها</param>
        /// <returns>لیستی از اشیاء از نوع T</returns>
        private static List<T> MapReaderToObjects<T>(SQLiteDataReader reader) where T : new()
        {
            var results = new List<T>();
            var properties = GetMappedProperties(typeof(T)).Where(p => p.CanWrite).ToArray();
            var ordinals = Enumerable.Range(0, reader.FieldCount)
                .ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                var obj = new T();
                foreach (var property in properties)
                {
                    if (!ordinals.TryGetValue(property.Name, out var ordinal) || reader.IsDBNull(ordinal))
                        continue;

                    try
                    {
                        var dbValue = reader.GetValue(ordinal);
                        property.SetValue(obj, ConvertDatabaseValue(dbValue, property.PropertyType));
                    }
                    catch (Exception ex)
                    {
                        // Handle specific property mapping errors or log them
                        throw new InvalidOperationException(
                            $"Error mapping property '{property.Name}': {ex.Message}", ex);
                    }
                }

                results.Add(obj);
            }

            return results;
        }

        /// <summary>
        /// متد برای اجرای کوئری‌های سفارشی و بازگشت نتایج به صورت لیست
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که باید دریافت شود</typeparam>
        /// <param name="query">کوئری SQL که باید اجرا شود</param>
        /// <param name="parameters">پارامترهای کوئری</param>
        /// <returns>لیستی از اشیاء از نوع T</returns>
        public List<T> Query<T>(string query, Dictionary<string, object>? parameters = null) where T : new()
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.", nameof(query));
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(query, connection);

            AddParameters(command, parameters);

            using var reader = command.ExecuteReader();
            return MapReaderToObjects<T>(reader);
        }


        /// <summary>
        /// این تابع برای اجرای یک کوئری SQL که یک مقدار اسکالر (تک مقداری) برمی‌گرداند، استفاده می‌شود.
        /// این تابع برای کوئری‌هایی مانند COUNT، SUM، MAX، MIN و سایر توابع تجمعی مناسب است.
        /// همچنین از پارامترهای دیکشنری برای جلوگیری از حملات تزریق SQL (SQL Injection) استفاده می‌کند.
        /// </summary>
        /// <typeparam name="T">نوع داده‌ای که انتظار دارید از کوئری برگردانده شود.</typeparam>
        /// <param name="query">کوئری SQL که باید اجرا شود.</param>
        /// <param name="parameters">دیکشنری شامل پارامترهای کوئری. کلیدها نام پارامترها و مقادیر، مقادیر پارامترها هستند.</param>
        /// <returns>مقدار اسکالر بازگشتی از کوئری. اگر نتیجه null یا DBNull باشد، مقدار پیش‌فرض نوع T برگردانده می‌شود.</returns>
        /// <example>
        /// مثال زیر نحوه استفاده از این تابع را نشان می‌دهد:
        /// <code>
        /// var query = "SELECT COUNT(*) FROM Users WHERE IsActive = @IsActive";
        /// var parameters = new Dictionary<string, object>
        /// {
        ///     { "@IsActive", true }
        /// };
        ///
        /// int activeUserCount = ExecuteScalar<int>(query, parameters);
        /// </code>
        /// در این مثال، تعداد کاربران فعال در سیستم برگردانده می‌شود.
        /// </example>
        public T? ExecuteScalar<T>(string query, Dictionary<string, object>? parameters = null)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.", nameof(query));
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(query, connection);

            AddParameters(command, parameters);

            var result = command.ExecuteScalar();
            return (result == DBNull.Value || result == null) 
                ? default 
                : (T)ConvertDatabaseValue(result, typeof(T));
        }

        /// <summary>
        /// متد برای اجرای کوئری‌های SQL بدون بازگشت داده
        /// </summary>
        /// <param name="query">کوئری SQL که باید اجرا شود</param>
        /// <param name="parameters">پارامترهای کوئری</param>
        public void ExecuteNonQuery(string query, Dictionary<string, object>? parameters = null)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.", nameof(query));
            lock (_writeLock)
            {
                using var connection = OpenConnection();
                using var command = new SQLiteCommand(query, connection);
                AddParameters(command, parameters);

                try
                {
                    command.ExecuteNonQuery();
                }
                catch (SQLiteException ex)
                {
                    throw new SQLiteException("An error occurred while executing a database operation.", ex);
                }
            }
        }


        /// <summary>
        /// بررسی می‌کند که نوع عمومی دارای حداقل یک ویژگی باشد.
        /// </summary>
        /// <typeparam name="T">
        /// نوع عمومی که نیاز است حداقل یک ویژگی داشته باشد.
        /// </typeparam>
        /// <exception cref="InvalidOperationException">
        /// در صورتی که نوع عمومی فاقد ویژگی باشد، یک خطا ایجاد می‌شود.
        /// </exception>
        private static void ValidateType<T>() => _ = GetMappedProperties(typeof(T)).Any()
            ? true
            : throw new InvalidOperationException("Type T must have at least one property.");


        /// <summary>
        /// یک شناسگر SQL را ایمن‌سازی و نقل‌قول می‌کند تا از تزریق SQL و خطاهای نحو جلوگیری شود.
        /// </summary>
        /// <param name="identifier">
        /// نام شناسگر (مانند نام جدول یا ستون) که باید نقل‌قول شود.
        /// </param>
        /// <returns>
        /// شناسگر نقل‌قول‌شده و ایمن‌شده.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// اگر شناسگر خالی یا null باشد، یک خطا ایجاد می‌شود.
        /// </exception>
        private static string QuoteIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentNullException(@"Identifier cannot be null or empty", nameof(identifier));
            }

            // Escape double quotes by doubling them
            var escapedIdentifier = identifier.Replace("\"", "\"\"");

            // Wrap the escaped identifier in double quotes
            return $"\"{escapedIdentifier}\"";
        }

        /// <summary>
        /// یک اتصال جدید SQLite را باز کرده و بازمی‌گرداند.
        /// </summary>
        /// <returns>اتصال بازشده به پایگاه داده.</returns>
        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// پارامترهای ارائه‌شده را به فرمان SQLite اضافه می‌کند.
        /// </summary>
        /// <param name="command">فرمان SQLite که باید پارامترها به آن افزوده شوند.</param>
        /// <param name="parameters">دیکشنری نام و مقدار پارامترها؛ می‌تواند <c>null</c> باشد.</param>
        private static void AddParameters(SQLiteCommand command, Dictionary<string, object>? parameters)
        {
            if (parameters == null) return;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        /// <summary>
        /// ویژگی‌های عمومی و نگاشت‌شونده یک نوع را از کش دریافت یا در آن ذخیره می‌کند.
        /// </summary>
        /// <param name="type">نوع مدلی که ویژگی‌های آن باید دریافت شوند.</param>
        /// <returns>آرایه ویژگی‌هایی که با <see cref="NotMappedAttribute"/> علامت‌گذاری نشده‌اند.</returns>
        private static PropertyInfo[] GetMappedProperties(Type type) =>
            MappedPropertiesCache.GetOrAdd(type, t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !Attribute.IsDefined(p, typeof(NotMappedAttribute)))
                .ToArray());

        private bool ExistsByValue<T>(string keyName, object? keyValue) where T : new()
        {
            var tableName = QuoteIdentifier(typeof(T).Name);
            var query = keyValue is null or DBNull
                ? $"SELECT 1 FROM {tableName} WHERE {QuoteIdentifier(keyName)} IS NULL LIMIT 1;"
                : $"SELECT 1 FROM {tableName} WHERE {QuoteIdentifier(keyName)} = @{keyName} LIMIT 1;";
            var parameters = keyValue is null or DBNull
                ? null
                : new Dictionary<string, object> { [$"@{keyName}"] = keyValue };
            return ExecuteScalar<int>(query, parameters) > 0;
        }

        private static Type GetNonNullableType(Type type) => Nullable.GetUnderlyingType(type) ?? type;

        /// <summary>
        /// مقدار خوانده‌شده از پایگاه داده را به نوع مقصد تبدیل می‌کند.
        /// </summary>
        /// <param name="value">مقدار خوانده‌شده از پایگاه داده.</param>
        /// <param name="destinationType">نوع ویژگی مقصد.</param>
        /// <returns>مقدار تبدیل‌شده و سازگار با نوع مقصد.</returns>
        /// <exception cref="InvalidCastException">اگر مقدار قابل تبدیل به <see cref="Guid"/> نباشد، پرتاب می‌شود.</exception>
        private static object ConvertDatabaseValue(object value, Type destinationType)
        {
            var targetType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
            if (targetType.IsEnum)
                return value is string text
                    ? Enum.Parse(targetType, text, true)
                    : Enum.ToObject(targetType, value);

            if (targetType == typeof(Guid))
            {
                if (value is Guid guid)
                    return guid;

                if (value is byte[] guidBytes && guidBytes.Length == 16)
                    return new Guid(guidBytes);

                var guidText = Convert.ToString(value);

                if (Guid.TryParse(guidText, out var parsedGuid))
                    return parsedGuid;

                throw new InvalidCastException(
                    $"Cannot convert value '{value}' to Guid.");
            }

            if (targetType == typeof(bool) && value is long longValue)
                return longValue != 0;
            return Convert.ChangeType(value, targetType);
        }

        /// <summary>
        /// معتبر بودن عملگر منطقی ترکیب شرایط را بررسی و شکل استاندارد آن را بازمی‌گرداند.
        /// </summary>
        /// <param name="conditionType">عملگر منطقی نوع‌امن.</param>
        /// <returns>عملگر SQL متناظر با مقدار enum.</returns>
        /// <exception cref="ArgumentOutOfRangeException">اگر مقدار enum تعریف نشده باشد، پرتاب می‌شود.</exception>
        private static string GetSqlLogicalOperator(LogicalOperator conditionType) => conditionType switch
        {
            LogicalOperator.And => "AND",
            LogicalOperator.Or => "OR",
            _ => throw new ArgumentOutOfRangeException(nameof(conditionType), conditionType, "Unsupported logical operator.")
        };

        /// <summary>
        /// معتبر بودن عملیات کلید خارجی را بررسی و شکل استاندارد آن را بازمی‌گرداند.
        /// </summary>
        /// <param name="action">عملیات مورد استفاده در <c>ON DELETE</c> یا <c>ON UPDATE</c>.</param>
        /// <returns>عملیات معتبر با حروف بزرگ.</returns>
        /// <exception cref="ArgumentException">اگر عملیات کلید خارجی پشتیبانی نشود، پرتاب می‌شود.</exception>
        private static string ValidateForeignKeyAction(string action)
        {
            var normalized = string.IsNullOrWhiteSpace(action)
                ? "NO ACTION"
                : string.Join(" ", action.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (!ForeignKeyActions.Contains(normalized))
                throw new ArgumentException($"Unsupported foreign-key action '{action}'.", nameof(action));
            return normalized.ToUpperInvariant();
        }

        /// <summary>
        /// متد برای دریافت نوع داده‌های SQLite از نوع C#
        /// </summary>
        /// <param name="type">نوع داده C#</param>
        /// <returns>نوع داده معادل SQLite</returns>
        private static string GetSqLiteType(Type type) =>
            type switch
            {
                { } when type == typeof(int) || type == typeof(long) || type == typeof(bool) => "INTEGER",
                { } when type == typeof(double) || type == typeof(float) => "REAL",
                { } when type == typeof(string) || type == typeof(DateTime) => "TEXT",
                _ => "TEXT"
            };

        /// <summary>
        /// این متد یک expression از نوع <see cref="Expression{Func{T,object}}"/> برای دسترسی به یک ویژگی خاص از نوع <typeparamref name="T"/> ایجاد می‌کند.
        /// </summary>
        /// <typeparam name="T">نوع شیء که ویژگی مورد نظر در آن قرار دارد.</typeparam>
        /// <param name="propertyName">نام ویژگی که می‌خواهیم آن را به عنوان یک expression دسترسی پیدا کنیم.</param>
        /// <returns>یک <see cref="Expression{Func{T,object}}"/> که به ویژگی مشخص شده اشاره دارد.</returns>
        private static Expression<Func<T, object>> CreateKeySelector<T>(string propertyName)
        {
            var parameter = Expression.Parameter(typeof(T), "g");
            var property = Expression.Property(parameter, propertyName);
            var convertedProperty = Expression.Convert(property, typeof(object));
            var lambda = Expression.Lambda<Func<T, object>>(convertedProperty, parameter);
            return lambda;
        }

        /// <summary>
        /// این تابع برای ساخت لیست شرایط (WHERE) و پارامترهای مربوطه از یک دیکشنری شامل عبارات لامبدا و مقادیر استفاده می‌کند.
        /// این تابع برای تبدیل شرایط لامبدا به فرمت قابل استفاده در کوئری‌های SQL طراحی شده است.
        /// </summary>
        /// <typeparam name="T">نوع مدلی که شرایط بر اساس آن تعریف شده‌اند.</typeparam>
        /// <param name="conditions">دیکشنری شامل عبارات لامبدا به عنوان کلید و مقادیر مربوطه به عنوان مقدار.</param>
        /// <returns>یک تاپل شامل لیست شرایط (WHERE) و دیکشنری پارامترها.</returns>
        /// <exception cref="InvalidOperationException">اگر نوع عبارت لامبدا پشتیبانی نشود، این خطا پرتاب می‌شود.</exception>
        private static (List<string> conditionList, Dictionary<string, object> parameters) BuildConditions<T>(
            Dictionary<Expression<Func<T, object>>, object> conditions)
        {
            var conditionList = new List<string>();
            var parameters = new Dictionary<string, object>();

            foreach (var condition in conditions)
            {
                if (condition.Key.Body is MemberExpression memberExpression)
                {
                    var columnName = QuoteIdentifier(memberExpression.Member.Name);
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
                    var columnName = QuoteIdentifier(unaryMemberExpression.Member.Name);
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

        /// <summary>
        /// این تابع برای استخراج نام فیلد (ستون) از یک عبارت لامبدا استفاده می‌شود.
        /// این تابع برای تبدیل عبارات لامبدا به نام فیلدهای مدل طراحی شده است.
        /// </summary>
        /// <typeparam name="T">نوع مدلی که عبارت لامبدا بر اساس آن تعریف شده است.</typeparam>
        /// <param name="keySelector">عبارت لامبدا که فیلد مورد نظر را انتخاب می‌کند.</param>
        /// <returns>نام فیلد (ستون) استخراج شده از عبارت لامبدا.</returns>
        /// <exception cref="ArgumentNullException">اگر عبارت لامبدا null باشد، این خطا پرتاب می‌شود.</exception>
        /// <exception cref="ArgumentException">اگر عبارت لامبدا معتبر نباشد، این خطا پرتاب می‌شود.</exception>
        private static string ExtractKeyName<T>(Expression<Func<T, object>> keySelector)
        {
            if (keySelector == null)
                throw new ArgumentNullException(nameof(keySelector), "Key selector must be provided.");

            return keySelector.Body switch
            {
                // Extract the property name from a MemberExpression (direct property access)
                MemberExpression memberExpression => memberExpression.Member.Name,

                // Handle UnaryExpression (casting) where the operand is a MemberExpression
                UnaryExpression { Operand: MemberExpression unaryMemberExpression } => unaryMemberExpression.Member.Name,

                // Throw exception for any invalid expression type
                _ => throw new ArgumentException("Invalid key selector expression.", nameof(keySelector))
            };
        }

    }
}
