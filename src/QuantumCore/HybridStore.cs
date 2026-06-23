// ============================================================================
// 量子核（QuantumCore）— 混合存储实现
// 统一调度 Memory Engine 和 Disk Engine
//
// 写入模式：
//   Soft Write（默认）：先写内存 → 后台异步刷盘（高性能）
//   Hard Write（显式）：先写磁盘 → 再写内存（数据安全）
// ============================================================================

using System.Collections.Concurrent;
using System.Threading;
using QuantumCore.Disk;
using QuantumCore.Memory;

namespace QuantumCore;

/// <summary>
/// 混合存储引擎 — 统一调度内存引擎和磁盘引擎
/// 热数据自动缓存到内存，冷数据持久化到磁盘
/// </summary>
public sealed class HybridStore : IHybridStore
{
    private readonly QuantumCoreOptions _options;
    private readonly MemoryEngine _memory;
    private readonly DiskEngine _disk;

    // ── 后台刷盘 ──
    private readonly ConcurrentQueue<(string Key, string Value)> _dirtyQueue = new();
    private readonly ConcurrentDictionary<string, byte> _deletedKeys = new();
    private readonly Timer _flushTimer;
    private bool _disposed;
    private int _flushInProgress;
    private Task? _backgroundFlushTask;
    private readonly object _flushTaskLock = new();

