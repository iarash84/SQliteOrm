namespace SQliteOrm;

/// <summary>Configures one independent <see cref="SqliteOrm"/> instance.</summary>
public sealed class SqliteOrmOptions
{
    /// <summary>Gets or sets the System.Data.SQLite connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>Gets or sets whether each opened connection enables SQLite foreign-key enforcement.</summary>
    public bool EnableForeignKeys { get; set; } = true;
    /// <summary>Gets or sets whether each opened connection requests write-ahead logging.</summary>
    public bool EnableWal { get; set; }
    /// <summary>Gets or sets the SQLite busy timeout applied to each opened connection.</summary>
    public TimeSpan? BusyTimeout { get; set; }
    /// <summary>Gets or sets the command timeout in seconds, or <see langword="null"/> for provider defaults.</summary>
    public int? CommandTimeout { get; set; }
}
