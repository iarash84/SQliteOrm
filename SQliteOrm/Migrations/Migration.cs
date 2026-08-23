namespace SQliteOrm.Migrations;

/// <summary>Defines one deterministic database schema migration.</summary>
public abstract class Migration
{
    /// <summary>Stable identifier used for ordering and migration history.</summary>
    public virtual string Id => GetType().Name;
    public abstract void Up(MigrationBuilder migration);
    public abstract void Down(MigrationBuilder migration);
}
