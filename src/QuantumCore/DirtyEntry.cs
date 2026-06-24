// ============================================================================
// 量子核（QuantumCore）— 脏数据条目（多态设计）
//
// 每种操作类型封装为独立子类，构造函数保证字段合法，
// FlushToAsync 消除 switch/if 分支。
// ============================================================================

using QuantumCore.Disk;

namespace QuantumCore;

/// <summary>
/// 脏数据条目基类 — 多态分派，取代 enum + switch
/// </summary>
internal abstract class DirtyEntry
{
    public string Key { get; }

    /// <summary>用于跳过已删除 key 的标签（如 "str:xxx"、"hash:xxx"）</summary>
    public abstract string DeletionTag { get; }

    /// <summary>将此条目刷盘到 DiskEngine</summary>
    public abstract Task FlushToAsync(DiskEngine disk);

    protected DirtyEntry(string key) => Key = key;
}

// ── String 操作 ──

internal sealed class DirtyStringSet : DirtyEntry
{
    public string Value { get; }
    public override string DeletionTag => $"str:{Key}";
    public override Task FlushToAsync(DiskEngine disk) => disk.PersistStringAsync(Key, Value);
    public DirtyStringSet(string key, string value) : base(key) => Value = value;
}

internal sealed class DirtyStringDelete : DirtyEntry
{
    public override string DeletionTag => $"str:{Key}";
    public override Task FlushToAsync(DiskEngine disk) => disk.DeleteStringAsync(Key);
    public DirtyStringDelete(string key) : base(key) { }
}

// ── Hash 操作 ──

internal sealed class DirtyHashSet : DirtyEntry
{
    public string Field { get; }
    public string Value { get; }
    public override string DeletionTag => $"hash:{Key}";
    public override Task FlushToAsync(DiskEngine disk) => disk.PersistHashFieldAsync(Key, Field, Value);
    public DirtyHashSet(string key, string field, string value) : base(key)
    {
        Field = field;
        Value = value;
    }
}

internal sealed class DirtyHashDelete : DirtyEntry
{
    public string Field { get; }
    public override string DeletionTag => $"hash:{Key}";
    public override Task FlushToAsync(DiskEngine disk) => disk.RemoveHashFieldAsync(Key, Field);
    public DirtyHashDelete(string key, string field) : base(key) => Field = field;
}

// ── ZSet 操作 ──

internal sealed class DirtyZSetAdd : DirtyEntry
{
    public string Field { get; }
    public double Score { get; }
    public override string DeletionTag => $"zset:{Key}";
    public override Task FlushToAsync(DiskEngine disk) => disk.PersistZSetMemberAsync(Key, Field, Score);
    public DirtyZSetAdd(string key, string field, double score) : base(key)
    {
        Field = field;
        Score = score;
    }
}

internal sealed class DirtyZSetRemove : DirtyEntry
{
    public string Field { get; }
    public override string DeletionTag => $"zset:{Key}";
    public override Task FlushToAsync(DiskEngine disk) => disk.RemoveZSetMemberAsync(Key, Field);
    public DirtyZSetRemove(string key, string field) : base(key) => Field = field;
}
