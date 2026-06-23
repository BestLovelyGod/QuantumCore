// ============================================================================
// 量子核（QuantumCore）— ADO.NET 标准接口实现
// 让依赖标准 SQL 的代码零修改使用量子核
// ============================================================================

using System.Data;
using System.Data.Common;
using QuantumCore.Disk;
using QuantumCore.Sql;

namespace QuantumCore.AdoNet;

/// <summary>
/// 量子核数据库连接 — 实现 IDbConnection
/// 使用方式与 SQLiteConnection 完全一致
/// </summary>
public sealed class QuantumCoreConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    private string _connectionString = "";
    private readonly SqlQuery _sql;
    private readonly Bitcask? _storage;
    private QuantumCoreTransaction? _currentTransaction;

    internal QueryExecutor Executor { get; }

    public QuantumCoreConnection() : this("") { }

    public QuantumCoreConnection(string connectionString)
    {
        _connectionString = connectionString;
        _storage = CreateStorage(connectionString);
        Executor = new QueryExecutor(_storage);
        _sql = new SqlQuery();
    }

    // ── IDbConnection ──

    public override string ConnectionString
    {
        get => _connectionString;
        set { _connectionString = value; }
    }

    public override string Database => "QuantumCore";
    public override string DataSource => _connectionString;
    public override string ServerVersion => "1.0.0";
    public override ConnectionState State => _state;

    public override void Open()
    {
        _state = ConnectionState.Open;
    }

    public override void Close()
    {
        if (_state == ConnectionState.Closed) return;
        _currentTransaction?.Dispose();
        _currentTransaction = null;
        _storage?.Dispose();
        _state = ConnectionState.Closed;
    }

    public override void ChangeDatabase(string databaseName) { }

    // ── 创建命令 ──

    protected override DbCommand CreateDbCommand()
    {
        return new QuantumCoreCommand(this);
    }

    // ── 事务 ──

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开");

        _currentTransaction = new QuantumCoreTransaction(this, isolationLevel);
        return _currentTransaction;
    }

    internal void ClearTransaction() => _currentTransaction = null;

    // ── 内部方法 ──

    internal QueryResult ExecuteSql(string sql)
    {
        // 替换参数占位符（@param）
        var parser = new SqlParser(sql);
        var statement = parser.Parse();
        return Executor.Execute(statement);
    }

    internal QueryResult ExecuteSql(string sql, Dictionary<string, object?> parameters)
    {
        // 替换 @param 为实际值
        var processedSql = sql;
        foreach (var kv in parameters)
        {
            var value = kv.Value switch
            {
                string s => $"'{s}'",
                null => "NULL",
                _ => kv.Value.ToString()!
            };
            processedSql = processedSql.Replace($"@{kv.Key}", value);
        }

        var parser = new SqlParser(processedSql);
        var statement = parser.Parse();
        return Executor.Execute(statement);
    }

    private static Bitcask? CreateStorage(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString) || connectionString == ":memory:")
            return null; // 内存模式，不持久化

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var dataDir = "./data";
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                dataDir = kv[1].Trim();
            }
        }

        return new Bitcask(dataDir);
    }
}
