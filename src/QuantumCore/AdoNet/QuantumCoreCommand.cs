// ============================================================================
// 量子核（QuantumCore）— IDbCommand 实现
// ============================================================================

using System.Data;
using System.Data.Common;
using QuantumCore.Sql;

namespace QuantumCore.AdoNet;

/// <summary>
/// 量子核数据库命令 — 实现 IDbCommand
/// </summary>
public sealed class QuantumCoreCommand : DbCommand
{
    private readonly QuantumCoreConnection _connection;
    private string _commandText = "";
    private QuantumCoreParameterCollection _parameters = new();

    internal QuantumCoreCommand(QuantumCoreConnection connection)
    {
        _connection = connection;
    }

    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? "";
    }

    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set { }
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => null;
        set { }
    }

    // ── 执行 ──

    public override int ExecuteNonQuery()
    {
        var result = ExecuteCore();
        return result.AffectedRows;
    }

    public override object? ExecuteScalar()
    {
        var result = ExecuteCore();
        if (result.Rows.Count > 0 && result.Columns.Count > 0)
            return result.Rows[0][result.Columns[0]];
        return null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var result = ExecuteCore();
        return new QuantumCoreDataReader(result);
    }

    // ── 参数化执行 ──

    private QueryResult ExecuteCore()
    {
        // 构建参数字典
        var paramDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (QuantumCoreParameter p in _parameters)
        {
            paramDict[p.ParameterName!.TrimStart('@')] = p.Value;
        }

        // 按长度降序替换，避免短参数名误匹配长参数名（如 @id 匹配 @id_card）
        var sql = _commandText;
        foreach (var kv in paramDict.OrderByDescending(k => k.Key.Length))
        {
            var placeholder = $"@{kv.Key}";
            var value = FormatValue(kv.Value);
            // 只替换完整的参数占位符（后面是非标识符字符或字符串结尾）
            sql = ReplaceParameterPlaceholder(sql, placeholder, value);
        }

        return _connection.ExecuteSql(sql);
    }

    /// <summary>
    /// 安全替换参数占位符：只替换完整的 @param（后面不是字母/数字/下划线）
    /// </summary>
    private static string ReplaceParameterPlaceholder(string sql, string placeholder, string value)
    {
        var result = new System.Text.StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            int idx = sql.IndexOf(placeholder, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                result.Append(sql, i, sql.Length - i);
                break;
            }

            // 检查占位符后面是否紧跟标识符字符（字母/数字/下划线）
            int afterIdx = idx + placeholder.Length;
            bool is完整 = afterIdx >= sql.Length || !char.IsLetterOrDigit(sql[afterIdx]) && sql[afterIdx] != '_';

            if (is完整)
            {
                result.Append(sql, i, idx - i);
                result.Append(value);
                i = afterIdx;
            }
            else
            {
                // 不是完整占位符，跳过
                result.Append(sql, i, afterIdx - i);
                i = afterIdx;
            }
        }
        return result.ToString();
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "1" : "0",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            _ => value.ToString()!
        };
    }

    // ── 预编译（简化实现） ──

    public override void Prepare() { }

    public override void Cancel() { }

    protected override DbParameter CreateDbParameter()
    {
        return new QuantumCoreParameter();
    }
}
