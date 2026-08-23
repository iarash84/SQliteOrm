using System.ComponentModel.DataAnnotations;

namespace SQliteOrm.Tests;

public sealed class MetadataValidationTests
{
    [Fact]
    public void Composite_keys_are_rejected_instead_of_silently_selecting_one_key()
    {
        using var db = new SqliteOrm("Data Source=:memory:");

        var exception = Assert.Throws<InvalidOperationException>(() => db.CreateTable<CompositeKeyEntity>());

        Assert.Contains("Composite keys are not supported", exception.Message);
    }

    private sealed class CompositeKeyEntity
    {
        [Key]
        public int TenantId { get; set; }

        [Key]
        public int EntityId { get; set; }
    }
}
