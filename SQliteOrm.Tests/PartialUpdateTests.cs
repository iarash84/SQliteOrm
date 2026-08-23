using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQliteOrm.Tests;

public sealed class PartialUpdateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sqlite-orm-update-{Guid.NewGuid():N}.db");
    private readonly SqliteOrm _db;

    public PartialUpdateTests()
    {
        _db = new SqliteOrm($"Data Source={_path}");
        _db.CreateTable<UpdateRecord>();
    }

    [Fact]
    public void One_property_can_be_updated_without_loading_the_entity()
    {
        var id = Insert("first", active: true);

        var affected = _db.Update<UpdateRecord>()
            .Set(x => x.Active, false)
            .Where(x => x.Id == id)
            .Execute();

        Assert.Equal(1, affected);
        Assert.False(_db.Find<UpdateRecord, int>(id)!.Active);
    }

    [Fact]
    public void Multiple_properties_are_updated_together()
    {
        var id = Insert("before", active: false);
        var updatedAt = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        _db.Update<UpdateRecord>()
            .Set(x => x.Name, "after")
            .Set(x => x.UpdatedAt, updatedAt)
            .Where(x => x.Id == id)
            .Execute();

        var row = _db.Find<UpdateRecord, int>(id)!;
        Assert.Equal("after", row.Name);
        Assert.Equal(updatedAt, row.UpdatedAt);
    }

    [Fact]
    public void Complex_predicates_use_the_existing_expression_compiler()
    {
        Insert("match", active: true, kind: UpdateKind.Admin, score: 1);
        Insert("also-match", active: true, kind: UpdateKind.User, score: 10);
        Insert("inactive", active: false, kind: UpdateKind.Admin, score: 10);

        var affected = _db.Update<UpdateRecord>()
            .Set(x => x.Name, "updated")
            .Where(x => x.Active && (x.Kind == UpdateKind.Admin || x.Score > 5))
            .Execute();

        Assert.Equal(2, affected);
        Assert.Equal(2, _db.Count<UpdateRecord>(x => x.Name == "updated"));
    }

    [Fact]
    public void Null_and_enum_values_are_parameterized_and_round_trip()
    {
        var id = Insert("values", active: true);

        _db.Update<UpdateRecord>()
            .Set(x => x.Note, null)
            .Set(x => x.Kind, UpdateKind.Admin)
            .Where(x => x.Id == id)
            .Execute();

        var row = _db.Find<UpdateRecord, int>(id)!;
        Assert.Null(row.Note);
        Assert.Equal(UpdateKind.Admin, row.Kind);
    }

    [Fact]
    public void Execute_returns_zero_when_no_rows_match()
    {
        Assert.Equal(0, _db.Update<UpdateRecord>()
            .Set(x => x.Active, true)
            .Where(x => x.Id == -1)
            .Execute());
    }

    [Fact]
    public void Generated_keys_and_unmapped_properties_cannot_be_set()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _db.Update<UpdateRecord>().Set(x => x.Id, 10));
        Assert.Throws<ArgumentException>(() =>
            _db.Update<UpdateRecord>().Set(x => x.Ignored, "value"));
    }

    [Fact]
    public void Set_and_where_are_both_required()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _db.Update<UpdateRecord>().Where(x => x.Active).Execute());
        Assert.Throws<InvalidOperationException>(() =>
            _db.Update<UpdateRecord>().Set(x => x.Active, false).Execute());
    }

    private int Insert(string name, bool active, UpdateKind kind = UpdateKind.User, int score = 0) =>
        _db.Insert(new UpdateRecord
        {
            Name = name,
            Active = active,
            Kind = kind,
            Score = score,
            Note = "present"
        });

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class UpdateRecord
    {
        [Key, AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Active { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? Note { get; set; }
        public UpdateKind Kind { get; set; }
        public int Score { get; set; }
        [NotMapped]
        public string? Ignored { get; set; }
    }

    private enum UpdateKind
    {
        User,
        Admin
    }
}