    public HybridStore(QuantumCoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _memory = new MemoryEngine(options);
        _disk = new DiskEngine(options);

        // 后台定时刷盘（默认每秒一次）
        _flushTimer = new Timer(FlushDirtyQueue, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    // ── String 操作（Soft Write：先内存，后台刷盘） ──

    public Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null)
    {
        _memory.StringSet(key, value, expiry);
        _dirtyQueue.Enqueue((key, value));
        return Task.FromResult(true);
    }

    // ── String 操作（Hard Write：先落盘，再写内存） ──

    public async Task<bool> StringSetHardAsync(string key, string value, TimeSpan? expiry = null)
    {
        await _disk.PersistAsync(key, value);
        _memory.StringSet(key, value, expiry);
        return true;
    }

    // ── 读取（统一：内存优先，穿透到磁盘） ──

    public async Task<string?> StringGetAsync(string key)
    {
        // 优先从内存读取
        var value = _memory.StringGet(key);
        if (value != null) return value;

        // 内存未命中，从磁盘加载
        value = await _disk.LoadAsync(key);
        if (value != null)
        {
            // 回填内存缓存
            _memory.StringSet(key, value);
        }
        return value;
    }

    public async Task<bool> StringDeleteAsync(string key)
    {
        _memory.StringDelete(key);
        MarkDeleted(key); // 标记已删除，防止刷盘时复活
        await _disk.DeleteAsync(key);
        return true;
    }

    public Task<bool> StringExistsAsync(string key)
    {
        // 先查内存（无 IO 开销），命中则直接返回，避免双重 IO
        if (_memory.StringExists(key))
            return Task.FromResult(true);

        // 内存未命中再查磁盘
        return _disk.ExistsAsync(key);
    }

    // ── Hash 操作 ──

    public Task<bool> HashSetAsync(string key, string field, string value)
    {
        _memory.HashSet(key, field, value);
        return Task.FromResult(true);
    }

    public Task<string?> HashGetAsync(string key, string field)
    {
        return Task.FromResult(_memory.HashGet(key, field));
    }

    public Task<bool> HashDeleteAsync(string key, string field)
    {
        return Task.FromResult(_memory.HashDelete(key, field));
    }

    public Task<Dictionary<string, string>> HashGetAllAsync(string key)
    {
        return Task.FromResult(_memory.HashGetAll(key));
    }

    // ── ZSet 操作 ──

    public Task<bool> ZSetAddAsync(string key, string member, double score)
    {
        return Task.FromResult(_memory.ZSetAdd(key, member, score));
    }

    public Task<double?> ZSetScoreAsync(string key, string member)
    {
        return Task.FromResult(_memory.ZSetScore(key, member));
    }

    public Task<List<(string Member, double Score)>> ZSetRangeAsync(string key, int start, int stop)
    {
        return Task.FromResult(_memory.ZSetRange(key, start, stop));
    }

    public Task<bool> ZSetRemoveAsync(string key, string member)
    {
        return Task.FromResult(_memory.ZSetRemove(key, member));
    }

    // ── 通用 ──

    public async Task<bool> KeyDeleteAsync(string key)
    {
        _memory.StringDelete(key);
        MarkDeleted(key); // 标记已删除，防止刷盘时复活
        await _disk.DeleteAsync(key);
        return true;
    }

    public Task<bool> KeyExistsAsync(string key)
    {
        if (_memory.StringExists(key))
            return Task.FromResult(true);

        return _disk.ExistsAsync(key);
    }

    public Task<bool> KeyExpireAsync(string key, TimeSpan expiry)
    {
        var value = _memory.StringGet(key);
        if (value == null)
            return Task.FromResult(false);

        // 重新创建带新 TTL 的条目
        _memory.StringDelete(key);
        _memory.StringSet(key, value, expiry);
        return Task.FromResult(true);
    }

    public Task<long> DatabaseSizeAsync()
    {
        return Task.FromResult(_memory.Count);
    }

    public Task<List<string>> PrefixSearchAsync(string prefix)
    {
        var keys = _memory.GetKeysByPrefix(prefix);
        return Task.FromResult(keys);
    }

    // ── 淘汰策略 ──

    /// <summary>
    /// 获取淘汰统计
    /// </summary>
    public long EvictionCount => _memory.EvictionCount;

    /// <summary>
    /// 批量淘汰 key
    /// </summary>
    public int EvictBatch(int count, EvictionPolicy? policy = null)
    {
        return _memory.EvictBatch(count, policy);
    }

    /// <summary>
    /// 清理所有过期 key
    /// </summary>
    public int CleanExpired()
    {
        return _memory.CleanExpired();
    }

    /// <summary>
    /// 获取内存使用估算（字节）
    /// </summary>
    public long EstimateMemoryUsage()
    {
        return _memory.EstimateMemoryUsage();
    }

    // ── 崩溃恢复 ──

    /// <summary>
    /// Bitcask 启动时自动重放日志重建索引
    /// </summary>
    public async Task<int> RecoverAsync()
    {
        // Bitcask 在构造时已自动重建索引
        await Task.CompletedTask;
        return 0;
    }

    // ── 压缩合并 ──

    /// <summary>
    /// 压缩合并日志文件，删除过期数据
    /// </summary>
    public async Task CompactAsync()
    {
        await FlushNowAsync(); // 先刷完脏数据
        await _disk.CompactAsync();
    }

    // ── 后台刷盘 ──

    /// <summary>
    /// 定时刷盘回调：从 ConcurrentQueue 逐条取出并异步写盘
    /// </summary>
    private void FlushDirtyQueue(object? state)
    {
        if (_disposed) return;

        if (Interlocked.CompareExchange(ref _flushInProgress, 1, 0) != 0)
            return;

        lock (_flushTaskLock)
        {
            _backgroundFlushTask = Task.Run(async () =>
            {
                try
                {
                    while (_dirtyQueue.TryDequeue(out var item))
                    {
                        if (_disposed) break;
                        if (_deletedKeys.ContainsKey(item.Key)) continue;
                        await _disk.PersistAsync(item.Key, item.Value);
                    }
                }
                catch
                {
                    // 刷盘异常不应导致进程崩溃
                }
                finally
                {
                    Interlocked.Exchange(ref _flushInProgress, 0);
                }
            });
        }
    }

    /// <summary>
    /// 立即刷完所有脏数据（用于关闭前或硬写前）
    /// </summary>
    public async Task FlushNowAsync()
    {
        while (_dirtyQueue.TryDequeue(out var item))
        {
            if (_deletedKeys.ContainsKey(item.Key)) continue;
            await _disk.PersistAsync(item.Key, item.Value);
        }
    }

    /// <summary>
    /// 标记 key 为已删除，防止后续刷盘时复活已删除的数据
    /// </summary>
    private void MarkDeleted(string key)
    {
        _deletedKeys[key] = 0;
    }

    /// <summary>
    /// 清除已删除标记（在 FlushNowAsync 完成后调用）
    /// </summary>
    private void ClearDeletedMarks()
    {
        _deletedKeys.Clear();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // 1. 停止定时器（不再触发新回调）
            _flushTimer.Dispose();

            // 2. 等待后台刷盘任务完成（有限等待，避免死锁）
            try
            {
                lock (_flushTaskLock)
                {
                    _backgroundFlushTask?.Wait(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // 等待超时或异常时不阻塞 Dispose
            }

            // 3. 刷完所有剩余脏数据
            try
            {
                FlushNowAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch
            {
                // Dispose 不应抛出异常
            }

            // 4. 释放磁盘资源
            _disk.Dispose();

            Interlocked.Exchange(ref _flushInProgress, 0);
        }
    }
}
