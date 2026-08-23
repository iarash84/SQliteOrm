# SQliteOrm

`SQliteOrm` is a small, attribute-based SQLite ORM for .NET 8. It creates tables from C# models and provides common CRUD operations, filters, ordering, joins, parameterized SQL, and scalar queries.

> The English documentation is the primary reference. A Persian quick guide follows below.
>
> Every executable example in the primary reference is followed by the SQL it emits or executes. Setup and model-definition examples do not execute SQL.

## Contents

- [Requirements and setup](#requirements-and-setup)
- [Define a model](#define-a-model)
- [Create tables](#create-tables)
- [Create records](#create-records)
- [Transactions](#transactions)
- [Strongly typed queries](#strongly-typed-queries)
- [Read records](#read-records)
- [Update, upsert, and delete](#update-upsert-and-delete)
- [Relationships and joins](#relationships-and-joins)
- [Raw SQL](#raw-sql)
- [Attributes and supported types](#attributes-and-supported-types)
- [Common mistakes](#common-mistakes)
- [راهنمای فارسی](#راهنمای-فارسی)

## Requirements and setup

- .NET 8 SDK
- SQLite is provided through `System.Data.SQLite.Core`.

To use the project from this repository, add a project reference:

```xml
<ItemGroup>
  <ProjectReference Include="..\SQliteOrm\SQliteOrm.csproj" />
</ItemGroup>
```

Then import the namespaces used by the examples:

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using SQliteOrm;
```

Initialize the singleton once, before accessing `SqLiteOrm.Instance`:

```csharp
var databasePath = Path.Combine(AppContext.BaseDirectory, "app.db");
SqLiteOrm.Initialize(databasePath);

var db = SqLiteOrm.Instance;
```

Calling `SqLiteOrm.Instance` before `Initialize` throws `InvalidOperationException`. The database path cannot be null, empty, or whitespace.

## Define a model

By default, the table name is the C# class name and the column name is the property name. `[Table]` and `[Column]` override those names. Public readable properties are mapped unless they have `[NotMapped]`.

```csharp
public enum UserRole
{
    User,
    Admin
}

public sealed class User
{
    [Key]
    public int Id { get; set; }

    [Required]
    [Unique]
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public int Age { get; set; }
    public bool IsActive { get; set; }
    public double Credit { get; set; }
    public DateTime CreatedAt { get; set; }
    public UserRole Role { get; set; }
    public string? Nickname { get; set; }

    [NotMapped]
    public string? DisplayText { get; set; }
}
```

`[Key]` identifies the primary key independently of its property name. Add `[AutoIncrement]` to an `int` or `long` key when SQLite should generate it. For backward compatibility, an `int` or `long` key named `Id` also uses auto-increment by convention. `[Required]` creates `NOT NULL`, and `[Unique]` creates `UNIQUE`.

## Create tables

Create the table before inserting or querying data. It is safe to call this repeatedly because the generated SQL uses `CREATE TABLE IF NOT EXISTS`.

```csharp
var db = SqLiteOrm.Instance;
db.CreateTable<User>();
```

SQL executed: `CREATE TABLE IF NOT EXISTS "User" (...);`

Create every table used by your application during startup:

```csharp
db.CreateTable<User>();
db.CreateTable<Category>();
db.CreateTable<Product>();
```

SQL executed: one `CREATE TABLE IF NOT EXISTS "..." (...);` statement for each model.

## Create records

### Insert one record

`Insert` returns the SQLite-generated row ID.

```csharp
var user = new User
{
    Email = "ada@example.com",
    DisplayName = "Ada Lovelace",
    IsActive = true,
    Credit = 25.50,
    CreatedAt = DateTime.UtcNow,
    Role = UserRole.Admin
};

db.Insert(user);
Console.WriteLine(user.Id);
```

SQL executed: `INSERT INTO "User" ("Email", "DisplayName", "IsActive", "Credit", "CreatedAt", "Role", "Nickname") VALUES (@Email, @DisplayName, @IsActive, @Credit, @CreatedAt, @Role, @Nickname); SELECT last_insert_rowid();`

Database-generated keys and `[NotMapped]` properties are excluded from the insert statement. Manually assigned keys, including `Guid` keys, are inserted normally. Generated integer keys are written back to the entity. The legacy `int` return value is the SQLite row ID.

### Insert many records

The list overload inserts all rows in one transaction. If a row fails, the transaction is rolled back.

```csharp
db.Insert(new List<User>
{
    new() { Email = "grace@example.com", DisplayName = "Grace", CreatedAt = DateTime.UtcNow },
    new() { Email = "linus@example.com", DisplayName = "Linus", CreatedAt = DateTime.UtcNow }
});
```

SQL executed: `INSERT INTO "User" (...) VALUES (...);` once per item, inside one transaction.

Do not include null elements in the list; that raises `ArgumentException`.

## Transactions

Use `Transaction` when several ORM operations must commit or roll back as one unit:

```csharp
db.Transaction(tx =>
{
    tx.Insert(order);
    tx.Update(customer);
    tx.Insert(payment);

    var pending = tx.Table<Order>()
        .Where(item => item.Status == OrderStatus.Pending)
        .ToList();
});
```

The ORM opens one connection, begins one SQLite transaction, and binds every session operation to both. It commits only after the callback returns successfully. If any operation or the callback throws, it attempts rollback and rethrows the original exception. The transaction, connection, and commands are disposed afterward.

The session supports `Insert`, bulk `Insert`, `Update`, strongly typed `Delete`, primary-key `Delete`, `Table<T>()`, raw `Query`, `ExecuteScalar`, and `Execute`. A session cannot be used after its callback ends.

Nested transactions are rejected with `InvalidOperationException`; they never create an independent inner transaction. Bulk insert uses this same infrastructure: outside a transaction it creates one transaction, while inside a transaction it reuses the active connection and transaction.

## Strongly typed queries

`Table<T>()` is the recommended query API. It builds query state without accessing the database; SQL executes only when a terminal method is called.

```csharp
List<User> users = db.Table<User>()
    .Where(user => user.IsActive)
    .Where(user => user.Credit >= 100)
    .ToList();
```

SQL executed: `SELECT * FROM "User" WHERE (("IsActive" = @p0) AND ("Credit" >= @p1));`

Multiple `Where` calls are combined with `AND`. Queries also support `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Skip`, and `Take`. Terminal methods are `ToList`, `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Any`, and `Count`. `First` and `FirstOrDefault` use `LIMIT 1`; `Single` reads at most two rows; `Any` uses `EXISTS`; and `Count` executes `COUNT(*)` in SQLite.

Runtime values, including captured variables, are always parameters:

```csharp
var minimumAge = 18;
var adults = db.Table<User>()
    .Where(user => user.IsActive && user.Age >= minimumAge)
    .ToList();
```

The predicate DSL supports comparisons, boolean operators, null checks, `string.Contains`, `StartsWith`, `EndsWith`, and collection membership:

```csharp
var ids = new[] { 1, 4, 9 };
var selected = db.Table<User>()
    .Where(user => ids.Contains(user.Id) && user.Email.StartsWith("admin"))
    .ToList();
```

## Read records

### Get a record by primary key

```csharp
User? user = db.FindById<User>(42);

if (user is not null)
    Console.WriteLine(user.Email);
```

SQL executed: `SELECT * FROM "User" WHERE "Id" = @Id;`

For any primary-key type or property name, use the metadata-driven overload:

```csharp
User? user = db.Find<User, Guid>(userKey);
```

### Find one record by a column

```csharp
User? user = db.FirstOrDefault<User>(u => u.Email == "ada@example.com");
```

SQL executed: `SELECT * FROM "User" WHERE ("Email" = @p0) LIMIT 1;`

### Find one record with multiple conditions

```csharp
User? admin = db.Table<User>()
    .Where(u => u.IsActive && u.Role == UserRole.Admin)
    .FirstOrDefault();
```

SQL executed: `SELECT * FROM "User" WHERE (("IsActive" = @p0) AND ("Role" = @p1)) LIMIT 1;`

### Get all records

```csharp
List<User> users = db.Table<User>().ToList();
```

SQL executed: `SELECT * FROM "User";`

### Filter records

Use a boolean expression to compose conditions with normal C# operators.

```csharp
var activeAdmins = db.Table<User>()
    .Where(u => u.IsActive && u.Role == UserRole.Admin)
    .ToList();
```

SQL executed: `SELECT * FROM "User" WHERE (("IsActive" = @p0) AND ("Role" = @p1));`

Use `||` to match either condition:

```csharp
var selectedUsers = db.Table<User>()
    .Where(u => u.Email == "ada@example.com" || u.DisplayName == "Grace")
    .ToList();
```

SQL executed: `SELECT * FROM "User" WHERE (("Email" = @p0) OR ("DisplayName" = @p1));`

### Query nullable columns

Passing `null` creates an `IS NULL` condition instead of `= NULL`.

```csharp
var anonymousUsers = db.Table<User>()
    .Where(u => u.Nickname == null)
    .ToList();
```

SQL executed: `SELECT * FROM "User" WHERE "Nickname" IS NULL;`

### Sort, limit, and offset

Use strongly typed ordering and pagination as part of the deferred query:

```csharp
var page = db.Table<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.Email)
    .ThenByDescending(u => u.CreatedAt)
    .Skip(40)
    .Take(20)
    .ToList();
```

SQL executed: `SELECT * FROM "User" WHERE ("IsActive" = @p0) ORDER BY "Email" ASC, "CreatedAt" DESC LIMIT @__limit OFFSET @__offset;`

`Skip` and `Take` reject negative values. Calling `OrderBy` starts or replaces ordering, while `ThenBy` requires an existing ordering. The legacy dictionary-based ordering and `limit`/`offset` arguments remain available for compatibility.

### Count and existence checks

```csharp
int totalUsers = db.Count<User>();

int activeUsers = db.Count<User>(u => u.IsActive);

bool existsById = db.Exists<User>(42);
bool existsByEmail = db.Any<User>(u => u.Email == "ada@example.com");
```

SQL executed: `SELECT COUNT(*) FROM "User";`, `SELECT COUNT(*) FROM "User" WHERE ("IsActive" = @p0);`, `SELECT 1 FROM "User" WHERE "Id" = @Id LIMIT 1;`, and `SELECT EXISTS(SELECT 1 FROM "User" WHERE ("Email" = @p0) LIMIT 1);`.

## Update, upsert, and delete

### Update by primary key

`Update(obj)` uses the property marked `[Key]`. It updates mapped properties other than that key.

```csharp
var user = db.FindById<User>(42);
if (user is not null)
{
    user.DisplayName = "Ada King";
    user.IsActive = false;
    db.Update(user);
}
```

SQL executed: `SELECT * FROM "User" WHERE "Id" = @Id;` followed by `UPDATE "User" SET ... WHERE "Id" = @Id;`.

### Update by another key

```csharp
var user = new User
{
    Id = 42,
    Email = "ada@example.com",
    DisplayName = "Ada King",
    IsActive = true
};

db.Update<User>(u => u.Email, user);
```

SQL executed: `UPDATE "User" SET "DisplayName" = @DisplayName, ... WHERE "Email" = @Email;`

When using a custom key, that key is used in the `WHERE` clause and is not updated.

### Upsert

`Upsert` checks whether a record exists using the selected property. It inserts when no record exists; otherwise it updates it.

```csharp
var user = new User
{
    Email = "ada@example.com",
    DisplayName = "Ada",
    IsActive = true,
    CreatedAt = DateTime.UtcNow
};

db.Upsert<User>(u => u.Email, user);

user.DisplayName = "Ada Lovelace";
db.Upsert<User>(u => u.Email, user);
```

SQL executed: each call first executes `SELECT 1 FROM "User" WHERE "Email" = @Email LIMIT 1;`, then executes either `INSERT INTO "User" (...) VALUES (...);` or `UPDATE "User" SET ... WHERE "Email" = @Email;`.

The selected upsert key must not be null.

### Delete

Use a strongly typed predicate for conditional deletes. The return value is the number of affected rows:

```csharp
var cutoff = DateTime.UtcNow.AddYears(-1);
int deleted = db.Delete<User>(user =>
    !user.IsActive && user.CreatedAt < cutoff);
```

SQL executed: `DELETE FROM "User" WHERE ((NOT ("IsActive" = @p0)) AND ("CreatedAt" < @p1));`

The predicate is required and every runtime value is parameterized. Deleting every row is deliberately separate and explicit:

```csharp
int deleted = db.DeleteAll<User>();
```

Primary-key and property/value overloads remain available for compatibility:

```csharp
db.Delete<User>(42);
db.Delete<User, Guid>(userKey);
db.Delete<User>(user => user.Email, "old@example.com");
```

## Relationships and joins

Define a foreign key using `ForeignKeyAttribute`. The attribute's first argument is the related table's class name. Valid `OnDelete` and `OnUpdate` values are `NO ACTION`, `RESTRICT`, `SET NULL`, `SET DEFAULT`, and `CASCADE`.

```csharp
public sealed class Category
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class Product
{
    [Key]
    public int Id { get; set; }

    [ForeignKey(nameof(Category), OnDelete = "CASCADE", OnUpdate = "RESTRICT")]
    public int CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    [NotMapped]
    public string? CategoryName { get; set; }
}
```

Create the referenced table first:

```csharp
db.CreateTable<Category>();
db.CreateTable<Product>();
```

SQL executed: `CREATE TABLE IF NOT EXISTS "Category" (...);` and `CREATE TABLE IF NOT EXISTS "Product" (... FOREIGN KEY("CategoryId") REFERENCES "Category"("Id") ...);`

### Join one related table

The related field is returned under the alias in the SQL result. This API maps the main table's mapped properties to `T`; use it when you need the main entities while joining for filtering, or use `Query<T>` with a dedicated projection model when you need to map a custom result shape.

```csharp
var products = db.GetAllWithRelation<Product, Category>(
    relationFieldName: nameof(Product.CategoryId),
    relatedFieldName: nameof(Category.Name),
    aliasName: nameof(Product.CategoryName),
    conditions: new() { [p => p.Name] = "Keyboard" });
```

SQL executed: `SELECT t.*, r."Name" AS "CategoryName" FROM "Product" t INNER JOIN "Category" r ON t."CategoryId" = r."Id" WHERE t."Name" = @Name;`

### Join with the dictionary-based API

```csharp
var products = db.GetAllWithRelations<Product>(
    mainTableAlias: "p",
    relationships: new Dictionary<string, (string relationFieldName, string relatedTableName, string tableRelationExistAlias)>
    {
        ["c"] = (nameof(Product.CategoryId), nameof(Category), "p")
    },
    additionalColumns: new List<(string tableAlias, string columnName, string aliasName)>
    {
        ("c", nameof(Category.Name), nameof(Product.CategoryName))
    },
    conditions: new Dictionary<string, object>
    {
        [nameof(Product.Name)] = "Keyboard"
    });
```

SQL executed: `SELECT "p".*, "c"."Name" AS "CategoryName" FROM "Product" "p" INNER JOIN "Category" "c" ON "p"."CategoryId" = "c"."Id" WHERE "p"."Name" = @Name;`

### Join with expression-based relations

```csharp
var products = db.GetAllWithRelations<Product>(
    mainTableAlias: "p",
    relationships: new List<(Expression<Func<Product, object>> relationExpression, string relatedTableName, string tableAlias)>
    {
        (p => p.CategoryId, nameof(Category), "c")
    },
    additionalColumns: new List<(string tableAlias, Expression<Func<Product, object>> columnExpression, string aliasName)>
    {
        ("c", p => p.Name, nameof(Product.CategoryName))
    },
    conditions: new Dictionary<Expression<Func<Product, object>>, object>
    {
        [p => p.Name] = "Keyboard"
    });
```

SQL executed: `SELECT "p".*, "c"."Name" AS "DescriptionAgain" FROM "Product" "p" INNER JOIN "Category" "c" ON "p"."CategoryId" = "c"."Id" WHERE "p"."Name" = @p0;`

## Raw SQL

For queries not covered by the ORM API, use parameterized raw SQL. Never concatenate user input into SQL text.

### Run a command without a result set

```csharp
db.ExecuteNonQuery(
    "UPDATE \"User\" SET \"Credit\" = \"Credit\" + @amount WHERE \"Id\" = @id",
    new Dictionary<string, object>
    {
        ["@amount"] = 10.0,
        ["@id"] = 42
    });
```

SQL executed exactly as supplied: `UPDATE "User" SET "Credit" = "Credit" + @amount WHERE "Id" = @id`.

### Query a list

```csharp
List<User> users = db.Query<User>(
    "SELECT * FROM \"User\" WHERE \"Credit\" >= @minimumCredit",
    new() { ["@minimumCredit"] = 100.0 });
```

SQL executed exactly as supplied: `SELECT * FROM "User" WHERE "Credit" >= @minimumCredit`.

Columns are mapped to public writable properties case-insensitively. It is fine to query a projection:

```csharp
public sealed class UserSummary
{
    public string Email { get; set; } = string.Empty;
    public double Credit { get; set; }
}

var summaries = db.Query<UserSummary>(
    "SELECT \"Email\", \"Credit\" FROM \"User\" WHERE \"IsActive\" = @active",
    new() { ["@active"] = true });
```

SQL executed exactly as supplied: `SELECT "Email", "Credit" FROM "User" WHERE "IsActive" = @active`.

### Query a scalar

```csharp
int activeUserCount = db.ExecuteScalar<int>(
    "SELECT COUNT(*) FROM \"User\" WHERE \"IsActive\" = @active",
    new() { ["@active"] = true });

double highestCredit = db.ExecuteScalar<double>("SELECT MAX(\"Credit\") FROM \"User\"");
string? missingValue = db.ExecuteScalar<string>("SELECT NULL");
```

SQL executed: `SELECT COUNT(*) FROM "User" WHERE "IsActive" = @active`, `SELECT MAX("Credit") FROM "User"`, and `SELECT NULL`.

`ExecuteScalar<T>` returns `default` when SQLite returns `NULL`.

## Attributes and supported types

| Attribute / type | Behavior |
| --- | --- |
| `[Key]` | Marks the primary key; the property name and type are not restricted to `Id` or integers. |
| `[AutoIncrement]` | Makes an `int` or `long` primary key SQLite-generated with `AUTOINCREMENT`. |
| `[Required]` | Adds `NOT NULL`. |
| `[Unique]` | Adds `UNIQUE`. |
| `[NotMapped]` | Excludes the property from table mapping. |
| `[Table]`, `[Column]` | Override the default table or column name. |
| `[ForeignKey("TableName")]` | Adds a foreign-key constraint; mapped related types use their actual `[Key]` column. |
| `byte`, `short`, `int`, `long`, `bool`, enums | Stored as SQLite `INTEGER`. |
| `double`, `float` | Stored as SQLite `REAL`. |
| `decimal` | Stored with SQLite `NUMERIC` affinity. |
| `byte[]` | Stored as SQLite `BLOB`. |
| `string`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `Guid` | Stored as SQLite `TEXT`; temporal values use invariant round-trip formats. |
| `Nullable<T>` | Uses the same affinity and conversion as `T`; database `NULL` materializes as `null`. |

## Common mistakes

- Call `Initialize` once before `Instance`.
- Call `CreateTable<T>()` before using a model's table.
- Strongly typed predicates support mapped properties, comparisons, boolean composition, null checks, supported string methods, and collection `Contains`; computed arithmetic and arbitrary method calls are rejected.
- Use `@parameterName` placeholders with `Query`, `ExecuteNonQuery`, and `ExecuteScalar` instead of string interpolation.
- Mark generated integer keys with `[AutoIncrement]`; `Insert` writes generated values back to entities automatically.
- The relation APIs map the main entity; use `Query<T>` and a dedicated projection model for custom result shapes.
- Create referenced tables before tables that declare foreign keys.

## Predicate support and limitations

The strongly typed public query API is backed by an internal predicate pipeline. It translates `Expression<Func<T, bool>>` into a small query AST and then compiles that AST to parameterized SQLite SQL. The older dictionary-based methods remain available for source compatibility but are no longer the recommended query style.

Prefer `Table<T>().Where(...)`, `FirstOrDefault(predicate)`, `Any(predicate)`, `Count(predicate)`, and `Delete(predicate)` for new code. `GetAll` condition dictionaries, `FindOneByKey`, property/value `Exists`, and property/value `Delete` remain compatibility APIs.

The initial translator supports `==`, `!=`, `>`, `>=`, `<`, `<=`, `&&`, `||`, `!`, null equality checks, `string.Contains`, `string.StartsWith`, `string.EndsWith`, and collection `Contains` as `IN`. Column names are resolved through entity metadata, captured values become parameters, LIKE wildcard characters are escaped, and grouping is preserved.

Arithmetic, arbitrary method calls, computed properties, navigation/member chains, string comparison overloads, and other expression nodes are intentionally unsupported. They raise `NotSupportedException` instead of being evaluated or inserted into SQL.

---

# راهنمای فارسی

این بخش خلاصه‌ای فارسی از راهنمای بالا است. برای جزئیات کامل و مثال‌های بیشتر، بخش انگلیسی را مرجع اصلی در نظر بگیرید.

## شروع سریع

ابتدا مسیر دیتابیس را مشخص کرده و ORM را مقداردهی کنید:

```csharp
using SQliteOrm;

SqLiteOrm.Initialize(Path.Combine(AppContext.BaseDirectory, "app.db"));
var db = SqLiteOrm.Instance;
```

مدل و جدول را بسازید:

```csharp
public sealed class Customer
{
    [Key]
    public int Id { get; set; }

    [Required]
    [Unique]
    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    public string? Nickname { get; set; }
}

db.CreateTable<Customer>();
```

## درج، خواندن، ویرایش و حذف

```csharp
var customer = new Customer
{
    Email = "ali@example.com",
    Name = "Ali",
    IsActive = true
};

customer.Id = db.Insert(customer);             // درج و دریافت Id

Customer? found = db.FindById<Customer>(customer.Id);
Customer? byEmail = db.FirstOrDefault<Customer>(c => c.Email == "ali@example.com");

customer.Name = "Ali Rezaei";
db.Update(customer);                           // ویرایش بر اساس Id

db.Delete<Customer>(customer.Id);              // حذف بر اساس Id
db.Delete<Customer>(c => c.Email, "ali@example.com"); // حذف با ستون دلخواه
```

## درج چند رکورد و Upsert

```csharp
db.Insert(new List<Customer>
{
    new() { Email = "a@example.com", Name = "A" },
    new() { Email = "b@example.com", Name = "B" }
});

var item = new Customer { Email = "a@example.com", Name = "نسخه جدید" };
db.Upsert<Customer>(c => c.Email, item);
```

## Transaction

```csharp
db.Transaction(tx =>
{
    tx.Insert(customer);
    tx.Update(order);
    tx.Execute("UPDATE \"Audit\" SET \"Status\" = @status",
        new() { ["@status"] = "Done" });
});
```

تمام عملیات callback از یک connection و transaction مشترک استفاده می‌کنند. در صورت خطا همه تغییرات rollback می‌شوند و nested transaction پشتیبانی نمی‌شود.

در `Upsert` اگر رکوردی با ایمیل موردنظر وجود داشته باشد، به‌روزرسانی می‌شود؛ در غیر این صورت درج می‌شود.

## فیلتر، مرتب‌سازی و صفحه‌بندی

```csharp
var activeCustomers = db.Table<Customer>()
    .Where(c => c.IsActive)
    .ToList();

var results = db.Table<Customer>()
    .Where(c => c.Name == "Ali" || c.Email == "sara@example.com")
    .OrderBy(c => c.Name)
    .Skip(10)
    .Take(10)
    .ToList();
```

برای مقدار `null` از شرط `IS NULL` استفاده می‌شود:

```csharp
var noExtraValue = db.Table<Customer>()
    .Where(c => c.Nickname == null)
    .ToList();
```

## شمارش و بررسی وجود

```csharp
int allCount = db.Count<Customer>();
int activeCount = db.Count<Customer>(c => c.IsActive);
bool exists = db.Any<Customer>(c => c.Email == "ali@example.com");
```

## اجرای SQL خام و امن

همیشه از پارامتر استفاده کنید و مقدار ورودی کاربر را به رشته SQL نچسبانید:

```csharp
db.ExecuteNonQuery(
    "UPDATE \"Customer\" SET \"IsActive\" = @active WHERE \"Email\" = @email",
    new() { ["@active"] = false, ["@email"] = "ali@example.com" });

var customers = db.Query<Customer>(
    "SELECT * FROM \"Customer\" WHERE \"Name\" = @name",
    new() { ["@name"] = "Ali" });

int count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Customer\"");
```

## نکات مهم

- پیش از استفاده از `SqLiteOrm.Instance` حتماً `Initialize` را فراخوانی کنید.
- قبل از درج یا خواندن داده، `CreateTable<T>()` را اجرا کنید.
- برای شرط‌ها فقط عبارت انتخاب ویژگی بنویسید؛ مانند `c => c.Email`.
- متدهای رابطه، مدل اصلی را نگاشت می‌کنند؛ برای خروجی سفارشی از `Query<T>` و یک مدل projection استفاده کنید.
- برای رابطه‌ها، جدول مرجع را زودتر ایجاد کنید.
