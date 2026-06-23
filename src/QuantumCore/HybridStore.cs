// ============================================================================
// 量子核（QuantumCore）— 混合存储实现
// 统一调度 Memory Engine 和 Disk Engine
//
// 写入模式：
//   Soft Write（默认）：先写内存 → 后台异步刷盘（高性能）
//   Hard Write（显式）：先写磁盘 → 再写内存（数据安全）
//
// 持久化：
//   String — Bitcask 原生存储
//   Hash   — JSON 序列化，key 前缀 "hash:"
//   ZSet   — JSON 序列化，key 前缀 "zset:"
// ============================================================================

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using QuantumCore.Disk;
using QuantumCore.Memory;

namespace QuantumCore;

/// <summary>
/// 脏数据条目类型
/// </summary>
internal enum DirtyEntryType : byte
{
    String = 1,
    Hash = 2,
    ZSet = 3,
    Delete = 0xFF
}

/// <summary>
/// 脏数据条目
/// </summary>
internal readonly record struct DirtyEntry(
    DirtyEntryType Type,
    string Key,
    string? Field,   // Hash 用
    string? Value,   // String/Hash 用
    double? Score,   // ZSet 用
    bool Remove      // true = 删除操作
);

/// <summary>
/// 混合存储引擎 — 统一调度内存引擎和磁盘引擎
/// 热数据自动缓存到内存，冷数据持久化到磁盘
///
/// WAL 保护：所有写操作先追加到 WAL，再写内存
/// 刷盘成功后截断 WAL；崩溃后重放 WAL 恢复数据
/// </summary>
public sealed class HybridStore : IHybridStore
{
    private readonly QuantumCoreOptions _options;
    private readonly MemoryEngine _memory;
    private readonly DiskEngine _disk;

    // ── WAL 预写日志 ──
    private readonly WalLog _wal;

    // ── 后台刷盘 ──
    private readonly ConcurrentQueue<DirtyEntry> _dirtyQueue = new();
    private readonly ConcurrentDictionary<string, byte> _deletedKeys = new();
    private readonly Timer _flushTimer;
    private bool _disposed;
    private int _flushInProgress;
    private Task? _backgroundFlushTask;
    private readonly object _flushTaskLock = new();

