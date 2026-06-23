// ============================================================================
// 量子核（QuantumCore）— SQL 查询接口
// 对外暴露 SQL 查询能力，内部使用 QueryExecutor
// ============================================================================

using QuantumCore.Disk;
using QuantumCore.Sql.Dialect;

namespace QuantumCore.Sql;

/// <summary>
/// SQL 查询接口 — 通过 SQL 语句操作数据
/// 支持通过方言插件扩展 SQL 兼容性
/// </summary>
public sealed class SqlQuery : IDisposable
{
    private readonly QueryExecutor _executor;
    private readonly DialectRegistry _dialects;

    public SqlQuery() : this(null) { }

    internal SqlQuery(Bitcask? storage)
    {
        _executor = new QueryExecutor(storage);
        _dialects = new DialectRegistry();
    }

    /// <summary>
    /// 注册 SQL 方言插件
    /// </summary>
    public SqlQuery WithDialect(IDialectPlugin plugin)
    {
        _dialects.Register(plugin);
        return this;
    }

    /// <summary>
    /// 执行一条 SQL 语句
    /// </summary>
    public QueryResult Execute(string sql)
    {
        var parser = new SqlParser(sql, _dialects);
        var statement = parser.Parse();
        return _executor.Execute(statement);
    }

    /// <summary>
    /// 获取已注册的方言列表
    /// </summary>
    public IReadOnlyList<IDialectPlugin> Dialects => _dialects.Plugins;

    public void Dispose()
    {
        // QueryExecutor 无需释放
    }
}
