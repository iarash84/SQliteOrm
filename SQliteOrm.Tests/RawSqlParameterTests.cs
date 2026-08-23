namespace SQliteOrm.Tests;

public sealed class RawSqlParameterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sqlite-orm-raw-{Guid.NewGuid():N}.db");
    private readonly SqliteOrm _db;

    public RawSqlParameterTests()
    {
        _db = new SqliteOrm($"Data Source={_path}");
        _db.ExecuteNonQuery("CREATE TABLE \"RawItems\" (\"Name\" TEXT, \"Kind\" INTEGER, \"Optional\" TEXT);");
    }

    [Fact]
    public void Anonymous_object_parameters_work_for_commands_queries_and_scalars()
    {
        _db.ExecuteNonQuery(
            "INSERT INTO \"RawItems\" (\"Name\", \"Kind\") VALUES (@name, @kind);",
            new { name = "anonymous", kind = RawKind.Admin });

        var rows = _db.Query<RawItem>(
            "SELECT * FROM \"RawItems\" WHERE \"Name\" = @name AND \"Kind\" = @kind;",
            new { name = "anonymous", kind = RawKind.Admin });

        Assert.Equal(RawKind.Admin, Assert.Single(rows).Kind);
        Assert.Equal(1, _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM \"RawItems\" WHERE \"Name\" = @name;", new { name = "anonymous" }));
    }

    [Fact]
    public void Dictionary_parameters_remain_supported_with_or_without_prefixes()
    {
        _db.ExecuteNonQuery(
            "INSERT INTO \"RawItems\" (\"Name\", \"Kind\") VALUES (@name, @kind);",
            new Dictionary<string, object> { ["name"] = "dictionary", ["@kind"] = RawKind.User });

        Assert.Single(_db.Query<RawItem>("SELECT * FROM \"RawItems\" WHERE \"Name\" = @name;",
            new Dictionary<string, object> { ["@name"] = "dictionary" }));
    }

    [Fact]
    public void Null_and_enum_values_use_the_standard_type_mapping()
    {
        _db.ExecuteNonQuery(
            "INSERT INTO \"RawItems\" (\"Name\", \"Kind\", \"Optional\") VALUES (@name, @kind, @optional);",
            new { name = "nullable", kind = RawKind.Admin, optional = (string?)null });

        var row = Assert.Single(_db.Query<RawItem>(
            "SELECT * FROM \"RawItems\" WHERE \"Kind\" = @kind AND \"Optional\" IS @optional;",
            new { kind = RawKind.Admin, optional = (string?)null }));
        Assert.Null(row.Optional);
        Assert.Equal(RawKind.Admin, row.Kind);
    }

    [Fact]
    public void Injection_text_is_bound_as_a_value_and_never_changes_the_sql()
    {
        const string attack = "x'); DROP TABLE RawItems; --";
        _db.ExecuteNonQuery(
            "INSERT INTO \"RawItems\" (\"Name\", \"Kind\") VALUES (@name, @kind);",
            new { name = attack, kind = RawKind.User });

        Assert.Equal(attack, Assert.Single(_db.Query<RawItem>(
            "SELECT * FROM \"RawItems\" WHERE \"Name\" = @name;", new { name = attack })).Name);
        Assert.Equal(1, _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'RawItems';"));
    }

    [Fact]
    public void Duplicate_and_invalid_parameter_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _db.ExecuteScalar<int>("SELECT @name",
            new Dictionary<string, object> { ["name"] = 1, ["@name"] = 2 }));
        Assert.Throws<ArgumentException>(() => _db.ExecuteScalar<int>("SELECT 1",
            new Dictionary<string, object> { ["not valid"] = 1 }));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class RawItem
    {
        public string Name { get; set; } = string.Empty;
        public RawKind Kind { get; set; }
        public string? Optional { get; set; }
    }

    private enum RawKind
    {
        User = 1,
        Admin = 2
    }
}
