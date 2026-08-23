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
- [Migrations](#migrations)
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
using SQliteOrm.Migrations;
```

Create and dispose an independent ORM instance. This form is suitable for dependency injection and allows multiple databases in one process:

```csharp
using var db = new SqliteOrm("Data Source=app.db");
using var cache = new SqliteOrm("Data Source=cache.db");
```

For explicit database behavior, use options:

```csharp
using var db = new SqliteOrm(new SqliteOrmOptions
{
    ConnectionString = "Data Source=app.db",
    EnableForeignKeys = true,
    EnableWal = true,
    BusyTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = 30
});
```

`SqliteOrm` has no global mutable database state. Each instance owns its connection configuration; connections themselves are opened per operation or per transaction session. After `Dispose`, starting another database operation throws `ObjectDisposedException`.

The former singleton remains as a compatibility facade during migration:

```csharp
SqLiteOrm.Initialize("app.db");
SqLiteOrm legacyDb = SqLiteOrm.Instance;
```

New code should inject or directly construct `SqliteOrm` instead. The compatibility facade inherits the same implementation and does not maintain a second ORM code path.

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

`CreateTable<T>()` only creates a missing table. It does not compare, alter, or upgrade an existing schema. Use migrations for deployed databases whose schema changes over time.

## Migrations

Migrations are small ordered classes. Their stable ID defaults to the class name, so use sortable names such as `Migration001_CreateUsers` and never rename an applied migration.

```csharp
public sealed class Migration001_CreateUsers : Migration
{
    public override void Up(MigrationBuilder migration)
    {
        migration.CreateTable<User>();
        migration.CreateIndex<User>(user => user.Email, unique: true);
    }

    public override void Down(MigrationBuilder migration)
    {
        migration.DropIndex("IX_User_Email");
        migration.DropTable<User>();
    }
}

public sealed class Migration002_AddNickname : Migration
{
    public override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<User>(user => user.Nickname, nullable: true);
    }

    public override void Down(MigrationBuilder migration)
    {
        // Dropping a column requires a table rebuild and is intentionally
        // not provided by the first migration API.
    }
}
```

Apply migrations during application startup:

```csharp
db.Migrate(
    new Migration002_AddNickname(),
    new Migration001_CreateUsers());
```

The supplied order does not matter: migrations run by ID using ordinal ordering. Applied IDs and UTC timestamps are stored in `__SQliteOrmMigrations`. An applied migration is never run twice, including after the application restarts.

Each pending migration runs in its own transaction. Its schema operations and history insert commit together; if an operation fails, that migration is rolled back and its ID is not recorded. Earlier successful migrations remain applied.

The initial builder supports:

- `ExecuteSql`
- `CreateTable<T>` and `DropTable<T>`
- strongly typed `AddColumn`
- strongly typed `CreateIndex` and named `DropIndex`
- `RenameTable`
- strongly typed `RenameColumn`

`RenameColumn` requires SQLite 3.25 or newer. `AddColumn` deliberately rejects primary-key, auto-increment, and unique properties because SQLite cannot add those constraints with a simple `ALTER TABLE`. Dropping columns, changing column definitions, and other operations requiring table reconstruction are not implemented; use carefully reviewed `ExecuteSql` when you intentionally own that rebuild process.

To execute `Down` for the latest applied migration:

```csharp
db.RollbackLastMigration(
    new Migration001_CreateUsers(),
    new Migration002_AddNickname());
```

The matching applied migration must be supplied. Its `Down` operations and history deletion run in one transaction.

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

The session supports `Insert`, bulk `Insert`, `Update`, native `Upsert`, strongly typed `Delete`, primary-key `Delete`, `Table<T>()`, raw `Query`, `ExecuteScalar`, and `Execute`. A session cannot be used after its callback ends.

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

`Upsert` executes one atomic SQLite `INSERT ... ON CONFLICT ... DO UPDATE` statement. The conflict target must be a mapped primary key or `[Unique]` property.

```csharp
var user = new User
{
    Email = "ada@example.com",
    DisplayName = "Ada",
    IsActive = true,
    CreatedAt = DateTime.UtcNow
};

db.Upsert(user, conflictOn: u => u.Email);

user.DisplayName = "Ada Lovelace";
db.Upsert(user, conflictOn: u => u.Email);
```

SQL executed: `INSERT INTO "User" (...) VALUES (...) ON CONFLICT ("Email") DO UPDATE SET ... RETURNING "Id";`

No preliminary existence query is executed. Values are parameters, generated and primary-key columns are not updated, and generated keys are returned to the entity. To use the mapped primary key as the target, call `db.Upsert(entity)`. SQLite treats `NULL` values in a unique column as distinct, so a nullable conflict target containing `null` normally inserts a new row.

The older argument order remains available for compatibility:

```csharp
db.Upsert<User>(u => u.Email, user);
```

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

### Strongly typed inner join

Use mapped property expressions for both join keys, joined filtering, and projection:

```csharp
public sealed class ProductWithCategory
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
}