    // ── JSON 序列化选项 ──
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public HybridStore(QuantumCoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _memory = new MemoryEngine(options);
        _disk = new DiskEngine(options);

        // 初始化 WAL
        _wal = new WalLog(options.DataDirectory);

        // 后台定时刷盘（默认每秒一次）
        _flushTimer = new Timer(FlushDirtyQueue, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    // ── String 操作（Soft Write：先 WAL → 内存，后台刷盘） ──

    public Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null)
    {
        // WAL: 先写日志
        if (_options.WalEnabled)
            _wal.Append(new WalEntry(WalOp.StringSet, key, null, value, 0));

        _memory.StringSet(key, value, expiry);
        _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.String, key, null, value, null, false));
        return Task.FromResult(true);
    }

    // ── String 操作（Hard Write：先 WAL → 落盘，再写内存） ──

    public async Task<bool> StringSetHardAsync(string key, string value, TimeSpan? expiry = null)
    {
        // WAL: 先写日志
        if (_options.WalEnabled)
            _wal.Append(new WalEntry(WalOp.StringSet, key, null, value, 0));

        await _disk.PersistStringAsync(key, value);
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
        value = await _disk.LoadStringAsync(key);
        if (value != null)
        {
            // 回填内存缓存
            _memory.StringSet(key, value);
        }
        return value;
    }

    public async Task<bool> StringDeleteAsync(string key)
    {
        // WAL: 先写日志
        if (_options.WalEnabled)
            _wal.Append(new WalEntry(WalOp.StringDelete, key, null, null, 0));

        _memory.StringDelete(key);
        MarkDeleted($"str:{key}");
        await _disk.DeleteStringAsync(key);
        return true;
    }

    public Task<bool> StringExistsAsync(string key)
    {
        // 先查内存（无 IO 开销），命中则直接返回，避免双重 IO
        if (_memory.StringExists(key))
            return Task.FromResult(true);

        // 内存未命中再查磁盘
        return _disk.ExistsStringAsync(key);
    }

    // ── Hash 操作（Soft Write：先 WAL → 内存，后台刷盘） ──

    public Task<bool> HashSetAsync(string key, string field, string value)
    {
        // WAL: 先写日志
        if (_options.WalEnabled)
            _wal.Append(new WalEntry(WalOp.HashSet, key, field, value, 0));

        _memory.HashSet(key, field, value);
        _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.Hash, key, field, value, null, false));
        return Task.FromResult(true);
    }

    public async Task<string?> HashGetAsync(string key, string field)
    {
        var value = _memory.HashGet(key, field);
        if (value != null) return value;

        // 内存未命中，从磁盘加载整个 Hash 并回填
        var allFields = await _disk.LoadHashAsync(key);
        if (allFields != null && allFields.Count > 0)
        {
            foreach (var kv in allFields)
                _memory.HashSet(key, kv.Key, kv.Value);
            return _memory.HashGet(key, field);
        }
        return null;
    }

    public async Task<bool> HashDeleteAsync(string key, string field)
    {
        var existed = _memory.HashDelete(key, field);
        if (existed)
        {
            // WAL: 先写日志
            if (_options.WalEnabled)
                _wal.Append(new WalEntry(WalOp.HashDelete, key, field, null, 0));

            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.Hash, key, field, null, null, true));
            // 如果整个 Hash 空了，标记删除磁盘 key
            var remaining = _memory.HashGetAll(key);
            if (remaining.Count == 0)
            {
                MarkDeleted($"hash:{key}");
                await _disk.DeleteHashAsync(key);
            }
        }
        return existed;
    }

    public async Task<Dictionary<string, string>> HashGetAllAsync(string key)
    {
        var result = _memory.HashGetAll(key);
        if (result.Count > 0) return result;

        // 内存未命中，从磁盘加载
        var allFields = await _disk.LoadHashAsync(key);
        if (allFields != null)
        {
            foreach (var kv in allFields)
                _memory.HashSet(key, kv.Key, kv.Value);
            return _memory.HashGetAll(key);
        }
        return new();
    }

    // ── ZSet 操作（Soft Write：先 WAL → 内存，后台刷盘） ──

    public Task<bool> ZSetAddAsync(string key, string member, double score)
    {
        // WAL: 先写日志
        if (_options.WalEnabled)
            _wal.Append(new WalEntry(WalOp.ZSetAdd, key, member, null, score));

        _memory.ZSetAdd(key, member, score);
        _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.ZSet, key, member, null, score, false));
        return Task.FromResult(true);
    }

    public async Task<double?> ZSetScoreAsync(string key, string member)
    {
        var score = _memory.ZSetScore(key, member);
        if (score != null) return score;

        // 内存未命中，从磁盘加载整个 ZSet 并回填
        var allMembers = await _disk.LoadZSetAsync(key);
        if (allMembers != null && allMembers.Count > 0)
        {
            foreach (var kv in allMembers)
                _memory.ZSetAdd(key, kv.Key, kv.Value);
            return _memory.ZSetScore(key, member);
        }
        return null;
    }

    public async Task<List<(string Member, double Score)>> ZSetRangeAsync(string key, int start, int stop)
    {
        // 先确保内存有数据
        var score = _memory.ZSetScore(key, "__probe__");
        if (score == null)
        {
            var allMembers = await _disk.LoadZSetAsync(key);
            if (allMembers != null && allMembers.Count > 0)
            {
                foreach (var kv in allMembers)
                    _memory.ZSetAdd(key, kv.Key, kv.Value);
            }
        }
        return _memory.ZSetRange(key, start, stop);
    }

    public async Task<bool> ZSetRemoveAsync(string key, string member)
    {
        var existed = _memory.ZSetRemove(key, member);
        if (existed)
        {
            // WAL: 先写日志
            if (_options.WalEnabled)
                _wal.Append(new WalEntry(WalOp.ZSetRemove, key, member, null, 0));

            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.ZSet, key, member, null, null, true));
            // 如果整个 ZSet 空了，标记删除磁盘 key
            var remaining = _memory.ZSetScore(key, "__probe__");
            if (remaining == null)
            {
                MarkDeleted($"zset:{key}");
                await _disk.DeleteZSetAsync(key);
            }
        }
        return existed;
    }

    // ── 通用 ──

    public async Task<bool> KeyDeleteAsync(string key)
    {
        _memory.StringDelete(key);
        MarkDeleted($"str:{key}");
        MarkDeleted($"hash:{key}");
        MarkDeleted($"zset:{key}");
        await _disk.DeleteStringAsync(key);
        await _disk.DeleteHashAsync(key);
        await _disk.DeleteZSetAsync(key);
        return true;
    }

    public Task<bool> KeyExistsAsync(string key)
    {
        if (_memory.StringExists(key) ||
            _memory.HashGetAll(key).Count > 0 ||
            _memory.ZSetScore(key, "__probe__") != null)
            return Task.FromResult(true);

        return _disk.ExistsStringAsync(key);
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
    /// 启动时从磁盘恢复数据，并重放 WAL 未提交条目
    /// </summary>
    public async Task<int> RecoverAsync()
    {
        var restored = 0;

        // 1. 从 Bitcask 恢复 Hash 数据
        foreach (var key in _disk.GetAllHashKeys())
        {
            if (_deletedKeys.ContainsKey($"hash:{key}")) continue;
            var fields = await _disk.LoadHashAsync(key);
            if (fields != null)
            {
                foreach (var kv in fields)
                    _memory.HashSet(key, kv.Key, kv.Value);
                restored++;
            }
        }

        // 2. 从 Bitcask 恢复 ZSet 数据
        foreach (var key in _disk.GetAllZSetKeys())
        {
            if (_deletedKeys.ContainsKey($"zset:{key}")) continue;
            var members = await _disk.LoadZSetAsync(key);
            if (members != null)
            {
                foreach (var kv in members)
                    _memory.ZSetAdd(key, kv.Key, kv.Value);
                restored++;
            }
        }

        // 3. 重放 WAL 未提交条目（崩溃恢复核心逻辑）
        if (_options.WalEnabled && _wal.HasUncommittedData)
        {
            var walEntries = _wal.Replay();
            foreach (var entry in walEntries)
            {
                switch (entry.Op)
                {
                    case WalOp.StringSet:
                        if (entry.Value != null)
                        {
                            _memory.StringSet(entry.Key, entry.Value);
                            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.String, entry.Key, null, entry.Value, null, false));
                        }
                        break;
                    case WalOp.StringDelete:
                        _memory.StringDelete(entry.Key);
                        _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.String, entry.Key, null, null, null, true));
                        break;
                    case WalOp.HashSet:
                        if (entry.Field != null && entry.Value != null)
                        {
                            _memory.HashSet(entry.Key, entry.Field, entry.Value);
                            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.Hash, entry.Key, entry.Field, entry.Value, null, false));
                        }
                        break;
                    case WalOp.HashDelete:
                        if (entry.Field != null)
                        {
                            _memory.HashDelete(entry.Key, entry.Field);
                            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.Hash, entry.Key, entry.Field, null, null, true));
                        }
                        break;
                    case WalOp.ZSetAdd:
                        if (entry.Field != null)
                        {
                            _memory.ZSetAdd(entry.Key, entry.Field, entry.Score);
                            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.ZSet, entry.Key, entry.Field, null, entry.Score, false));
                        }
                        break;
                    case WalOp.ZSetRemove:
                        if (entry.Field != null)
                        {
                            _memory.ZSetRemove(entry.Key, entry.Field);
                            _dirtyQueue.Enqueue(new DirtyEntry(DirtyEntryType.ZSet, entry.Key, entry.Field, null, null, true));
                        }
                        break;
                }
                restored++;
            }
        }

        return restored;
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
                    while (_dirtyQueue.TryDequeue(out var entry))
                    {
                        if (_disposed) break;

                        // 跳过已标记删除的 key
                        var tag = entry.Type switch
                        {
                            DirtyEntryType.String => $"str:{entry.Key}",
                            DirtyEntryType.Hash => $"hash:{entry.Key}",
                            DirtyEntryType.ZSet => $"zset:{entry.Key}",
                            _ => entry.Key
                        };
                        if (_deletedKeys.ContainsKey(tag)) continue;

                        // 按类型刷盘
                        switch (entry.Type)
                        {
                            case DirtyEntryType.String:
                                if (entry.Remove)
                                    await _disk.DeleteStringAsync(entry.Key);
                                else if (entry.Value != null)
                                    await _disk.PersistStringAsync(entry.Key, entry.Value);
                                break;
                            case DirtyEntryType.Hash:
                                if (entry.Remove)
                                    await _disk.RemoveHashFieldAsync(entry.Key, entry.Field!);
                                else
                                    await _disk.PersistHashFieldAsync(entry.Key, entry.Field!, entry.Value!);
                                break;
                            case DirtyEntryType.ZSet:
                                if (entry.Remove)
                                    await _disk.RemoveZSetMemberAsync(entry.Key, entry.Field!);
                                else
                                    await _disk.PersistZSetMemberAsync(entry.Key, entry.Field!, entry.Score!.Value);
                                break;
                        }
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
    /// 刷完后截断 WAL，标记数据已持久化
    /// </summary>
    public async Task FlushNowAsync()
    {
        while (_dirtyQueue.TryDequeue(out var entry))
        {
            var tag = entry.Type switch
            {
                DirtyEntryType.String => $"str:{entry.Key}",
                DirtyEntryType.Hash => $"hash:{entry.Key}",
                DirtyEntryType.ZSet => $"zset:{entry.Key}",
                _ => entry.Key
            };
            if (_deletedKeys.ContainsKey(tag)) continue;

            switch (entry.Type)
            {
                case DirtyEntryType.String:
                    if (entry.Value != null)
                        await _disk.PersistStringAsync(entry.Key, entry.Value);
                    break;
                case DirtyEntryType.Hash:
                    if (entry.Field != null && entry.Value != null)
                        await _disk.PersistHashFieldAsync(entry.Key, entry.Field, entry.Value);
                    break;
                case DirtyEntryType.ZSet:
                    if (entry.Field != null && entry.Score != null)
                        await _disk.PersistZSetMemberAsync(entry.Key, entry.Field, entry.Score.Value);
                    break;
            }
        }

        // 刷盘完成，截断 WAL
        if (_options.WalEnabled)
            _wal.Truncate();
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

            // 3. 刷完所有剩余脏数据并截断 WAL
            try
            {
                FlushNowAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch
            {
                // Dispose 不应抛出异常
            }

            // 4. 释放 WAL 资源
            try
            {
                _wal?.Dispose();
            }
            catch
            {
            }

            // 5. 释放磁盘资源
            _disk.Dispose();

            Interlocked.Exchange(ref _flushInProgress, 0);
        }
    }
}
