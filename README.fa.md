# SQliteOrm

فارسی | [English](README.md) | [ویکی پروژه](WIKI.md#فارسی)

SQliteOrm یک micro-ORM سبک، همگام و ویژهٔ SQLite برای .NET 8 است. API فعلی آن instance-based، مبتنی بر metadata، تا حد ممکن نوع‌امن و به‌صورت پیش‌فرض پارامتری است. هدف پروژه ارائهٔ یک LINQ provider کامل یا جایگزین Entity Framework نیست.

## راه‌اندازی و چرخهٔ عمر

برای هر تنظیم مستقل پایگاه داده یک نمونهٔ `SqliteOrm` بسازید:

```csharp
using SQliteOrm;

using var db = new SqliteOrm(new SqliteOrmOptions
{
    ConnectionString = "Data Source=app.db",
    EnableForeignKeys = true,
    EnableWal = true,
    BusyTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = 30
});
```

هر عملیات عادی connection خودش را باز و Dispose می‌کند. در transaction یک connection باز می‌شود و همهٔ عملیات callback از همان connection استفاده می‌کنند. `Dispose` مانع شروع عملیات جدید می‌شود؛ نمونهٔ ORM نمایندهٔ یک connection دائماً باز نیست. بنابراین می‌توان `SqliteOrm` را بدون وابستگی به DI framework در برنامه تزریق کرد.

`SqLiteOrm.Initialize/Instance` فقط برای سازگاری با API singleton قدیمی باقی مانده است. در کد جدید `SqliteOrm` را مستقیماً بسازید و تزریق کنید.

## نگاشت موجودیت

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("Users")]
public sealed class User
{
    [Key, AutoIncrement]
    public long UserId { get; set; }

    [Column("email_address"), Required, Unique]
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
    public string? Nickname { get; set; }
    public UserRole Role { get; set; }
    public byte[]? Avatar { get; set; }

    [NotMapped]
    public string? UiLabel { get; set; }
}
```

metadata نگاشت برای هر CLR type فقط یک‌بار با reflection ساخته و cache می‌شود. `[Key]` به نام پراپرتی وابسته نیست. composite key پشتیبانی نمی‌شود و مدل دارای چند `[Key]` رد می‌شود. `[AutoIncrement]` فقط روی کلید `int` یا `long` معتبر است. برای سازگاری، کلید عددی با نام `Id` نیز generated در نظر گرفته می‌شود؛ در مدل جدید بهتر است `[AutoIncrement]` را صریح بنویسید.

Attributeهای پشتیبانی‌شده:

| Attribute | رفتار |
| --- | --- |
| `[Table]` و `[Column]` | نام جدول یا ستون را تغییر می‌دهند. |
| `[Key]` | primary key یگانه را مشخص می‌کند. |
| `[AutoIncrement]` | `AUTOINCREMENT` را ایجاد و کلید عددی را generated می‌کند. |
| `[Required]` | قید `NOT NULL` ایجاد می‌کند. |
| `[Unique]` | قید `UNIQUE` ایجاد می‌کند. |
| `[NotMapped]` | پراپرتی را از کل نگاشت حذف می‌کند. |
| `[ForeignKey]` | foreign key با actionهای معتبر SQLite ایجاد می‌کند. |

نگاشت typeها:

| CLR type | نگاشت SQLite |
| --- | --- |
| `byte`، `short`، `int`، `long`، `bool` و enum | `INTEGER`؛ bool به‌شکل `0`/`1` و enum با مقدار underlying ذخیره می‌شود. |
| `float` و `double` | `REAL` |
| `decimal` | `NUMERIC` |
| `byte[]` | `BLOB` |
| `string` و `Guid` | `TEXT` |
| `DateTime`، `DateTimeOffset`، `DateOnly` و `TimeOnly` | `TEXT` با فرمت invariant و round-trip |
| نوع‌های nullable پشتیبانی‌شده | همان نگاشت نوع اصلی؛ `NULL` دیتابیس به `null` تبدیل می‌شود. |

فعلاً API عمومی برای ثبت custom type handler وجود ندارد. از typeهای پشتیبانی‌شده یا SQL خام با نمایش صریح استفاده کنید.

## ساخت schema و migration

```csharp
db.CreateTable<User>();
```

`CreateTable<T>()` فقط `CREATE TABLE IF NOT EXISTS` اجرا می‌کند و schema جدول موجود را مقایسه یا ارتقا نمی‌دهد. برای تغییر نسخهٔ دیتابیس از migration استفاده کنید:

```csharp
using SQliteOrm.Migrations;

