using SQliteOrm.Transactions;

namespace SQliteOrm;

public partial class SqliteOrm
{
    /// <summary>Runs all callback operations on one connection and commits them atomically.</summary>
    /// <param name="action">Operations to execute through the transaction-bound session.</param>
    /// <exception cref="InvalidOperationException">Thrown when a nested transaction is requested.</exception>
    /// <remarks>The original operation exception is rethrown after a best-effort rollback.</remarks>
    public void Transaction(Action<SqliteTransactionSession> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_transactionContext.Value is { IsActive: true })
            throw new InvalidOperationException("Nested transactions are not supported.");
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var session = new SqliteTransactionSession(this);
            _transactionContext.Value = new TransactionContext(connection, transaction);
            try
            {
                action(session);
                transaction.Commit();
            }
            catch
            {
                // Rollback failure must not replace the exception that caused the transaction to fail.
                try { transaction.Rollback(); }
                catch { }
                throw;
            }
            finally
            {
                session.Deactivate();
                _transactionContext.Value!.IsActive = false;
                _transactionContext.Value = null;
            }
        }
    }
}
