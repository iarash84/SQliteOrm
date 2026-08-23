using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Data.SQLite;
using SQliteOrm;
using SQliteOrm.Mapping;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SQliteOrm.Tests;

public sealed class SqLiteOrmTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqLiteOrm _orm;

    public SqLiteOrmTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sqlite-orm-tests-{Guid.NewGuid():N}.db");
        SqLiteOrm.Initialize(_databasePath);
        _orm = SqLiteOrm.Instance;
        _orm.CreateTable<Person>();
        _orm.CreateTable<Customer>();
        _orm.CreateTable<Purchase>();
    }

    [Fact]
    public void Initialize_rejects_an_empty_database_path()
    {
        Assert.Throws<ArgumentException>(() => SqLiteOrm.Initialize(" "));
    }

    [Fact]
    public void CreateTable_creates_constraints_and_ignores_not_mapped_properties()
    {
        var columns = _orm.Query<ColumnInfo>("PRAGMA table_info(\"Person\");");
        var indexes = _orm.Query<IndexInfo>("PRAGMA index_list(\"Person\");");

        Assert.Contains(columns, c => c.name == nameof(Person.Id) && c.pk == 1);
        Assert.Contains(columns, c => c.name == nameof(Person.Name) && c.notnull == 1);
        Assert.DoesNotContain(columns, c => c.name == nameof(Person.TransientValue));
        Assert.Contains(indexes, i => i.unique == 1);

        var foreignKeys = _orm.Query<ForeignKeyInfo>("PRAGMA foreign_key_list(\"Purchase\");");
        Assert.Contains(foreignKeys, fk => fk.table == nameof(Customer) && fk.on_delete == "CASCADE" && fk.on_update == "RESTRICT");
    }

    [Fact]
    public void Insert_query_find_count_exists_update_and_delete_work_end_to_end()
    {
        var ada = NewPerson("Ada", 31, true);
        var id = _orm.Insert(ada);

        Assert.True(id > 0);
        Assert.Equal(id, ada.Id);
        Assert.True(_orm.Exists<Person>(id));
        Assert.True(_orm.Exists<Person>(p => p.Name, "Ada"));
        Assert.Equal(1, _orm.Count<Person>());
        Assert.Equal(1, _orm.Count<Person>(new() { [p => p.Name] = "Ada" }));

        var found = _orm.FindById<Person>(id);
        Assert.NotNull(found);
        Assert.Equal("Ada", found.Name);
        Assert.True(found.Active);
        Assert.Equal(PersonKind.Admin, found.Kind);

        ada.Age = 32;
        _orm.Update(ada);
        Assert.Equal(32, _orm.FindOneByKey<Person>(p => p.Id, id.ToString())!.Age);

        _orm.Delete<Person>(p => p.Name, "Ada");
        Assert.False(_orm.Exists<Person>(id));
        _orm.Delete<Person>(id); // default-key overload is safe when the row is already gone
    }

    [Fact]
    public void Insert_list_is_transactional_and_rejects_null_items()
    {
        _orm.Insert(new List<Person> { NewPerson("A", 1), NewPerson("B", 2) });
        Assert.Equal(2, _orm.Count<Person>());

        Assert.Throws<ArgumentException>(() => _orm.Insert(new List<Person> { NewPerson("C", 3), null! }));
        Assert.Equal(2, _orm.Count<Person>());
        _orm.Insert(new List<Person>());
    }

    [Fact]
    public void Upsert_inserts_then_updates_using_the_selected_property()
    {
        var person = NewPerson("Unique", 10);
        _orm.Upsert<Person>(p => p.Name, person);
        person.Age = 11;
        _orm.Upsert<Person>(p => p.Name, person);

        Assert.Equal(1, _orm.Count<Person>());
        Assert.Equal(11, _orm.FindOneByKey<Person>(p => p.Name, "Unique")!.Age);
    }

    [Fact]
    public void Find_and_count_support_multiple_conditions_and_or_operator()
    {
        _orm.Insert(new List<Person>
        {
            NewPerson("Ada", 30),
            NewPerson("Grace", 30),
            NewPerson("Linus", 40)
        });

        var person = _orm.FindOneByKey<Person>(new()
        {
            [p => p.Name] = "Ada",
            [p => p.Age] = 30
        });
        var count = _orm.Count<Person>(new()
        {
            [p => p.Name] = "Ada",
            [p => p.Age] = 40
        }, LogicalOperator.Or);

        Assert.NotNull(person);
        Assert.Equal("Ada", person.Name);
        Assert.Equal(2, count);
    }

    [Fact]
    public void GetAll_supports_conditions_nulls_ordering_limit_and_offset()
    {
        var first = NewPerson("First", 10); first.Nickname = null;
        var second = NewPerson("Second", 20); second.Nickname = "two";
        var third = NewPerson("Third", 30); third.Nickname = null;
        _orm.Insert(new List<Person> { first, second, third });

        var nullNicknames = _orm.GetAll<Person>(new() { [p => p.Nickname!] = null! });
        var paged = _orm.GetAll<Person>(
            new() { [p => p.Age] = 10, [p => p.Name] = "Third" }, LogicalOperator.Or, 1, 1,
            new() { [p => p.Age] = SortOrder.ASC });

        Assert.Equal(2, nullNicknames.Count);
        Assert.Single(paged);
        Assert.Equal("Third", paged[0].Name);
    }

    [Fact]
    public void GetAll_supports_descending_order()
    {
        _orm.Insert(new List<Person>
        {
            NewPerson("Low", 10),
            NewPerson("Middle", 20),
            NewPerson("High", 30)
        });

        var result = _orm.GetAll<Person>(
            orderBy: new() { [p => p.Age] = SortOrder.DESC });

        Assert.Equal(new[] { "High", "Middle", "Low" }, result.Select(p => p.Name));
    }

    [Fact]
    public void Relation_queries_return_main_entities_and_accept_filters()
    {
        var customerId = _orm.Insert(new Customer { Name = "Contoso" });
        _orm.Insert(new Purchase { CustomerId = customerId, Description = "Keyboard" });

        var singleRelation = _orm.GetAllWithRelation<Purchase, Customer>(
            nameof(Purchase.CustomerId), nameof(Customer.Name), "CustomerName",
            new() { [p => p.Description] = "Keyboard" });
        var dictionaryRelations = _orm.GetAllWithRelations<Purchase>(
            "p", new Dictionary<string, (string, string, string)> { ["c"] = (nameof(Purchase.CustomerId), nameof(Customer), "p") },
            new List<(string, string, string)> { ("c", nameof(Customer.Name), "CustomerName") },
            new Dictionary<string, object> { [nameof(Purchase.Description)] = "Keyboard" });
        var expressionRelations = _orm.GetAllWithRelations<Purchase>(
            "p", new List<(Expression<Func<Purchase, object>>, string, string)> { (p => p.CustomerId, nameof(Customer), "c") },
            new List<(string, Expression<Func<Purchase, object>>, string)> { ("p", p => p.Description, "DescriptionAgain") },
            new Dictionary<Expression<Func<Purchase, object>>, object> { [p => p.Description] = "Keyboard" });

        _orm.Insert(new Purchase { CustomerId = customerId, Description = null! });
        var nullDescriptionRelations = _orm.GetAllWithRelations<Purchase>(
            "p", new List<(Expression<Func<Purchase, object>>, string, string)> { (p => p.CustomerId, nameof(Customer), "c") },
            conditions: new() { [p => p.Description] = null! });

        Assert.Single(singleRelation);
        Assert.Single(dictionaryRelations);
        Assert.Single(expressionRelations);
        Assert.Single(nullDescriptionRelations);
    }

    [Fact]
    public void Foreign_key_cascade_deletes_related_records()
    {
        var customerId = _orm.Insert(new Customer { Name = "Cascade customer" });
        _orm.Insert(new Purchase { CustomerId = customerId, Description = "Cascade purchase" });

        _orm.Delete<Customer>(customerId);

        Assert.Equal(0, _orm.Count<Purchase>());
    }

    [Fact]
    public void Raw_query_scalar_and_non_query_support_parameters_and_mapping()
    {
        _orm.ExecuteNonQuery("INSERT INTO \"Person\" (\"Name\", \"Age\", \"Active\", \"Score\", \"Kind\") VALUES (@name, @age, @active, @score, @kind)",
            new() { ["@name"] = "Raw", ["@age"] = 9, ["@active"] = true, ["@score"] = 4.5, ["@kind"] = 1 });

        var rows = _orm.Query<Person>("SELECT * FROM \"Person\" WHERE \"Name\" = @name", new() { ["@name"] = "Raw" });
        Assert.Single(rows);
        Assert.True(rows[0].Active);
        Assert.Equal(PersonKind.Admin, rows[0].Kind);
        Assert.Equal(1, _orm.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Person\""));
        Assert.Null(_orm.ExecuteScalar<string>("SELECT NULL"));

        var token = Guid.NewGuid();
        var guidResult = _orm.Query<GuidProjection>("SELECT @token AS \"Token\"", new() { ["@token"] = token.ToString() });
        Assert.Equal(token, Assert.Single(guidResult).Token);
    }

    [Fact]
    public void Database_values_are_mapped_to_nullable_and_date_time_properties()
    {
        var createdAt = new DateTime(2026, 8, 9, 12, 30, 0, DateTimeKind.Utc);
        _orm.ExecuteNonQuery(
            "INSERT INTO \"Person\" (\"Name\", \"Age\", \"Active\", \"Score\", \"Kind\", \"Nickname\") VALUES (@name, @age, @active, @score, @kind, @nickname)",
            new()
            {
                ["@name"] = "Nullable",
                ["@age"] = 1,
                ["@active"] = 0,
                ["@score"] = 0.5,
                ["@kind"] = "User",
                ["@nickname"] = DBNull.Value
            });

        var result = _orm.Query<NullableProjection>(
            "SELECT \"Nickname\" AS \"Nickname\", @createdAt AS \"CreatedAt\" FROM \"Person\" WHERE \"Name\" = @name",
            new() { ["@createdAt"] = createdAt.ToString("O"), ["@name"] = "Nullable" });

        var row = Assert.Single(result);
        Assert.Null(row.Nickname);
        Assert.Equal(createdAt, row.CreatedAt.ToUniversalTime());
    }

    [Fact]
    public void Unique_constraint_prevents_duplicate_values()
    {
        _orm.Insert(NewPerson("Unique name", 1));

        Assert.Throws<SQLiteException>(() => _orm.Insert(NewPerson("Unique name", 2)));
        Assert.Equal(1, _orm.Count<Person>());
    }

    [Fact]
    public void GetAll_rejects_an_undefined_logical_operator() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _orm.GetAll<Person>(conditionType: (LogicalOperator)99));

    [Fact]
    public void Public_methods_validate_invalid_input()
    {
        Assert.Throws<ArgumentNullException>(() => _orm.Insert((Person)null!));
        Assert.Throws<ArgumentException>(() => _orm.Query<Person>(""));
        Assert.Throws<ArgumentException>(() => _orm.ExecuteScalar<int>(""));
        Assert.Throws<ArgumentException>(() => _orm.ExecuteNonQuery(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => _orm.GetAll<Person>(limit: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _orm.GetAll<Person>(offset: -1));
        Assert.Throws<InvalidOperationException>(() => _orm.CreateTable<NoProperties>());
        Assert.Throws<ArgumentException>(() => _orm.CreateTable<InvalidForeignKey>());
        Assert.Throws<InvalidOperationException>(() => _orm.GetAll<Person>(new() { [p => p.Age + 1] = 2 }));
        Assert.Throws<ArgumentException>(() => _orm.Delete<Person>(p => p.Age + 1, "1"));
        Assert.Throws<ArgumentNullException>(() => _orm.Upsert<Person>(p => p.Id, NewPerson("invalid", 1)));
    }

    [Fact]
    public void Count_and_key_lookups_support_models_without_an_Id_property()
    {
        _orm.CreateTable<NaturalKeyRecord>();
        var createdAt = new DateTime(2026, 8, 10, 1, 2, 3, DateTimeKind.Utc);
        _orm.Insert(new NaturalKeyRecord { CreatedAt = createdAt, Value = "first" });

        Assert.Equal(1, _orm.Count<NaturalKeyRecord>());
        Assert.True(_orm.Exists<NaturalKeyRecord>(x => x.CreatedAt, createdAt));
        Assert.Equal("first", _orm.FindOneByKey<NaturalKeyRecord>(x => x.CreatedAt, createdAt)!.Value);
    }

    [Fact]
    public void CreateTable_allows_a_non_integer_manual_key()
    {
        _orm.CreateTable<InvalidKeyType>();
        var key = Guid.NewGuid();
        _orm.Insert(new InvalidKeyType { Id = key });
        Assert.Equal(key, _orm.Find<InvalidKeyType, Guid>(key)!.Id);
    }

    [Fact]
    public void Primary_key_operations_are_metadata_driven()
    {
        _orm.CreateTable<NamedLongKey>();
        _orm.CreateTable<GuidKeyRecord>();
        _orm.CreateTable<ManualIntKey>();
        _orm.CreateTable<ManualIntKeyChild>();

        var generated = new NamedLongKey { Value = "before" };
        _orm.Insert(generated);
        Assert.True(generated.UserId > 0);
        Assert.Contains("AUTOINCREMENT", GetTableSql(nameof(NamedLongKey)));

        generated.Value = "after";
        _orm.Update(generated);
        Assert.Equal("after", _orm.Find<NamedLongKey, long>(generated.UserId)!.Value);
        _orm.Delete<NamedLongKey, long>(generated.UserId);
        Assert.Null(_orm.Find<NamedLongKey, long>(generated.UserId));

        var guid = Guid.NewGuid();
        _orm.Insert(new GuidKeyRecord { UserKey = guid, Value = "guid" });
        Assert.Equal("guid", _orm.Find<GuidKeyRecord, Guid>(guid)!.Value);
        Assert.DoesNotContain("AUTOINCREMENT", GetTableSql(nameof(GuidKeyRecord)));

        _orm.Insert(new ManualIntKey { Code = 42, Value = "manual" });
        Assert.Equal("manual", _orm.Find<ManualIntKey, int>(42)!.Value);
        Assert.DoesNotContain("AUTOINCREMENT", GetTableSql(nameof(ManualIntKey)));
        Assert.Contains(_orm.Query<ForeignKeyInfo>($"PRAGMA foreign_key_list(\"{nameof(ManualIntKeyChild)}\");"),
            foreignKey => foreignKey.table == nameof(ManualIntKey) && foreignKey.to == nameof(ManualIntKey.Code));

        var generatedBatch = new List<NamedLongKey> { new() { Value = "one" }, new() { Value = "two" } };
        _orm.Insert(generatedBatch);
        Assert.All(generatedBatch, item => Assert.True(item.UserId > 0));
        Assert.NotEqual(generatedBatch[0].UserId, generatedBatch[1].UserId);
    }

    private string GetTableSql(string tableName) => _orm.Query<TableSql>(
        "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name",
        new() { ["@name"] = tableName }).Single().sql;

    [Fact]
    public void Entity_metadata_is_cached_and_resolves_mapping_rules()
    {
        var first = EntityMapCache.Get<MappedRecord>();
        var second = EntityMapCache.Get<MappedRecord>();

        Assert.Same(first, second);
        Assert.Equal("mapped_records", first.TableName);
        Assert.Equal(nameof(MappedRecord.Id), first.Key!.PropertyName);
        Assert.True(first.Key.IsPrimaryKey);
        Assert.True(first.Key.IsAutoGenerated);
        Assert.Equal("display_name", first.GetProperty(nameof(MappedRecord.Name)).ColumnName);
        Assert.Equal(typeof(string), first.GetProperty(nameof(MappedRecord.Name)).ClrType);
        Assert.Equal("TEXT", first.GetProperty(nameof(MappedRecord.Name)).SqliteType);
        Assert.True(first.GetProperty(nameof(MappedRecord.Nickname)).IsNullable);
        Assert.False(first.GetProperty(nameof(MappedRecord.Name)).IsNullable);
        Assert.True(first.GetProperty(nameof(MappedRecord.Name)).IsRequired);
        Assert.DoesNotContain(first.Properties, p => p.PropertyName == nameof(MappedRecord.Ignored));
    }

    [Fact]
    public void Supported_types_generate_correct_affinities_and_round_trip()
    {
        _orm.CreateTable<SupportedTypes>();
        var columns = _orm.Query<ColumnInfo>($"PRAGMA table_info(\"{nameof(SupportedTypes)}\");")
            .ToDictionary(column => column.name, column => column.type);

        foreach (var name in new[] { nameof(SupportedTypes.Byte), nameof(SupportedTypes.Short), nameof(SupportedTypes.Int),
                     nameof(SupportedTypes.Long), nameof(SupportedTypes.Bool), nameof(SupportedTypes.Enum),
                     nameof(SupportedTypes.NullableByte), nameof(SupportedTypes.NullableShort), nameof(SupportedTypes.NullableInt),
                     nameof(SupportedTypes.NullableLong), nameof(SupportedTypes.NullableEnum) })
            Assert.Equal("INTEGER", columns[name]);
        Assert.Equal("REAL", columns[nameof(SupportedTypes.Float)]);
        Assert.Equal("REAL", columns[nameof(SupportedTypes.NullableDouble)]);
        Assert.Equal("NUMERIC", columns[nameof(SupportedTypes.Decimal)]);
        Assert.Equal("NUMERIC", columns[nameof(SupportedTypes.NullableDecimal)]);
        Assert.Equal("BLOB", columns[nameof(SupportedTypes.Bytes)]);
        Assert.Equal("TEXT", columns[nameof(SupportedTypes.Guid)]);
        Assert.Equal("TEXT", columns[nameof(SupportedTypes.NullableDateTimeOffset)]);

        var timestamp = new DateTime(2026, 8, 23, 10, 11, 12, DateTimeKind.Utc);
        var offset = new DateTimeOffset(2026, 8, 23, 10, 11, 12, TimeSpan.FromHours(3.5));
        var guid = Guid.NewGuid();
        var value = new SupportedTypes
        {
            Byte = 200, Short = -1234, Int = -123456, Long = 9_000_000_000,
            Float = 1.25f, Double = 2.5, Decimal = 12345.6789m, Bool = true,
            String = "text", Bytes = new byte[] { 0, 1, 127, 255 }, DateTime = timestamp,
            DateTimeOffset = offset, DateOnly = new DateOnly(2026, 8, 23), TimeOnly = new TimeOnly(10, 11, 12),
            Guid = guid, Enum = PersonKind.Admin, NullableByte = 8, NullableShort = 9, NullableInt = 7,
            NullableLong = 10, NullableFloat = 1.5f, NullableDouble = 8.5,
            NullableDecimal = 9.75m, NullableBool = false, NullableDateTime = timestamp,
            NullableDateTimeOffset = offset, NullableDateOnly = new DateOnly(2026, 1, 2),
            NullableTimeOnly = new TimeOnly(3, 4, 5), NullableGuid = guid, NullableEnum = PersonKind.User
        };
        _orm.Insert(value);
        var actual = _orm.Find<SupportedTypes, long>(value.RowKey)!;

        Assert.Equal(value.Byte, actual.Byte); Assert.Equal(value.Short, actual.Short);
        Assert.Equal(value.Int, actual.Int); Assert.Equal(value.Long, actual.Long);
        Assert.Equal(value.Float, actual.Float); Assert.Equal(value.Double, actual.Double);
        Assert.Equal(value.Decimal, actual.Decimal); Assert.Equal(value.Bool, actual.Bool);
        Assert.Equal(value.String, actual.String); Assert.Equal(value.Bytes, actual.Bytes);
        Assert.Equal(value.DateTime, actual.DateTime); Assert.Equal(value.DateTimeOffset, actual.DateTimeOffset);
        Assert.Equal(value.DateOnly, actual.DateOnly); Assert.Equal(value.TimeOnly, actual.TimeOnly);
        Assert.Equal(value.Guid, actual.Guid); Assert.Equal(value.Enum, actual.Enum);
        Assert.Equal(value.NullableGuid, actual.NullableGuid); Assert.Equal(value.NullableEnum, actual.NullableEnum);

        var nulls = new SupportedTypes();
        _orm.Insert(nulls);
        var nullResult = _orm.Find<SupportedTypes, long>(nulls.RowKey)!;
        Assert.Null(nullResult.NullableByte); Assert.Null(nullResult.NullableShort);
        Assert.Null(nullResult.NullableInt); Assert.Null(nullResult.NullableLong);
        Assert.Null(nullResult.NullableFloat); Assert.Null(nullResult.NullableDouble);
        Assert.Null(nullResult.NullableDecimal); Assert.Null(nullResult.NullableBool);
        Assert.Null(nullResult.NullableDateTime); Assert.Null(nullResult.NullableDateTimeOffset);
        Assert.Null(nullResult.NullableDateOnly); Assert.Null(nullResult.NullableTimeOnly);
        Assert.Null(nullResult.NullableGuid); Assert.Null(nullResult.NullableEnum);

        Assert.Equal(guid, _orm.ExecuteScalar<Guid>("SELECT @value", new() { ["@value"] = guid }));
        Assert.True(_orm.Exists<SupportedTypes>(item => item.Enum, PersonKind.Admin));
    }

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    private static Person NewPerson(string name, int age, bool active = false) => new()
    {
        Name = name, Age = age, Active = active, Score = 1.5, Kind = PersonKind.Admin
    };

    private sealed class Person
    {
        [Key] public int Id { get; set; }
        [Required, Unique] public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool Active { get; set; }
        public double Score { get; set; }
        public PersonKind Kind { get; set; }
        public string? Nickname { get; set; }
        [NotMapped] public string? TransientValue { get; set; }
    }

    private sealed class Customer { [Key] public int Id { get; set; } public string Name { get; set; } = string.Empty; }
    private sealed class Purchase
    {
        [Key] public int Id { get; set; }
        [ForeignKey(nameof(Customer), OnDelete = "CASCADE", OnUpdate = "RESTRICT")] public int CustomerId { get; set; }
        public string Description { get; set; } = string.Empty;
    }
    private sealed class NoProperties { }
    private sealed class InvalidForeignKey { [Key] public int Id { get; set; } [ForeignKey("Person", OnDelete = "DROP")] public int PersonId { get; set; } }
    private sealed class ColumnInfo { public string name { get; set; } = string.Empty; public string type { get; set; } = string.Empty; public int notnull { get; set; } public int pk { get; set; } }
    private sealed class IndexInfo { public int unique { get; set; } }
    private sealed class ForeignKeyInfo { public string table { get; set; } = string.Empty; public string to { get; set; } = string.Empty; public string on_delete { get; set; } = string.Empty; public string on_update { get; set; } = string.Empty; }
    private sealed class GuidProjection { public Guid Token { get; set; } }
    private sealed class NullableProjection { public string? Nickname { get; set; } public DateTime CreatedAt { get; set; } }
    private sealed class NaturalKeyRecord { public DateTime CreatedAt { get; set; } public string Value { get; set; } = string.Empty; }
    private sealed class InvalidKeyType { [Key] public Guid Id { get; set; } }
    private sealed class NamedLongKey
    {
        [Key, AutoIncrement] public long UserId { get; set; }
        public string Value { get; set; } = string.Empty;
    }
    private sealed class GuidKeyRecord
    {
        [Key] public Guid UserKey { get; set; }
        public string Value { get; set; } = string.Empty;
    }
    private sealed class ManualIntKey
    {
        [Key] public int Code { get; set; }
        public string Value { get; set; } = string.Empty;
    }
    private sealed class ManualIntKeyChild
    {
        [Key, AutoIncrement] public long ChildKey { get; set; }
        [ForeignKey(nameof(ManualIntKey))] public int ParentCode { get; set; }
    }
    private sealed class TableSql { public string sql { get; set; } = string.Empty; }
    private sealed class SupportedTypes
    {
        [Key, AutoIncrement] public long RowKey { get; set; }
        public byte Byte { get; set; }
        public short Short { get; set; }
        public int Int { get; set; }
        public long Long { get; set; }
        public float Float { get; set; }
        public double Double { get; set; }
        public decimal Decimal { get; set; }
        public bool Bool { get; set; }
        public string? String { get; set; }
        public byte[]? Bytes { get; set; }
        public DateTime DateTime { get; set; }
        public DateTimeOffset DateTimeOffset { get; set; }
        public DateOnly DateOnly { get; set; }
        public TimeOnly TimeOnly { get; set; }
        public Guid Guid { get; set; }
        public PersonKind Enum { get; set; }
        public byte? NullableByte { get; set; }
        public short? NullableShort { get; set; }
        public int? NullableInt { get; set; }
        public long? NullableLong { get; set; }
        public float? NullableFloat { get; set; }
        public double? NullableDouble { get; set; }
        public decimal? NullableDecimal { get; set; }
        public bool? NullableBool { get; set; }
        public DateTime? NullableDateTime { get; set; }
        public DateTimeOffset? NullableDateTimeOffset { get; set; }
        public DateOnly? NullableDateOnly { get; set; }
        public TimeOnly? NullableTimeOnly { get; set; }
        public Guid? NullableGuid { get; set; }
        public PersonKind? NullableEnum { get; set; }
    }
    [Table("mapped_records")]
    private sealed class MappedRecord
    {
        [Key] public int Id { get; set; }
        [Required, Column("display_name")] public string Name { get; set; } = string.Empty;
        public string? Nickname { get; set; }
        [NotMapped] public string? Ignored { get; set; }
    }
    private enum PersonKind { User, Admin }
}
