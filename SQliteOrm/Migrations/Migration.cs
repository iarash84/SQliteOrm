namespace SQliteOrm.Migrations;

/// <summary>Defines one deterministic database schema migration.</summary>
public abstract class Migration
{
    /// <summary>Stable identifier used for ordering and migration history.</summary>
    public virtual string Id => GetType().Name;
    /// <summary>Describes schema operations that apply this migration.</summary>
    public abstract void Up(MigrationBuilder migration);
    /// <summary>Describes schema operations that revert this migration where supported.</summary>
    public abstract void Down(MigrationBuilder migration);
}