public sealed class Migration002_AddNickname : Migration
{
    public override void Up(MigrationBuilder migration) =>
        migration.AddColumn<User>(user => user.Nickname, nullable: true);

    public override void Down(MigrationBuilder migration)
    {
        // حذف ستون نیازمند بازسازی جدول است و در DSL اولیه وجود ندارد.
    }
}

db.Migrate(new Migration002_AddNickname());
```

شناسهٔ migration به‌صورت پیش‌فرض نام کلاس است، با مقایسهٔ ordinal مرتب می‌شود و همراه زمان UTC در `__SQliteOrmMigrations` ثبت می‌شود. هر migration معلق و ثبت history آن داخل یک transaction اجرا می‌شوند. اگر migration بعدی خطا دهد، migrationهای موفق قبلی باقی می‌مانند. هر ID فقط یک‌بار اجرا می‌شود. migration را از یک مسیر هماهنگ در startup اجرا کنید؛ runner نقش distributed deployment lock ندارد.

`MigrationBuilder` از `ExecuteSql`، `CreateTable`، `DropTable`، `AddColumn`، `CreateIndex`، `DropIndex`، `RenameTable` و `RenameColumn` پشتیبانی می‌کند. `RenameColumn` به SQLite 3.25 یا جدیدتر نیاز دارد. `AddColumn` برای primary key، auto-increment و unique رد می‌شود. حذف ستون و عملیات نیازمند بازسازی جدول عمداً پشتیبانی نمی‌شوند. `RollbackLastMigration(...)` متد `Down` آخرین migration ارائه‌شده و اجراشده را اعمال می‌کند.

## Insert و Upsert

```csharp
var user = new User { Email = "ada@example.com", IsActive = true, Role = UserRole.Admin };
db.Insert(user);

db.Insert(new List<User>
{
    new() { Email = "a@example.com" },
    new() { Email = "b@example.com" }
});

db.Upsert(user, conflictOn: value => value.Email);
```

Insert پراپرتی ignored و generated را کنار می‌گذارد و کلید عددی generated را به entity برمی‌گرداند. Bulk insert از زیرساخت transaction استفاده می‌کند و یکجا rollback می‌شود. Upsert با یک دستور اتمیک `INSERT ... ON CONFLICT ... DO UPDATE` و بدون query وجود رکورد اجرا می‌شود. conflict target باید key یا پراپرتی unique باشد؛ کلید و ستون generated به‌روزرسانی نمی‌شوند.

## Query نوع‌امن

`Table<T>()` فقط state می‌سازد و در terminal method اجرا می‌شود:

```csharp
var users = db.Table<User>()
    .Where(user => user.IsActive && user.Role != UserRole.Disabled)
    .Where(user => user.Email.Contains("@example.com"))
    .OrderBy(user => user.DisplayName)
    .ThenByDescending(user => user.CreatedAt)
    .Skip(20)
    .Take(20)
    .ToList();
