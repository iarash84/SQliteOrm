using System.Data.SQLite;

namespace SQliteOrm.Transactions;

internal sealed class TransactionContext
{
    internal TransactionContext(SQLiteConnection connection, SQLiteTransaction transaction)
    { Connection = connection; Transaction = transaction; }
    internal SQLiteConnection Connection { get; }
    internal SQLiteTransaction Transaction { get; }
    internal bool IsActive { get; set; } = true;
}
