using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Data.SQLite;
using SQliteOrm;
using SQliteOrm.Mapping;
using SQliteOrm.Persistence;

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
    public void Strongly_typed_query_executes_terminal_operations()
    {
        _orm.Insert(new List<Person>
        {
            NewPerson("Minor", 17, true),
            NewPerson("Adult", 20, true),
            NewPerson("Inactive", 30, false)
        });

        var adults = _orm.Table<Person>()
            .Where(person => person.Active)
            .Where(person => person.Age >= 18);

        Assert.Equal("Adult", Assert.Single(adults.ToList()).Name);
        Assert.Equal("Adult", adults.First().Name);
        Assert.Equal("Adult", adults.FirstOrDefault()!.Name);
        Assert.Equal("Adult", adults.Single().Name);
        Assert.Equal("Adult", adults.SingleOrDefault()!.Name);
        Assert.True(adults.Any());
        Assert.Equal(1, adults.Count());
        Assert.True(_orm.Any<Person>(person => person.Name == "Adult"));
        Assert.Equal(2, _orm.Count<Person>(person => person.Active));
        Assert.Equal("Adult", _orm.FirstOrDefault<Person>(person => person.Age == 20)!.Name);
    }

    [Fact]
    public void Strongly_typed_query_has_correct_terminal_semantics_and_is_deferred()
    {
        var deferred = _orm.Table<Person>().Where(person => person.Age + 1 > 18);
        Assert.Throws<NotSupportedException>(() => deferred.ToList());

        Assert.Null(_orm.Table<Person>().Where(person => person.Name == "missing").FirstOrDefault());
        Assert.Null(_orm.Table<Person>().Where(person => person.Name == "missing").SingleOrDefault());
        Assert.Throws<InvalidOperationException>(() => _orm.Table<Person>().First());

        _orm.Insert(new List<Person> { NewPerson("One", 1), NewPerson("Two", 2) });
        Assert.Throws<InvalidOperationException>(() => _orm.Table<Person>().Single());
        Assert.Throws<InvalidOperationException>(() => _orm.Table<Person>().SingleOrDefault());

        var command = _orm.Table<Person>().Where(person => person.Name == "Robert'); DROP TABLE Person;--").BuildSelect(1);
        Assert.EndsWith(" LIMIT @__limit;", command.Sql);
        Assert.DoesNotContain("DROP TABLE", command.Sql);
        Assert.Equal(2, command.Parameters!.Count);
        Assert.Equal(1, command.Parameters["@__limit"]);
    }

    [Fact]
    public void Strongly_typed_query_supports_ordering_and_then_by()
    {
        _orm.Insert(new List<Person>
        {
            NewPerson("Charlie", 20), NewPerson("Alpha", 20), NewPerson("Bravo", 10)
        });

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" },
            _orm.Table<Person>().OrderBy(person => person.Name).ToList().Select(person => person.Name));
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" },
            _orm.Table<Person>().OrderByDescending(person => person.Name).ToList().Select(person => person.Name));
        Assert.Equal(new[] { "Bravo", "Alpha", "Charlie" },
            _orm.Table<Person>().OrderBy(person => person.Age).ThenBy(person => person.Name).ToList().Select(person => person.Name));
        Assert.Equal(new[] { "Bravo", "Charlie", "Alpha" },
            _orm.Table<Person>().OrderBy(person => person.Age).ThenByDescending(person => person.Name).ToList().Select(person => person.Name));

        Assert.Throws<InvalidOperationException>(() => _orm.Table<Person>().ThenBy(person => person.Name));
        Assert.Throws<NotSupportedException>(() => _orm.Table<Person>().OrderBy(person => person.Age + 1));

        var mappedCommand = _orm.Table<MappedRecord>().OrderBy(record => record.Name).BuildSelect();
        Assert.Contains("ORDER BY \"display_name\" ASC", mappedCommand.Sql);
    }

    [Fact]
    public void Strongly_typed_query_supports_skip_take_and_filtered_paging()
    {
        _orm.Insert(new List<Person>
        {
            NewPerson("A", 10, true), NewPerson("B", 20, false), NewPerson("C", 30, true),
            NewPerson("D", 40, true), NewPerson("E", 50, false)
        });

        Assert.Equal(new[] { "C", "D", "E" },
            _orm.Table<Person>().OrderBy(person => person.Age).Skip(2).ToList().Select(person => person.Name));
        Assert.Equal(new[] { "A", "B" },
            _orm.Table<Person>().OrderBy(person => person.Age).Take(2).ToList().Select(person => person.Name));
        Assert.Equal(new[] { "B", "C" },
            _orm.Table<Person>().OrderBy(person => person.Age).Skip(1).Take(2).ToList().Select(person => person.Name));
        Assert.Equal(new[] { "C", "D" }, _orm.Table<Person>()
            .Where(person => person.Active).OrderBy(person => person.Age).Skip(1).Take(2)
            .ToList().Select(person => person.Name));
        Assert.Equal(1, _orm.Table<Person>().OrderBy(person => person.Age).Skip(4).Count());
        Assert.False(_orm.Table<Person>().Take(0).Any());

        Assert.Throws<ArgumentOutOfRangeException>(() => _orm.Table<Person>().Skip(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _orm.Table<Person>().Take(-1));

        var command = _orm.Table<Person>().OrderBy(person => person.Name)
            .ThenByDescending(person => person.Age).Skip(5).Take(10).BuildSelect();
        Assert.Contains("ORDER BY \"Name\" ASC, \"Age\" DESC LIMIT @__limit OFFSET @__offset", command.Sql);
        Assert.Equal(10, command.Parameters!["@__limit"]);
        Assert.Equal(5, command.Parameters["@__offset"]);
    }

    [Fact]
    public void Predicate_delete_supports_nested_conditions_nulls_and_affected_count()
    {
        var youngInactive = NewPerson("Young", 10); youngInactive.Nickname = "known";
        var nullInactive = NewPerson("Null inactive", 30);
        var retainedInactive = NewPerson("Retained", 30); retainedInactive.Nickname = "known";
        var active = NewPerson("Active", 10, true);
        _orm.Insert(new List<Person> { youngInactive, nullInactive, retainedInactive, active });

        var deleted = _orm.Delete<Person>(person =>
            !person.Active && (person.Age < 18 || person.Nickname == null));

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "Active", "Retained" },
            _orm.Table<Person>().OrderBy(person => person.Name).ToList().Select(person => person.Name));
        Assert.Equal(1, _orm.Delete<Person>(person => person.Nickname == null));

        var injection = "Retained' OR 1=1 --";
        Assert.Equal(0, _orm.Delete<Person>(person => person.Name == injection));
        Assert.Equal(1, _orm.Count<Person>());
        Assert.Throws<ArgumentNullException>(() =>
            _orm.Delete<Person>((Expression<Func<Person, bool>>)null!));
        Assert.Equal(1, _orm.DeleteAll<Person>());
        Assert.Equal(0, _orm.Count<Person>());
    }

    [Fact]
    public void Transaction_commits_multiple_entity_types_and_supports_all_session_operations()
    {
        SqliteTransactionSession? capturedSession = null;
        _orm.Transaction(tx =>
        {
            capturedSession = tx;
            var customer = new Customer { Name = "Before" };
            tx.Insert(customer);
            customer.Name = "After";
            tx.Update(customer);
            tx.Insert(new Purchase { CustomerId = customer.Id, Description = "Pending" });
            tx.Execute("UPDATE \"Purchase\" SET \"Description\" = @value",
                new() { ["@value"] = "Committed" });

            Assert.Equal(1, tx.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Customer\""));
            Assert.Equal("After", tx.Table<Customer>().First().Name);
            Assert.Equal("Committed", tx.Query<Purchase>("SELECT * FROM \"Purchase\"").Single().Description);
        });

        Assert.Equal("After", _orm.Table<Customer>().Single().Name);
        Assert.Equal("Committed", _orm.Table<Purchase>().Single().Description);
        Assert.Throws<InvalidOperationException>(() => capturedSession!.Execute("SELECT 1"));
    }

    [Fact]
    public void Transaction_rolls_back_all_changes_and_rethrows_original_exception()
    {
        var expected = new TestTransactionException("rollback");
        var actual = Assert.Throws<TestTransactionException>(() => _orm.Transaction(tx =>
        {
            tx.Insert(NewPerson("Rolled back", 1));
            tx.Insert(new Customer { Name = "Also rolled back" });
            Assert.Equal(1, tx.Table<Person>().Count());
            throw expected;
        }));

        Assert.Same(expected, actual);
        Assert.Equal(0, _orm.Count<Person>());
        Assert.Equal(0, _orm.Count<Customer>());
    }

    [Fact]
    public void Nested_transactions_are_rejected_and_outer_transaction_rolls_back()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => _orm.Transaction(tx =>
        {
            tx.Insert(NewPerson("Outer", 1));
            _orm.Transaction(_ => { });
        }));

        Assert.Contains("Nested transactions", exception.Message);
        Assert.Equal(0, _orm.Count<Person>());
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
        Assert.Throws<InvalidOperationException>(() => _orm.Upsert(NewPerson("invalid", 1), p => p.Age));
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

    [Fact]
    public void Native_upsert_supports_unique_and_primary_key_conflicts()
    {
        var unique = NewPerson("native@example.com", 10);
        _orm.Upsert(unique, conflictOn: person => person.Name);
        Assert.True(unique.Id > 0);
        unique.Age = 11;
        _orm.Upsert(unique, conflictOn: person => person.Name);
        Assert.Equal(1, _orm.Count<Person>());
        Assert.Equal(11, _orm.Find<Person, int>(unique.Id)!.Age);

        var replacement = NewPerson("replacement@example.com", 12);
        replacement.Id = unique.Id;
        _orm.Upsert(replacement, conflictOn: person => person.Id);
        Assert.Equal(1, _orm.Count<Person>());
        Assert.Equal("replacement@example.com", _orm.Find<Person, int>(unique.Id)!.Name);

        var command = UpsertCommandBuilder.Build(replacement, nameof(Person.Name));
        Assert.Contains("ON CONFLICT (\"Name\") DO UPDATE", command.Sql);
        Assert.DoesNotContain("SELECT", command.Sql);
        Assert.DoesNotContain(replacement.Name, command.Sql);
        Assert.Contains(replacement.Name, command.Parameters.Values);
    }

    [Fact]
    public void Native_upsert_supports_non_id_manual_primary_keys_and_nullable_values()
    {
        _orm.CreateTable<ManualIntKey>();
        var entity = new ManualIntKey { Code = 42, Value = "inserted" };
        _orm.Upsert(entity);
        entity.Value = "updated";
        _orm.Upsert(entity);

        Assert.Equal(1, _orm.Count<ManualIntKey>());
        Assert.Equal("updated", _orm.Find<ManualIntKey, int>(42)!.Value);

        _orm.CreateTable<NullableUniqueRecord>();
        _orm.Upsert(new NullableUniqueRecord { Token = null, Value = "first" }, record => record.Token);
        _orm.Upsert(new NullableUniqueRecord { Token = null, Value = "second" }, record => record.Token);
        Assert.Equal(2, _orm.Count<NullableUniqueRecord>());
    }

    [Fact]
    public void Native_upsert_is_atomic_when_update_violates_another_constraint()
    {
        _orm.CreateTable<AtomicUpsertRecord>();
        _orm.Insert(new AtomicUpsertRecord { Email = "a@example.com", Username = "one" });
        _orm.Insert(new AtomicUpsertRecord { Email = "b@example.com", Username = "two" });

        Assert.Throws<SQLiteException>(() => _orm.Upsert(
            new AtomicUpsertRecord { Email = "a@example.com", Username = "two" },
            record => record.Email));

        Assert.Equal(2, _orm.Count<AtomicUpsertRecord>());
        Assert.Equal("one", _orm.FirstOrDefault<AtomicUpsertRecord>(record => record.Email == "a@example.com")!.Username);
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
    private sealed class NullableUniqueRecord
    {
        [Key, AutoIncrement] public long RecordKey { get; set; }
        [Unique] public string? Token { get; set; }
        public string Value { get; set; } = string.Empty;
    }
    private sealed class AtomicUpsertRecord
    {
        [Key, AutoIncrement] public long RecordKey { get; set; }
        [Unique] public string Email { get; set; } = string.Empty;
        [Unique] public string Username { get; set; } = string.Empty;
    }
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
    private sealed class TestTransactionException(string message) : Exception(message);
}