```

Terminal methodها عبارت‌اند از `ToList`، `First`، `FirstOrDefault`، `Single`، `SingleOrDefault`، `Any` و `Count`. متد `First` در SQL از `LIMIT 1`، متد `Any` از query وجود و `Count` از `COUNT(*)` استفاده می‌کند. مقدار منفی `Skip` یا `Take` رد می‌شود. `ThenBy` به ordering قبلی نیاز دارد. چند `Where` با `AND` ترکیب می‌شوند.

```csharp
User? user = db.FirstOrDefault<User>(x => x.Email == email);
bool exists = db.Any<User>(x => x.Email == email);
int count = db.Count<User>(x => x.IsActive);
User? byKey = db.Find<User, long>(42L);
```

### عبارت‌های پشتیبانی‌شده

- `==`، `!=`، `>`، `>=`، `<` و `<=`
- `&&`، `||` و `!`
- مقایسه‌های `null`
- پراپرتی bool نگاشت‌شده
- `string.Contains`، `StartsWith` و `EndsWith`
- `Contains` روی collection ثابت یا captureشده به‌شکل `IN`
- constant، captured field، nullable، enum و گروه‌بندی

همهٔ مقدارهای runtime به پارامتر SQLite تبدیل می‌شوند و wildcardهای LIKE escape می‌شوند. محاسبات ریاضی، navigation chain، selector محاسباتی، method call دلخواه، overloadهای string comparison و ارزیابی دلخواه expression پشتیبانی نمی‌شوند و `NotSupportedException` می‌دهند. در این موارد از SQL خام پارامتری استفاده کنید.

## Update و Delete

```csharp
user.DisplayName = "Ada Lovelace";
db.Update(user);
```

برای تغییر بخشی از ستون‌ها بدون خواندن entity:

```csharp
int affected = db.Update<User>()
    .Set(x => x.IsActive, false)
    .Set(x => x.LastLogin, null)
    .Where(x => x.LastLogin < cutoff)
    .Execute();
```

Partial update حداقل یک `Set` و یک `Where` می‌خواهد، همه مقدارها را پارامتری می‌کند، پراپرتی ignored یا database-generated را رد می‌کند و تعداد ردیف‌های تغییرکرده را برمی‌گرداند. full-table update ضمنی وجود ندارد.

```csharp
int deleted = db.Delete<User>(x => !x.IsActive && x.LastLogin < cutoff);
int allDeleted = db.DeleteAll<User>(); // کاملاً صریح
db.Delete<User, long>(userId);
```

Predicate delete همیشه predicate غیر-null لازم دارد. `DeleteAll<T>()` مسیر صریح حذف همه ردیف‌ها است.

## INNER JOIN نوع‌امن

```csharp
var results = db.Table<Order>()
    .Join<Customer>(order => order.CustomerId, customer => customer.CustomerId)
    .Where((order, customer) => customer.IsActive)
    .Select((order, customer) => new OrderSummary
    {
        OrderId = order.OrderId,
        CustomerName = customer.Name
    })
    .ToList();
```

نام جدول و ستون از metadata و مقدار فیلتر از پارامتر می‌آید. API فعلی یک `INNER JOIN`، فیلتر روی دو مدل و projection با member initializer را پشتیبانی می‌کند. LEFT JOIN، زنجیرهٔ joinها، aggregate روی join و projection دلخواه پیاده‌سازی نشده‌اند.

## Transaction

```csharp
db.Transaction(tx =>
{
    tx.Insert(order);
    tx.Update(customer);
    tx.Execute("INSERT INTO Audit (Message) VALUES (@message)", new { message = "created" });
    var count = tx.ExecuteScalar<int>("SELECT COUNT(*) FROM Orders");
});
```

callback از یک connection و یک SQLite transaction استفاده می‌کند. commit فقط پس از موفقیت callback انجام می‌شود. در صورت خطا rollback تلاش می‌شود و exception اصلی دوباره پرتاب می‌شود. session پس از پایان callback نامعتبر است. nested transaction صریحاً رد می‌شود و bulk operation داخل transaction همان transaction فعال را استفاده می‌کند.

## SQL خام

SQL خام راه فرار برای قابلیت‌های خارج از DSL است، نه جایگزین query نوع‌امن:

```csharp
var users = db.Query<User>(
    "SELECT * FROM Users WHERE IsActive = @active AND CreatedAt >= @since",
    new { active = true, since });

db.ExecuteNonQuery(
    "UPDATE Users SET DisplayName = @name WHERE UserId = @id",
    new { name, id });

