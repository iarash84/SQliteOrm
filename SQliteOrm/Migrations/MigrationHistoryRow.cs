namespace SQliteOrm.Migrations;

internal sealed class MigrationHistoryRow
{
    public string MigrationId { get; set; } = string.Empty;
    public DateTimeOffset AppliedAtUtc { get; set; }
}