var search = "Key";
var products = db.Table<Product>()
    .Where(product => product.Name.Contains(search))
    .Join<Category>(
        product => product.CategoryId,
        category => category.Id)
    .Where((product, category) => category.Name != null)
    .Select((product, category) => new ProductWithCategory
    {
        ProductId = product.Id,
        ProductName = product.Name,
        CategoryName = category.Name
    })
    .ToList();
```

SQL executed: `SELECT "t0"."Id" AS "ProductId", "t0"."Name" AS "ProductName", "t1"."Name" AS "CategoryName" FROM "Product" AS "t0" INNER JOIN "Category" AS "t1" ON "t0"."CategoryId" = "t1"."Id" WHERE ...;`

Table and column names come from `EntityMap`, including `[Table]` and `[Column]` overrides. Joined predicates use the same expression AST and parameter compiler as normal queries. The initial public API supports `INNER JOIN` and direct-property object-initializer projections. Computed projection expressions, constructor projections, multiple joins, joined ordering/pagination, and public `LEFT JOIN` are intentionally deferred. The internal join type model already distinguishes inner and left joins for that future extension.

### Legacy single-relation API

The older string-based API remains available for compatibility:

```csharp
var products = db.GetAllWithRelation<Product, Category>(
    relationFieldName: nameof(Product.CategoryId),
    relatedFieldName: nameof(Category.Name),
    aliasName: nameof(Product.CategoryName));
```

### Legacy dictionary-based joins

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

### Legacy tuple-based relations

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

- Prefer constructing and injecting `SqliteOrm`; use `SqLiteOrm.Initialize/Instance` only for compatibility with older code.
- Call `CreateTable<T>()` before using a model's table.
- Strongly typed predicates support mapped properties, comparisons, boolean composition, null checks, supported string methods, and collection `Contains`; computed arithmetic and arbitrary method calls are rejected.
- Use `@parameterName` placeholders with `Query`, `ExecuteNonQuery`, and `ExecuteScalar` instead of string interpolation.
- Mark generated integer keys with `[AutoIncrement]`; `Insert` writes generated values back to entities automatically.
- Prefer `Table<T>().Join<TRight>(...).Select(...)`; legacy relation APIs remain for compatibility with existing string/tuple configurations.
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

using var db = new SqliteOrm("Data Source=app.db");
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
db.Upsert(item, conflictOn: c => c.Email);
```

## Migration و تغییر نسخهٔ پایگاه داده

`CreateTable<T>()` فقط جدولِ موجودنباشد را ایجاد می‌کند و ساختار جدول موجود را ارتقا نمی‌دهد. برای تغییر schema در نسخه‌های بعدی برنامه از migration استفاده کنید:

```csharp
public sealed class Migration002_AddNickname : Migration
{
    public override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<Customer>(x => x.Nickname, nullable: true);
    }

    public override void Down(MigrationBuilder migration)
    {
        // حذف ستون در API اولیه نیازمند بازسازی جدول است و پشتیبانی نمی‌شود.
    }
}

db.Migrate(new Migration002_AddNickname());
```

Migrationها بر اساس شناسه به‌ترتیب قطعی اجرا می‌شوند و شناسه و زمان UTC اجرا در جدول `__SQliteOrmMigrations` ثبت می‌شود. هر migration فقط یک‌بار و در transaction مستقل اجرا می‌شود؛ در صورت خطا هم تغییرات همان migration و هم ثبت تاریخچه rollback می‌شوند. عملیات اولیهٔ builder شامل `ExecuteSql`، ساخت و حذف جدول، افزودن ستون، ساخت و حذف index و تغییر نام جدول یا ستون است. عملیات نیازمند بازسازی جدول، از جمله حذف ستون، عمداً در نسخهٔ اول پشتیبانی نمی‌شوند.

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

## JOIN نوع‌امن

```csharp
var results = db.Table<Order>()
    .Join<Customer>(order => order.CustomerId, customer => customer.Id)
    .Where((order, customer) => customer.IsActive)
    .Select((order, customer) => new OrderResult
    {
        OrderId = order.Id,
        CustomerName = customer.Name
    })
    .ToList();
```

نام جدول و ستون از metadata خوانده می‌شود و مقدارهای فیلتر همگی پارامتری هستند. نسخه فعلی INNER JOIN و projection مستقیم با object initializer را پشتیبانی می‌کند؛ APIهای رشته‌ای قبلی فقط برای سازگاری باقی مانده‌اند.

`Upsert` با یک دستور اتمیک `INSERT ... ON CONFLICT ... DO UPDATE` اجرا می‌شود و پیش از آن query جداگانه‌ای برای بررسی وجود رکورد اجرا نمی‌کند.

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

- برای کد جدید یک نمونه مستقل `SqliteOrm` بسازید و آن را Dispose کنید؛ API سراسری `SqLiteOrm.Instance` فقط برای سازگاری نگه داشته شده است.
- قبل از درج یا خواندن داده، `CreateTable<T>()` را اجرا کنید.
- برای شرط‌ها فقط عبارت انتخاب ویژگی بنویسید؛ مانند `c => c.Email`.
- متدهای رابطه، مدل اصلی را نگاشت می‌کنند؛ برای خروجی سفارشی از `Query<T>` و یک مدل projection استفاده کنید.
- برای رابطه‌ها، جدول مرجع را زودتر ایجاد کنید.
