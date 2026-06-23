// ============================================================================
// 量子核（QuantumCore）— IDbTransaction 实现
// ============================================================================

using System.Data;
using System.Data.Common;
using QuantumCore.Sql;

namespace QuantumCore.AdoNet;

/// <summary>
/// 量子核事务 — 实现 IDbTransaction
/// 通过 BEGIN / COMMIT / ROLLBACK 管理事务
/// </summary>
public sealed class QuantumCoreTransaction : DbTransaction
{
    private readonly QuantumCoreConnection _connection;
    private IsolationLevel _isolationLevel;
    private bool _completed;

    internal QuantumCoreTransaction(QuantumCoreConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        _isolationLevel = isolationLevel;

        // 执行 BEGIN
        _connection.Executor.Execute(new BeginTransactionStatement());
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;
    protected override DbConnection? DbConnection => _connection;


    public override void Commit()
    {
        if (_completed) throw new InvalidOperationException("事务已完成");
        _connection.Executor.Execute(new CommitTransactionStatement());
        _completed = true;
        _connection.ClearTransaction();
    }

    public override void Rollback()
    {
        if (_completed) throw new InvalidOperationException("事务已完成");
        _connection.Executor.Execute(new RollbackTransactionStatement());
        _completed = true;
        _connection.ClearTransaction();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            try { Rollback(); } catch { }
        }
        base.Dispose(disposing);
    }
}
