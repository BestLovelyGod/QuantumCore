// ============================================================================
// 量子核（QuantumCore）— IDbDataParameter 实现
// ============================================================================

using System.Data;
using System.Data.Common;

namespace QuantumCore.AdoNet;

/// <summary>
/// 量子核参数 — 继承 DbParameter
/// </summary>
public sealed class QuantumCoreParameter : DbParameter
{
    public override string ParameterName { get; set; } = "";
    public override object? Value { get; set; }
    public override DbType DbType { get; set; } = DbType.String;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; } = true;
    public override int Size { get; set; }
    public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;
    public override void ResetDbType() { }
}

/// <summary>
/// 参数集合
/// </summary>
public sealed class QuantumCoreParameterCollection : System.Data.Common.DbParameterCollection
{
    private readonly List<QuantumCoreParameter> _list = new();

    public override int Count => _list.Count;
    public override object SyncRoot => ((System.Collections.ICollection)_list).SyncRoot;

    public override int Add(object value)
    {
        _list.Add((QuantumCoreParameter)value);
        return _list.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values) Add(v);
    }

    public override void Clear() => _list.Clear();
    public override bool Contains(object value) => _list.Contains((QuantumCoreParameter)value);
    public override bool Contains(string value) => _list.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_list).CopyTo(array, index);
    public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.ICollection)_list).GetEnumerator();
    public override int IndexOf(object value) => _list.IndexOf((QuantumCoreParameter)value);
    public override int IndexOf(string parameterName) => _list.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _list.Insert(index, (QuantumCoreParameter)value);
    public override void Remove(object value) => _list.Remove((QuantumCoreParameter)value);
    public override void RemoveAt(int index) => _list.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _list.RemoveAll(p => p.ParameterName == parameterName);

    protected override DbParameter GetParameter(int index) => _list[index];
    protected override DbParameter GetParameter(string parameterName) =>
        _list.First(p => p.ParameterName == parameterName);
    protected override void SetParameter(int index, DbParameter value) => _list[index] = (QuantumCoreParameter)value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var idx = IndexOf(parameterName);
        if (idx >= 0) _list[idx] = (QuantumCoreParameter)value;
    }
}
