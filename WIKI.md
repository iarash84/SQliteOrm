# SQliteOrm Project Wiki

[English](#english) | [فارسی](#فارسی) | [README](README.md) | [README فارسی](README.fa.md)

---

# English

## Overview

SQliteOrm is a synchronous SQLite micro-ORM for .NET 8. The current API is instance-based, metadata-driven, strongly typed where practical, and parameterized by default.

It covers entity mapping, schema creation, CRUD, fluent predicates, ordering, paging, joins, transactions, migrations, partial updates, native upsert, and raw SQL. It intentionally does not implement a full LINQ provider or `IQueryable<T>`.

## Architecture

```text
Entity type
   ▼
EntityMapCache ── reflection once per CLR type
   ├── EntityMap: table and primary key
   └── PropertyMap: column, CLR/SQLite type, nullability, constraints
          ├── schema and migrations
          ├── CRUD and native upsert
          ├── query translation
          └── result materialization

Expression<Func<T, bool>>
   ▼
ExpressionTranslator
   ▼
SQL expression AST
   ▼
SQLite compiler ── SQL + parameters
   ▼
Command infrastructure ── connection / transaction / disposal
```

| Folder | Responsibility |
| --- | --- |
| `Mapping` | Cached `EntityMap` and `PropertyMap` metadata. |
| `TypeMapping` | CLR ↔ SQLite affinity and conversion. |
| `Querying` | Expression translation, AST, compiler, fluent query, and joins. |
| `Core` / `Commands` | CRUD and key lookup operations. |
| `Infrastructure` | Commands, connections, parameters, and materialization. |
| `Transactions` | Shared connection and transaction lifecycle. |
| `Migrations` | Ordered transactional schema changes and history. |
| `Compatibility` | Legacy public wrappers retained for source compatibility. |

## Configuration and lifecycle

```csharp
using SQliteOrm;

using var db = new SqliteOrm(new SqliteOrmOptions
{
    ConnectionString = "Data Source=application.db",
    EnableForeignKeys = true,
    EnableWal = true,
    BusyTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = 30
});
```

An ORM instance owns configuration rather than one permanently open connection. Ordinary operations open their own connections; a transaction shares one connection for its callback. Separate instances can target separate databases.

`SqLiteOrm.Initialize/Instance` remains only as a compatibility facade. Prefer constructing and injecting `SqliteOrm`.

## Entity mapping

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("users")]
public sealed class User
{
    [Key, AutoIncrement]
    public long UserKey { get; set; }

    [Column("email_address"), Required, Unique]
    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public UserRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
    public string? Nickname { get; set; }
    public byte[]? Avatar { get; set; }

    [NotMapped]
    public string? UiLabel { get; set; }
}
```

- `[Key]` is independent of the property name.
- `[AutoIncrement]` is valid on an `int` or `long` primary key.
- Composite keys are rejected.
- `[Table]` and `[Column]` names are reused throughout the ORM.
- `[Required]`, nullable metadata, `[Unique]`, `[NotMapped]`, and `[ForeignKey]` affect the central property map.
- Generated keys are excluded from inserts and assigned back after successful insertion.

Supported values include integer and floating-point types, decimal, bool, string, blobs, enums, GUIDs, date/time types, and their nullable forms. Conversion is centralized in `SqliteTypeHandler`.

## Schema and migrations

```csharp
db.CreateTable<User>();
```

`CreateTable<T>()` creates a missing table; it does not upgrade an existing schema. Use migrations for deployed databases:

```csharp
using SQliteOrm.Migrations;

public sealed class Migration002_AddNickname : Migration
{
    public override void Up(MigrationBuilder migration) =>
        migration.AddColumn<User>(x => x.Nickname, nullable: true);

    public override void Down(MigrationBuilder migration)
    {
        // DropColumn requires a table rebuild and is not in the initial DSL.
    }
}

db.Migrate(new Migration002_AddNickname());
```

Migrations run once in ordinal ID order. Each migration and its history record run in one transaction. History is stored in `__SQliteOrmMigrations`.

The builder supports raw SQL, create/drop table, add column, create/drop index, and rename table/column. Table-rebuild operations are intentionally unsupported.

## CRUD and native upsert

```csharp
var user = new User
{
    Email = "ada@example.com",
    IsActive = true,
    Role = UserRole.Admin,
    CreatedAt = DateTimeOffset.UtcNow
};

