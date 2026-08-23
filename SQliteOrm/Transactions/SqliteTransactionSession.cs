using System.Linq.Expressions;

namespace SQliteOrm;

/// <summary>Provides ORM operations bound to one SQLite connection and transaction.</summary>
public sealed class SqliteTransactionSession
{
    private readonly SqliteOrm _orm;
    private bool _active = true;
    internal SqliteTransactionSession(SqliteOrm orm) => _orm = orm;
    internal void Deactivate() => _active = false;
    /// <summary>Inserts one entity in the active transaction.</summary>
    public int Insert<T>(T entity) { EnsureActive(); return _orm.Insert(entity); }
    /// <summary>Creates a mapped table in the active transaction.</summary>
    public void CreateTable<T>() where T : new() { EnsureActive(); _orm.CreateTable<T>(); }
    /// <summary>Inserts entities in the active transaction.</summary>
    public void Insert<T>(List<T> entities) { EnsureActive(); _orm.Insert(entities); }
    /// <summary>Updates an entity in the active transaction.</summary>
    public void Update<T>(T entity) { EnsureActive(); _orm.Update(entity); }
    /// <summary>Atomically upserts using a selected conflict target.</summary>
    public void Upsert<T, TConflict>(T entity, Expression<Func<T, TConflict>> conflictOn) where T : new()
    { EnsureActive(); _orm.Upsert(entity, conflictOn); }
    /// <summary>Atomically upserts using the mapped primary key.</summary>
    public void Upsert<T>(T entity) where T : new() { EnsureActive(); _orm.Upsert(entity); }
    /// <summary>Deletes rows matching a typed predicate.</summary>
    public int Delete<T>(Expression<Func<T, bool>> predicate) { EnsureActive(); return _orm.Delete(predicate); }
    /// <summary>Deletes by the mapped primary key.</summary>
    public void Delete<T, TKey>(TKey key) { EnsureActive(); _orm.Delete<T, TKey>(key); }
    /// <summary>Starts a transaction-bound typed query.</summary>
    public SqliteQuery<T> Table<T>() where T : new() { EnsureActive(); return _orm.Table<T>(); }
    /// <summary>Executes raw SQL and materializes rows using dictionary parameters.</summary>
    public List<T> Query<T>(string sql, Dictionary<string, object>? parameters = null) where T : new()
    { EnsureActive(); return _orm.Query<T>(sql, parameters); }
    /// <summary>Executes raw SQL and materializes rows using object parameters.</summary>
    public List<T> Query<T>(string sql, object parameters) where T : new()
    { EnsureActive(); return _orm.Query<T>(sql, parameters); }
    /// <summary>Executes scalar SQL using dictionary parameters.</summary>
    public T? ExecuteScalar<T>(string sql, Dictionary<string, object>? parameters = null)
    { EnsureActive(); return _orm.ExecuteScalar<T>(sql, parameters); }
    /// <summary>Executes scalar SQL using object parameters.</summary>
    public T? ExecuteScalar<T>(string sql, object parameters)
    { EnsureActive(); return _orm.ExecuteScalar<T>(sql, parameters); }
    /// <summary>Executes raw SQL using dictionary parameters.</summary>
    public int Execute(string sql, Dictionary<string, object>? parameters = null)
    { EnsureActive(); return _orm.ExecuteNonQueryAffected(sql, parameters); }
    /// <summary>Executes raw SQL using object parameters.</summary>
    public int Execute(string sql, object parameters)
    { EnsureActive(); return _orm.ExecuteNonQueryAffected(sql, RawSql.RawSqlParameters.FromObject(parameters)); }
    private void EnsureActive()
    {
        if (!_active) throw new InvalidOperationException("The transaction session can only be used inside its callback.");
    }
}
