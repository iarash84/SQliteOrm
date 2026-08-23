using System.Linq.Expressions;

namespace SQliteOrm;

/// <summary>Provides ORM operations bound to one SQLite connection and transaction.</summary>
public sealed class SqliteTransactionSession
{
    private readonly SqliteOrm _orm;
    private bool _active = true;
    internal SqliteTransactionSession(SqliteOrm orm) => _orm = orm;
    internal void Deactivate() => _active = false;
    public int Insert<T>(T entity) { EnsureActive(); return _orm.Insert(entity); }
    public void CreateTable<T>() where T : new() { EnsureActive(); _orm.CreateTable<T>(); }
    public void Insert<T>(List<T> entities) { EnsureActive(); _orm.Insert(entities); }
    public void Update<T>(T entity) { EnsureActive(); _orm.Update(entity); }
    public void Upsert<T, TConflict>(T entity, Expression<Func<T, TConflict>> conflictOn) where T : new()
    { EnsureActive(); _orm.Upsert(entity, conflictOn); }
    public void Upsert<T>(T entity) where T : new() { EnsureActive(); _orm.Upsert(entity); }
    public int Delete<T>(Expression<Func<T, bool>> predicate) { EnsureActive(); return _orm.Delete(predicate); }
    public void Delete<T, TKey>(TKey key) { EnsureActive(); _orm.Delete<T, TKey>(key); }
    public SqliteQuery<T> Table<T>() where T : new() { EnsureActive(); return _orm.Table<T>(); }
    public List<T> Query<T>(string sql, Dictionary<string, object>? parameters = null) where T : new()
    { EnsureActive(); return _orm.Query<T>(sql, parameters); }
    public List<T> Query<T>(string sql, object parameters) where T : new()
    { EnsureActive(); return _orm.Query<T>(sql, parameters); }
    public T? ExecuteScalar<T>(string sql, Dictionary<string, object>? parameters = null)
    { EnsureActive(); return _orm.ExecuteScalar<T>(sql, parameters); }
    public T? ExecuteScalar<T>(string sql, object parameters)
    { EnsureActive(); return _orm.ExecuteScalar<T>(sql, parameters); }
    public int Execute(string sql, Dictionary<string, object>? parameters = null)
    { EnsureActive(); return _orm.ExecuteNonQueryAffected(sql, parameters); }
    public int Execute(string sql, object parameters)
    { EnsureActive(); return _orm.ExecuteNonQueryAffected(sql, RawSql.RawSqlParameters.FromObject(parameters)); }
    private void EnsureActive()
    {
        if (!_active) throw new InvalidOperationException("The transaction session can only be used inside its callback.");
    }
}
