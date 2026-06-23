// ============================================================================
// 量子核（QuantumCore）— 磁盘引擎（Bitcask 实现）
// 顺序追加写入 + 内存索引 + 定期压缩合并
// ============================================================================

namespace QuantumCore.Disk;

/// <summary>
/// 磁盘引擎 — 基于 Bitcask 的持久化存储
/// 所有写入顺序追加到日志文件，内存中维护 key → offset 索引
/// </summary>
internal sealed class DiskEngine : IDisposable
{
    private readonly QuantumCoreOptions _options;
    private readonly Bitcask _bitcask;

    public DiskEngine(QuantumCoreOptions options)
    {
        _options = options;
        _bitcask = new Bitcask(options.DataDirectory);
    }

    /// <summary>
    /// 持久化一个键值对（顺序追加到日志）
    /// </summary>
    public Task PersistAsync(string key, string value)
    {
        _bitcask.Set(key, value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取一个键值对（通过内存索引直接定位）
    /// </summary>
    public Task<string?> LoadAsync(string key)
    {
        var value = _bitcask.Get(key);
        return Task.FromResult(value);
    }

    /// <summary>
    /// 删除一个键值对（写入 tombstone 标记）
    /// </summary>
    public Task<bool> DeleteAsync(string key)
    {
        if (_bitcask.Exists(key))
        {
            _bitcask.Delete(key);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>
    /// 检查是否存在某个键
    /// </summary>
    public Task<bool> ExistsAsync(string key)
    {
        return Task.FromResult(_bitcask.Exists(key));
    }

    /// <summary>
    /// 获取所有 key（用于范围查询）
    /// </summary>
    public IReadOnlyCollection<string> GetAllKeys()
    {
        return _bitcask.Keys;
    }

    /// <summary>
    /// 压缩合并日志文件
    /// </summary>
    public Task CompactAsync()
    {
        return _bitcask.CompactAsync();
    }

    /// <summary>
    /// 清理旧的段文件
    /// </summary>
    public void CleanupOldSegments()
    {
        _bitcask.CleanupOldSegments();
    }

    public void Dispose()
    {
        _bitcask.Dispose();
    }
}
