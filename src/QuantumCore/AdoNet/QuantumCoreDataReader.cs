// ============================================================================
// 量子核（QuantumCore）— IDataReader 实现
// ============================================================================

using System.Data;
using System.Data.Common;
using QuantumCore.Sql;

namespace QuantumCore.AdoNet;

/// <summary>
/// 量子核数据读取器 — 实现 IDataReader
/// </summary>
public sealed class QuantumCoreDataReader : DbDataReader
{
    private readonly QueryResult _result;
    private int _rowIndex = -1;
    private bool _closed;

    internal QuantumCoreDataReader(QueryResult result)
    {
        _result = result;
    }

    public override int FieldCount => _result.Columns.Count;
    public override bool HasRows => _result.Rows.Count > 0;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => _result.AffectedRows;

    // ── 按名称读取 ──

    public override object? this[string name] =>
        _result.Rows.Count > _rowIndex && _rowIndex >= 0
            ? _result.Rows[_rowIndex].GetValueOrDefault(name)
            : null;

    public override object? this[int ordinal] =>
        _result.Rows.Count > _rowIndex && _rowIndex >= 0 && ordinal < _result.Columns.Count
            ? _result.Rows[_rowIndex].GetValueOrDefault(_result.Columns[ordinal])
            : null;

    // ── 获取列信息 ──

    public override string GetName(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _result.Columns[ordinal];
    }
    public override int GetOrdinal(string name) => _result.Columns.IndexOf(name);
    public override string GetDataTypeName(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return "Object";
    }
    public override Type GetFieldType(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return typeof(object);
    }
    public override Type GetProviderSpecificFieldType(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return typeof(object);
    }

    // ── 读取值 ──

    public override object? GetValue(int ordinal) => this[ordinal];
    public override int GetValues(object?[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
            values[i] = this[i];
        return count;
    }

    // ── 类型读取 ──

    public override bool IsDBNull(int ordinal) => this[ordinal] == null;
    public override string GetString(int ordinal) => this[ordinal]?.ToString() ?? "";
    public override int GetInt32(int ordinal) => Convert.ToInt32(this[ordinal]);
    public override long GetInt64(int ordinal) => Convert.ToInt64(this[ordinal]);
    public override double GetDouble(int ordinal) => Convert.ToDouble(this[ordinal]);
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(this[ordinal]);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(this[ordinal]);
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(this[ordinal]);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;

    // ── 遍历 ──

    public override bool Read()
    {
        _rowIndex++;
        return _rowIndex < _result.Rows.Count;
    }

    public override bool NextResult() => false;

    public override void Close() => _closed = true;

    public override int Depth => 0;
    public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.ICollection)_result.Rows).GetEnumerator();
    public override DataTable GetSchemaTable() => new();
    public override Guid GetGuid(int ordinal) => Guid.Empty;
    public override float GetFloat(int ordinal) => Convert.ToSingle(this[ordinal]);
    public override byte GetByte(int ordinal) => Convert.ToByte(this[ordinal]);
    public override char GetChar(int ordinal) => '\0';
    public override short GetInt16(int ordinal) => Convert.ToInt16(this[ordinal]);
}