db.Insert(user);
db.Update(user);
db.Delete<User, long>(user.UserKey);
```

Bulk insert is transactional:

```csharp
db.Insert(new List<User>
{
    new() { Email = "one@example.com" },
    new() { Email = "two@example.com" }
});
```

Upsert uses one SQLite-native atomic statement:

```csharp
db.Upsert(user, conflictOn: x => x.Email);
```

The conflict target must be the mapped primary key or a `[Unique]` property. No preliminary existence query is executed.

## Fluent queries

```csharp
var cutoff = DateTime.UtcNow.AddMonths(-6);

var users = db.Table<User>()
    .Where(x => x.IsActive && (x.LastLogin == null || x.LastLogin >= cutoff))
    .OrderBy(x => x.Email)
    .ThenByDescending(x => x.CreatedAt)
    .Skip(20)
    .Take(20)
    .ToList();
```

`Where`, ordering, and pagination only build state. Terminal methods are `ToList`, `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Any`, and `Count`.

```csharp
User? user = db.FirstOrDefault<User>(x => x.Email == email);
bool exists = db.Any<User>(x => x.Email == email);
int count = db.Count<User>(x => x.IsActive);
User? byKey = db.Find<User, long>(userKey);
```

Supported predicates include comparisons, `&&`, `||`, `!`, null checks, boolean properties, string `Contains`/`StartsWith`/`EndsWith`, and collection `Contains` translated to `IN`. Unsupported nodes throw `NotSupportedException`; arbitrary expressions are not evaluated silently.

## Partial update and predicate delete

```csharp
int changed = db.Update<User>()
    .Set(x => x.IsActive, false)
    .Set(x => x.Nickname, null)
    .Where(x => x.LastLogin < cutoff)
    .Execute();
```

Partial update requires at least one `Set` and one `Where`. Values are parameters, ignored properties are rejected, and generated keys cannot be modified.

```csharp
int deleted = db.Delete<User>(x => !x.IsActive && x.LastLogin < cutoff);
int allDeleted = db.DeleteAll<User>(); // explicit full-table operation
```

## Typed joins

```csharp
var results = db.Table<Order>()
    .Join<User>(order => order.UserKey, user => user.UserKey)
    .Where((order, user) => user.IsActive)
    .Select((order, user) => new OrderSummary
    {
        OrderKey = order.OrderKey,
        CustomerEmail = user.Email
    })
    .ToList();
```

The current implementation supports one `INNER JOIN`, typed mapped keys, joined predicates, and member-initializer projections. LEFT JOIN and chained joins are not implemented.

## Transactions

```csharp
db.Transaction(tx =>
{
    tx.Insert(order);
    tx.Update(user);
    tx.Execute("INSERT INTO audit_log (message) VALUES (@message)",
        new { message = "Order created" });
});
```

The callback shares one SQLite connection and transaction. Success commits; failure attempts rollback and rethrows the original exception. Nested transactions are rejected, and the session becomes invalid after its callback.

## Raw SQL

```csharp
var users = db.Query<User>(
    "SELECT * FROM users WHERE CreatedAt >= @since AND IsActive = @active",
    new { since, active = true });

db.ExecuteNonQuery(
    "UPDATE users SET IsActive = @active WHERE UserKey = @id",
    new { active = false, id = userKey });

int total = db.ExecuteScalar<int>("SELECT COUNT(*) FROM users");
```

Anonymous objects are recommended; dictionaries remain available for dynamic parameters. Parameter metadata is cached, and values use the central converter. Values are never replaced into SQL text. Raw SQL structure and identifiers remain the caller's responsibility.

## Concurrency and limitations

- The public API is synchronous; async commands are not implemented.
- Ordinary reads use independent connections.
- Writes on one instance are serialized; separate instances rely on SQLite locking.
- SQLite remains a single-writer database; WAL improves coexistence but not write concurrency.
- Do not dispose an instance while other callers use it.
- A regular `:memory:` database is connection-scoped and unsuitable for separate multi-operation connections.
- `IQueryable<T>`, change tracking, lazy loading, composite keys, custom converters, interceptors, and distributed migration locking are not implemented.

## Compatibility and development

Legacy singleton, dictionary query, property/value lookup, and string/tuple relation APIs remain available for source compatibility. New code should use instance-based `SqliteOrm`, `Table<T>()`, typed predicates and joins, partial update, migrations, transactions, and anonymous-object raw parameters.

```powershell
dotnet build SQliteOrm.sln --configuration Release
dotnet test SQliteOrm.sln --configuration Release --no-build
```

---

# فارسی

## معرفی

SQliteOrm یک micro-ORM همگام و ویژهٔ SQLite برای .NET 8 است. API فعلی instance-based، مبتنی بر metadata، تا حد ممکن نوع‌امن و به‌صورت پیش‌فرض پارامتری است.

کتابخانه نگاشت entity، ساخت schema، CRUD، predicateهای fluent، ordering، paging، join، transaction، migration، partial update، native upsert و SQL خام را پوشش می‌دهد؛ اما LINQ provider کامل یا `IQueryable<T>` نیست.

## معماری

```text
Entity type
   ▼
