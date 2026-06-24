// ============================================================================
// 量子核（QuantumCore）— 磁盘引擎（Bitcask 实现）
// 顺序追加写入 + 内存索引 + 定期压缩合并
//
// 数据分区（使用不同 Bitcask 实例避免 key 冲突）：
//   String — 直接存储（key = 原始 key）
//   Hash   — 字段级存储（key = "h\0{hashKey}\0{field}"，value = 字段值）
//   ZSet   — 字段级存储（key = "z\0{zsetKey}\0{member}"，value = score 字符串）
//
// Hash/ZSet 使用字段级 key，消除读-改-写放大：
//   写入：直接 Set，无需读取和反序列化整个 Dictionary
//   删除：直接 Delete，无需读取
//   读取全部：前缀扫描 + 顺序 IO
// ============================================================================

namespace QuantumCore.Disk;

/// <summary>
/// 磁盘引擎 — 基于 Bitcask 的持久化存储
/// 所有写入顺序追加到日志文件，内存中维护 key → offset 索引
/// 使用独立的 Bitcask 实例分别存储 String / Hash / ZSet
///
/// Hash/ZSet 采用字段级 key 分离，每个 field/member 是独立的 Bitcask 条目：
//   写入 O(1)，零读放大；读取全部 O(N) 前缀扫描（仅 cache miss 时触发）
/// </summary>
internal sealed class DiskEngine : IDisposable
{
    private readonly QuantumCoreOptions _options;
    private readonly Bitcask _stringStore;
    private readonly Bitcask _hashStore;
    private readonly Bitcask _zsetStore;

    // ── 字段级 key 前缀（\0 作为分隔符，不可出现在正常字符串中） ──
    private const string HashPrefix = "h\0";
    private const string ZSetPrefix = "z\0";

    public DiskEngine(QuantumCoreOptions options)
    {
        _options = options;
        var baseDir = options.DataDirectory;
        _stringStore = new Bitcask(baseDir);                         // → {baseDir}/bitcask/
        _hashStore = new Bitcask(Path.Combine(baseDir, "hash"));     // → {baseDir}/hash/bitcask/
        _zsetStore = new Bitcask(Path.Combine(baseDir, "zset"));     // → {baseDir}/zset/bitcask/
    }

    // ── 辅助方法 ──

    private static string HashFieldKey(string key, string field) => $"{HashPrefix}{key}\0{field}";
    private static string ZSetMemberKey(string key, string member) => $"{ZSetPrefix}{key}\0{member}";

    // ── String 操作（不变） ──

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

    // ── Hash 操作（字段级 key，零读放大） ──

    /// <summary>
    /// 持久化 Hash 的单个 field — 直接写入，无需读取整个 Hash
    /// Bitcask key: "h\0{key}\0{field}" → value
    /// </summary>
    public Task PersistHashFieldAsync(string key, string field, string value)
    {
        _hashStore.Set(HashFieldKey(key, field), value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从磁盘加载整个 Hash — 前缀扫描所有 field，顺序 IO
    /// </summary>
    public Task<Dictionary<string, string>?> LoadHashAsync(string key)
    {
        var prefix = $"{HashPrefix}{key}\0";
        var raw = _hashStore.GetAllByPrefix(prefix);
        if (raw.Count == 0)
            return Task.FromResult<Dictionary<string, string>?>(null);

        var dict = new Dictionary<string, string>(raw.Count);
        foreach (var (fullKey, value) in raw)
        {
            // fullKey = "h\0{key}\0{field}"，提取 field 部分
            var field = fullKey.Substring(prefix.Length);
            dict[field] = value;
        }
        return Task.FromResult<Dictionary<string, string>?>(dict);
    }

    /// <summary>
    /// 删除 Hash 的单个 field — 直接删除，无需读取整个 Hash
    /// </summary>
    public Task RemoveHashFieldAsync(string key, string field)
    {
        _hashStore.Delete(HashFieldKey(key, field));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除整个 Hash — 删除所有 field key
    /// </summary>
    public Task DeleteHashAsync(string key)
    {
        var prefix = $"{HashPrefix}{key}\0";
        var fieldKeys = _hashStore.GetKeysByPrefix(prefix);
        foreach (var fieldKey in fieldKeys)
            _hashStore.Delete(fieldKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取所有逻辑 Hash key（从字段级 key 中提取唯一 key）
    /// </summary>
    public IReadOnlyCollection<string> GetAllHashKeys()
    {
        return _hashStore.GetUniqueLogicalKeys(HashPrefix).ToList().AsReadOnly();
    }

    // ── ZSet 操作（字段级 key，零读放大） ──

    /// <summary>
    /// 持久化 ZSet 的单个 member — 直接写入，无需读取整个 ZSet
    /// Bitcask key: "z\0{key}\0{member}" → score 字符串
    /// </summary>
    public Task PersistZSetMemberAsync(string key, string member, double score)
    {
        _zsetStore.Set(ZSetMemberKey(key, member), score.ToString("R"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从磁盘加载整个 ZSet — 前缀扫描所有 member，顺序 IO
    /// </summary>
    public Task<Dictionary<string, double>?> LoadZSetAsync(string key)
    {
        var prefix = $"{ZSetPrefix}{key}\0";
        var raw = _zsetStore.GetAllByPrefix(prefix);
        if (raw.Count == 0)
            return Task.FromResult<Dictionary<string, double>?>(null);

        var dict = new Dictionary<string, double>(raw.Count);
        foreach (var (fullKey, value) in raw)
        {
            var member = fullKey.Substring(prefix.Length);
            dict[member] = double.Parse(value);
        }
        return Task.FromResult<Dictionary<string, double>?>(dict);
    }

    /// <summary>
    /// 删除 ZSet 的单个 member — 直接删除，无需读取整个 ZSet
    /// </summary>
    public Task RemoveZSetMemberAsync(string key, string member)
    {
        _zsetStore.Delete(ZSetMemberKey(key, member));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除整个 ZSet — 删除所有 member key
    /// </summary>
    public Task DeleteZSetAsync(string key)
    {
        var prefix = $"{ZSetPrefix}{key}\0";
        var memberKeys = _zsetStore.GetKeysByPrefix(prefix);
        foreach (var memberKey in memberKeys)
            _zsetStore.Delete(memberKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取所有逻辑 ZSet key（从字段级 key 中提取唯一 key）
    /// </summary>
    public IReadOnlyCollection<string> GetAllZSetKeys()
    {
        return _zsetStore.GetUniqueLogicalKeys(ZSetPrefix).ToList().AsReadOnly();
    }

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
