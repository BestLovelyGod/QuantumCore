// ============================================================================
// 量子核（QuantumCore）— 磁盘引擎（Bitcask 实现）
// 顺序追加写入 + 内存索引 + 定期压缩合并
//
// 数据分区（使用不同 Bitcask 实例避免 key 冲突）：
//   String — 直接存储
//   Hash   — JSON 序列化 Dictionary<string, string>
//   ZSet   — JSON 序列化 Dictionary<string, double>
// ============================================================================

using System.Text.Json;

namespace QuantumCore.Disk;

/// <summary>
/// 磁盘引擎 — 基于 Bitcask 的持久化存储
/// 所有写入顺序追加到日志文件，内存中维护 key → offset 索引
/// 使用独立的 Bitcask 实例分别存储 String / Hash / ZSet
/// </summary>
internal sealed class DiskEngine : IDisposable
{
    private readonly QuantumCoreOptions _options;
    private readonly Bitcask _stringStore;
    private readonly Bitcask _hashStore;
    private readonly Bitcask _zsetStore;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public DiskEngine(QuantumCoreOptions options)
    {
        _options = options;
        var baseDir = options.DataDirectory;
        _stringStore = new Bitcask(baseDir);              // → {baseDir}/bitcask/
        _hashStore = new Bitcask(Path.Combine(baseDir, "hash"));  // → {baseDir}/hash/bitcask/
        _zsetStore = new Bitcask(Path.Combine(baseDir, "zset"));  // → {baseDir}/zset/bitcask/
    }

    // ── String 操作 ──

    public Task PersistStringAsync(string key, string value)
    {
        _stringStore.Set(key, value);
        return Task.CompletedTask;
    }

    public Task<string?> LoadStringAsync(string key)
    {
        var value = _stringStore.Get(key);
        return Task.FromResult(value);
    }

    public Task<bool> DeleteStringAsync(string key)
    {
        if (_stringStore.Exists(key))
        {
            _stringStore.Delete(key);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> ExistsStringAsync(string key)
    {
        return Task.FromResult(_stringStore.Exists(key));
    }

    // ── Hash 操作 ──

    /// <summary>
    /// 持久化 Hash 的单个 field（增量更新）
    /// 先读取现有 Hash，合并新 field，再写回
    /// </summary>
    public Task PersistHashFieldAsync(string key, string field, string value)
    {
        var existing = _hashStore.Get(key);
        var dict = string.IsNullOrEmpty(existing)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(existing, JsonOpts) ?? new();
        dict[field] = value;
        _hashStore.Set(key, JsonSerializer.Serialize(dict, JsonOpts));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从磁盘加载整个 Hash
    /// </summary>
    public Task<Dictionary<string, string>?> LoadHashAsync(string key)
    {
        var json = _hashStore.Get(key);
        if (string.IsNullOrEmpty(json))
            return Task.FromResult<Dictionary<string, string>?>(null);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
        return Task.FromResult(dict);
    }

    /// <summary>
    /// 删除 Hash 的单个 field
    /// </summary>
    public Task RemoveHashFieldAsync(string key, string field)
    {
        var existing = _hashStore.Get(key);
        if (string.IsNullOrEmpty(existing)) return Task.CompletedTask;

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(existing, JsonOpts);
        if (dict != null && dict.Remove(field))
        {
            if (dict.Count == 0)
                _hashStore.Delete(key);
            else
                _hashStore.Set(key, JsonSerializer.Serialize(dict, JsonOpts));
        }
        return Task.CompletedTask;
    }

    public Task DeleteHashAsync(string key)
    {
        if (_hashStore.Exists(key))
            _hashStore.Delete(key);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<string> GetAllHashKeys() => _hashStore.Keys;

    // ── ZSet 操作 ──

    /// <summary>
    /// 持久化 ZSet 的单个 member（增量更新）
    /// </summary>
    public Task PersistZSetMemberAsync(string key, string member, double score)
    {
        var existing = _zsetStore.Get(key);
        var dict = string.IsNullOrEmpty(existing)
            ? new Dictionary<string, double>()
            : JsonSerializer.Deserialize<Dictionary<string, double>>(existing, JsonOpts) ?? new();
        dict[member] = score;
        _zsetStore.Set(key, JsonSerializer.Serialize(dict, JsonOpts));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从磁盘加载整个 ZSet
    /// </summary>
    public Task<Dictionary<string, double>?> LoadZSetAsync(string key)
    {
        var json = _zsetStore.Get(key);
        if (string.IsNullOrEmpty(json))
            return Task.FromResult<Dictionary<string, double>?>(null);
        var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOpts);
        return Task.FromResult(dict);
    }

    /// <summary>
    /// 删除 ZSet 的单个 member
    /// </summary>
    public Task RemoveZSetMemberAsync(string key, string member)
    {
        var existing = _zsetStore.Get(key);
        if (string.IsNullOrEmpty(existing)) return Task.CompletedTask;

        var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(existing, JsonOpts);
        if (dict != null && dict.Remove(member))
        {
            if (dict.Count == 0)
                _zsetStore.Delete(key);
            else
                _zsetStore.Set(key, JsonSerializer.Serialize(dict, JsonOpts));
        }
        return Task.CompletedTask;
    }

    public Task DeleteZSetAsync(string key)
    {
        if (_zsetStore.Exists(key))
            _zsetStore.Delete(key);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<string> GetAllZSetKeys() => _zsetStore.Keys;

    // ── 压缩合并 ──

    /// <summary>
    /// 压缩合并所有日志文件
    /// </summary>
    public async Task CompactAsync()
    {
        await _stringStore.CompactAsync();
        await _hashStore.CompactAsync();
        await _zsetStore.CompactAsync();
    }

    /// <summary>
    /// 清理旧的段文件
    /// </summary>
    public void CleanupOldSegments()
    {
        _stringStore.CleanupOldSegments();
        _hashStore.CleanupOldSegments();
        _zsetStore.CleanupOldSegments();
    }

    public void Dispose()
    {
        _stringStore.Dispose();
        _hashStore.Dispose();
        _zsetStore.Dispose();
    }
}