EntityMapCache ── reflection فقط یک‌بار برای هر CLR type
   ├── EntityMap: جدول و primary key
   └── PropertyMap: ستون، CLR/SQLite type، nullability و constraint
          ├── schema و migration
          ├── CRUD و native upsert
          ├── ترجمه query
          └── materialization نتیجه

Expression<Func<T, bool>>
   ▼
ExpressionTranslator → SQL AST → SQLite compiler
   ▼
SQL و parameterها
   ▼
Command infrastructure → connection / transaction / disposal
```

| پوشه | مسئولیت |
| --- | --- |
| `Mapping` | metadata کش‌شدهٔ `EntityMap` و `PropertyMap` |
| `TypeMapping` | تبدیل CLR و SQLite |
| `Querying` | expression، AST، compiler، fluent query و join |
| `Core` / `Commands` | CRUD و lookup کلید |
| `Infrastructure` | command، connection، parameter و materialization |
| `Transactions` | چرخهٔ connection و transaction مشترک |
| `Migrations` | تغییر schema مرتب و transactional |
| `Compatibility` | wrapperهای عمومی قدیمی برای source compatibility |

## تنظیم و چرخهٔ عمر

```csharp
using SQliteOrm;

using var db = new SqliteOrm(new SqliteOrmOptions
{
    ConnectionString = "Data Source=application.db",
    EnableForeignKeys = true,
    EnableWal = true,
    BusyTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = 30
});
```

ORM instance تنظیمات را نگه می‌دارد، نه یک connection دائماً باز. عملیات عادی connection جدا دارند و transaction در callback یک connection مشترک می‌سازد. برای دیتابیس‌های متفاوت می‌توان instanceهای مستقل داشت.

`SqLiteOrm.Initialize/Instance` فقط facade سازگاری است. در کد جدید `SqliteOrm` را بسازید و تزریق کنید.

## نگاشت entity

```csharp
[Table("users")]
public sealed class User
{
    [Key, AutoIncrement]
    public long UserKey { get; set; }

    [Column("email_address"), Required, Unique]
    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public UserRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
    public string? Nickname { get; set; }
    public byte[]? Avatar { get; set; }

    [NotMapped]
    public string? UiLabel { get; set; }
}
```

- `[Key]` به نام پراپرتی وابسته نیست.
- `[AutoIncrement]` روی primary key از نوع `int` یا `long` معتبر است.
- composite key رد می‌شود.
- نام‌های `[Table]` و `[Column]` در همه زیرسیستم‌ها استفاده می‌شوند.
- `[Required]`، nullability، `[Unique]`، `[NotMapped]` و `[ForeignKey]` در metadata مرکزی ثبت می‌شوند.
- کلید generated از insert حذف و پس از موفقیت به entity برگردانده می‌شود.

Typeهای عددی، bool، string، blob، enum، Guid، typeهای تاریخ/زمان و حالت nullable آن‌ها پشتیبانی می‌شوند و تبدیل در `SqliteTypeHandler` متمرکز است.

## Schema و Migration

```csharp
db.CreateTable<User>();
```

`CreateTable<T>()` فقط جدول مفقود را می‌سازد و schema موجود را ارتقا نمی‌دهد:

```csharp
public sealed class Migration002_AddNickname : Migration
{
    public override void Up(MigrationBuilder migration) =>
        migration.AddColumn<User>(x => x.Nickname, nullable: true);

    public override void Down(MigrationBuilder migration)
    {
        // DropColumn به بازسازی جدول نیاز دارد و در DSL اولیه وجود ندارد.
    }
}

db.Migrate(new Migration002_AddNickname());
```

Migrationها فقط یک‌بار و به‌ترتیب ordinal شناسه اجرا می‌شوند. هر migration و history آن یک transaction مشترک دارند و history در `__SQliteOrmMigrations` ذخیره می‌شود. عملیات بازسازی جدول عمداً پشتیبانی نمی‌شوند.

## CRUD و Upsert

```csharp
var user = new User
{
    Email = "ada@example.com",
    IsActive = true,
    Role = UserRole.Admin,
    CreatedAt = DateTimeOffset.UtcNow
};

db.Insert(user);
db.Update(user);
db.Delete<User, long>(user.UserKey);
```

Bulk insert در transaction اجرا می‌شود. Upsert نیز با یک دستور اتمیک SQLite و بدون existence query اولیه انجام می‌شود:

```csharp
db.Upsert(user, conflictOn: x => x.Email);
```

Conflict target باید primary key یا پراپرتی `[Unique]` باشد.

## Query نوع‌امن

```csharp
var cutoff = DateTime.UtcNow.AddMonths(-6);

