namespace SQliteOrm;

/// <summary>Configures one independent <see cref="SqliteOrm"/> instance.</summary>
public sealed class SqliteOrmOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public bool EnableForeignKeys { get; set; } = true;
    public bool EnableWal { get; set; }
    public TimeSpan? BusyTimeout { get; set; }
    public int? CommandTimeout { get; set; }
}