int total = db.ExecuteScalar<int>("SELECT COUNT(*) FROM Users");
```

metadata پراپرتی‌های anonymous object cache می‌شود. برای مجموعه پارامتر پویا `Dictionary<string, object>` همچنان پشتیبانی می‌شود. نام می‌تواند با یا بدون `@` باشد؛ نام خالی، نامعتبر یا تکراری پس از normalize رد می‌شود. `null`، enum و typeهای نگاشت‌شده از converter مرکزی عبور می‌کنند. مقدارها به `SQLiteCommand` داده می‌شوند و هرگز داخل SQL جایگزین یا interpolate نمی‌شوند. امنیت identifierها و ساختار SQL خام بر عهدهٔ فراخواننده است.

ستون نتیجه بدون حساسیت به بزرگی حروف روی پراپرتی writable نگاشت می‌شود. `ExecuteScalar<T>` برای SQL `NULL` مقدار `default` برمی‌گرداند.

## Thread، concurrency و رفتار SQLite

- عملیات عادی یک ORM instance connection مشترک نگه نمی‌دارند؛ readها connection مستقل دارند.
- writeها روی یک instance با lock داخلی سری می‌شوند. instanceهای مختلف با این lock هماهنگ نیستند و به locking خود SQLite، busy timeout و WAL متکی‌اند.
- transaction در زمان callback write lock را نگه می‌دارد و از context مبتنی بر `AsyncLocal` استفاده می‌کند. API عمومی همگام است و async API وجود ندارد.
- هم‌زمان با استفادهٔ سایر callerها instance را Dispose نکنید؛ رقابت Dispose و استفاده پشتیبانی نمی‌شود.
- SQLite همچنان فقط یک writer هم‌زمان دارد. WAL هم‌زیستی reader/writer را بهتر می‌کند اما contention نوشتن را حذف نمی‌کند.
- دیتابیس in-memory در SQLite به connection وابسته است؛ چون عملیات عادی connection جدا باز می‌کنند، برای workflow چندعملیاتی از فایل استفاده کنید.
- فعلاً logging، interceptor، hook یا extension point عمومی برای custom converter وجود ندارد.

## خطاها و APIهای سازگاری

selector نامعتبر و expression پشتیبانی‌نشده زود با `ArgumentException`، `InvalidOperationException` یا `NotSupportedException` متوقف می‌شود. خطای provider به‌شکل `SQLiteException` با جزئیات اصلی باقی می‌ماند. ORM هیچ predicate پشتیبانی‌نشده‌ای را بی‌صدا evaluate نمی‌کند و مقدار را وارد متن SQL نمی‌کند.

API canonical شامل `SqliteOrm` instance-based، `Table<T>()`، join نوع‌امن، predicate delete، partial update و anonymous-object parameter است. موارد زیر فقط برای source compatibility باقی مانده‌اند و برای کد جدید توصیه نمی‌شوند:

- `SqLiteOrm.Initialize/Instance`
- `GetAll` و APIهای dictionary-based برای condition، ordering و relation
- relation APIهای string/tuple
- `FindOneByKey`، `Exists` و `Delete` مبتنی بر property/value
- ترتیب آرگومان قدیمی Upsert

این wrapperها تا حد ممکن به metadata و command infrastructure فعلی هدایت می‌شوند. حذف یا تغییر نام آن‌ها، capitalization فعلی namespace/package یعنی `SQliteOrm` و convention تولید خودکار کلید عددی `Id` به major version آینده نیاز دارد.

## خلاصهٔ محدودیت‌ها

SQliteOrm همگام و ویژهٔ SQLite است. `IQueryable<T>`، change tracking، lazy loading، navigation loading، composite key، async API، distributed migration lock، projection دلخواه query تک‌جدولی، custom converter و migration engine کامل برای بازسازی جدول را پیاده‌سازی نمی‌کند. برای عملیات پشتیبانی‌شده API نوع‌امن و برای سایر موارد SQL خام پارامتری را به‌کار ببرید.

## تست‌ها

مجموعه تست‌ها metadata، schema، typeهای CLR، CRUD، native upsert، ترجمه predicate و مقاومت در برابر injection، ordering و paging، partial update، join، پارامترهای raw SQL، instanceهای مستقل، transaction و rollback، bulk operation و migration را پوشش می‌دهد.

## مجوز

MIT؛ فایل [LICENSE](LICENSE) را ببینید.