var users = db.Table<User>()
    .Where(x => x.IsActive && (x.LastLogin == null || x.LastLogin >= cutoff))
    .OrderBy(x => x.Email)
    .ThenByDescending(x => x.CreatedAt)
    .Skip(20)
    .Take(20)
    .ToList();
```

`Where`، ordering و paging فقط state می‌سازند. اجرا در `ToList`، `First`، `FirstOrDefault`، `Single`، `SingleOrDefault`، `Any` یا `Count` انجام می‌شود.

```csharp
User? user = db.FirstOrDefault<User>(x => x.Email == email);
bool exists = db.Any<User>(x => x.Email == email);
int count = db.Count<User>(x => x.IsActive);
User? byKey = db.Find<User, long>(userKey);
```

Comparison، `&&`، `||`، `!`، null check، پراپرتی bool، متدهای رشته و collection `Contains` پشتیبانی می‌شوند. node پشتیبانی‌نشده به‌جای evaluate بی‌صدا، `NotSupportedException` می‌دهد.

## Partial Update و Delete

```csharp
int changed = db.Update<User>()
    .Set(x => x.IsActive, false)
    .Set(x => x.Nickname, null)
    .Where(x => x.LastLogin < cutoff)
    .Execute();
```

حداقل یک `Set` و یک `Where` لازم است. همه مقدارها parameter هستند و پراپرتی ignored یا generated قابل تغییر نیست.

```csharp
int deleted = db.Delete<User>(x => !x.IsActive && x.LastLogin < cutoff);
int allDeleted = db.DeleteAll<User>();
```

## Join نوع‌امن

```csharp
var results = db.Table<Order>()
    .Join<User>(order => order.UserKey, user => user.UserKey)
    .Where((order, user) => user.IsActive)
    .Select((order, user) => new OrderSummary
    {
        OrderKey = order.OrderKey,
        CustomerEmail = user.Email
    })
    .ToList();
```

نسخه فعلی یک `INNER JOIN`، کلید نگاشت‌شده، predicate join و projection با member initializer را پشتیبانی می‌کند. LEFT JOIN و join زنجیره‌ای وجود ندارند.

## Transaction

```csharp
db.Transaction(tx =>
{
    tx.Insert(order);
    tx.Update(user);
    tx.Execute("INSERT INTO audit_log (message) VALUES (@message)",
        new { message = "Order created" });
});
```

همه عملیات callback یک connection و transaction دارند. موفقیت commit می‌شود؛ خطا rollback را تلاش کرده و exception اصلی را دوباره پرتاب می‌کند. nested transaction رد می‌شود و session بعد از callback معتبر نیست.

## SQL خام

```csharp
var users = db.Query<User>(
    "SELECT * FROM users WHERE CreatedAt >= @since AND IsActive = @active",
    new { since, active = true });

db.ExecuteNonQuery(
    "UPDATE users SET IsActive = @active WHERE UserKey = @id",
    new { active = false, id = userKey });
```

Anonymous object روش پیشنهادی است و Dictionary برای پارامترهای پویا باقی مانده است. metadata پارامتر cache می‌شود و valueها از converter مرکزی عبور می‌کنند. هیچ مقداری با replacement وارد SQL نمی‌شود؛ ساختار و identifierهای SQL خام بر عهده caller است.

## Concurrency و محدودیت‌ها

- API عمومی همگام است و async command ندارد.
- Readهای عادی connection مستقل دارند.
- Writeها روی یک instance سری می‌شوند؛ instanceهای جدا به locking SQLite متکی‌اند.
- SQLite یک writer هم‌زمان دارد؛ WAL فقط هم‌زیستی reader/writer را بهتر می‌کند.
- هم‌زمان با استفاده callerها instance را Dispose نکنید.
- دیتابیس عادی `:memory:` به connection وابسته است.
- `IQueryable<T>`، change tracking، lazy loading، composite key، custom converter، interceptor و distributed migration lock وجود ندارند.

## سازگاری و توسعه

Singleton، queryهای Dictionary، lookup مبتنی بر property/value و relation APIهای string/tuple برای source compatibility باقی مانده‌اند. کد جدید باید `SqliteOrm` instance-based، `Table<T>()`، predicate و join نوع‌امن، partial update، migration، transaction و anonymous-object parameter را ترجیح دهد.

```powershell
dotnet build SQliteOrm.sln --configuration Release
dotnet test SQliteOrm.sln --configuration Release --no-build
```
