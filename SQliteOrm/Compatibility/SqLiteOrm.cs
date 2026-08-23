using System.Data.SQLite;
using System.Threading;

namespace SQliteOrm;

/// <summary>Compatibility facade for the former global singleton API.</summary>
public sealed class SqLiteOrm : SqliteOrm
{
    private static SqLiteOrm? _instance;
    private SqLiteOrm(string databasePath) : base(new SqliteOrmOptions
    {
        ConnectionString = new SQLiteConnectionStringBuilder
        {
            DataSource = databasePath,
            Version = 3
        }.ConnectionString,
        EnableForeignKeys = true
    }) { }

    /// <summary>Gets the initialized compatibility singleton.</summary>
    /// <remarks>New code should construct and inject <see cref="SqliteOrm"/> instead.</remarks>
    public static SqLiteOrm Instance => _instance ?? throw new InvalidOperationException(
        "SqLiteOrm is not initialized. Prefer constructing SqliteOrm, or call SqLiteOrm.Initialize(databasePath).");

    /// <summary>Replaces the compatibility singleton with an instance for the specified database path.</summary>
    public static void Initialize(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path cannot be null or empty.", nameof(databasePath));
        var replacement = new SqLiteOrm(databasePath);
        Interlocked.Exchange(ref _instance, replacement)?.Dispose();
    }
}
