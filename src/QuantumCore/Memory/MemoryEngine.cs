// ============================================================================
// 量子核（QuantumCore）— 内存引擎骨架
// 类 Redis 的内存 KV 存储，纳秒级访问
// ============================================================================

using System.Collections.Concurrent;
using System.Threading;

namespace QuantumCore.Memory;

/// <summary>
/// 内存引擎 — 热数据存储
/// 提供 String / Hash / ZSet 数据结构，支持 LRU/LFU/TTL 淘汰
/// 线程安全：使用 ConcurrentDictionary + 读写锁
/// </summary>
internal sealed class MemoryEngine
{
    private readonly QuantumCoreOptions _options;

    // ── String 存储（线程安全） ──
    private readonly ConcurrentDictionary<string, StringEntry> _strings = new();
    // ── Hash 存储（外层线程安全，内层单 key 加锁） ──
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _hashes = new();
    // ── ZSet 存储（外层线程安全，内层单 key 加锁） ──
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, double>> _zsets = new();

    // ── 淘汰统计 ──
    private long _evictionCount;
    private long _writeCount;
    private long _keyCount; // 手动维护 key 数量，避免 ConcurrentDictionary.Count 的 O(n) 遍历

    public long EvictionCount => Volatile.Read(ref _evictionCount);

    public MemoryEngine(QuantumCoreOptions options)
    {
        _options = options;
    }

    // ── String 操作 ──
    public bool StringSet(string key, string value, TimeSpan? expiry = null)
    {
        // 检查内存上限，达到上限时批量淘汰
        var count = Volatile.Read(ref _keyCount);
        if (count >= _options.MaxKeyCount)
        {
            var evictCount = _options.MaxKeyCount < 100
                ? 1
                : Math.Max(1, _options.MaxKeyCount / 10);
            EvictBatch(evictCount, null);
        }

        // 新 key 时递增计数（旧 key 覆盖不递增）
        var isNew = !_strings.ContainsKey(key);
        _strings[key] = new StringEntry(value, expiry);
        if (isNew) Interlocked.Increment(ref _keyCount);

        // 每 100 次写入清理一次过期 key（延迟到写入后，不阻塞热路径）
        if (Volatile.Read(ref _writeCount) % 100 == 0)
        {
            Task.Run(() => CleanExpired());
        }
        Interlocked.Increment(ref _writeCount);

        return true;
    }

    // ── 淘汰逻辑 ──
    private void Evict(EvictionPolicy policy)
    {
        switch (policy)
        {
            case EvictionPolicy.LRU:
                EvictLRU();
                break;
            case EvictionPolicy.LFU:
                EvictLFU();
                break;
            case EvictionPolicy.FIFO:
                EvictFIFO();
                break;
        }
        Interlocked.Increment(ref _evictionCount);
    }

    /// <summary>
    /// LRU 淘汰：移除最近最少访问的 key
    /// </summary>
    private void EvictLRU()
    {
        var oldest = _strings
            .Where(kv => !kv.Value.IsExpired)
            .OrderBy(kv => kv.Value.LastAccessed)
            .FirstOrDefault();

        if (oldest.Key != null)
        {
            _strings.TryRemove(oldest.Key, out _);
        }
    }

    /// <summary>
    /// LFU 淘汰：移除访问次数最少的 key
    /// </summary>
    private void EvictLFU()
    {
        var leastFrequent = _strings
            .Where(kv => !kv.Value.IsExpired)
            .OrderBy(kv => kv.Value.AccessCount)
            .ThenBy(kv => kv.Value.LastAccessed)  // 次数相同时，按 LRU 处理
            .FirstOrDefault();

        if (leastFrequent.Key != null)
        {
            _strings.TryRemove(leastFrequent.Key, out _);
        }
    }

    /// <summary>
    /// FIFO 淘汰：移除最早写入的 key
    /// </summary>
    private void EvictFIFO()
    {
        var oldest = _strings
            .Where(kv => !kv.Value.IsExpired)
            .OrderBy(kv => kv.Value.CreatedAt)
            .FirstOrDefault();

        if (oldest.Key != null)
        {
            _strings.TryRemove(oldest.Key, out _);
        }
    }

    /// <summary>
    /// 淘汰指定数量的 key（优先淘汰过期 key，再按策略淘汰）
    /// </summary>
    public int EvictBatch(int count, EvictionPolicy? policy = null)
    {
        var effectivePolicy = policy ?? _options.EvictionPolicy;
        var evicted = 0;

        // 第一步：优先淘汰过期 key
        var expiredKeys = _strings
            .Where(kv => kv.Value.IsExpired)
            .Select(kv => kv.Key)
            .Take(count)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_strings.TryRemove(key, out _))
                evicted++;
        }

        // 第二步：如果还不够，按策略淘汰非过期 key
        while (evicted < count)
        {
            string? keyToRemove = effectivePolicy switch
            {
                EvictionPolicy.LRU => _strings
                    .Where(kv => !kv.Value.IsExpired)
                    .OrderBy(kv => kv.Value.LastAccessed)
                    .FirstOrDefault().Key,
                EvictionPolicy.LFU => _strings
                    .Where(kv => !kv.Value.IsExpired)
                    .OrderBy(kv => kv.Value.AccessCount)
                    .ThenBy(kv => kv.Value.LastAccessed)
                    .FirstOrDefault().Key,
                EvictionPolicy.FIFO => _strings
                    .Where(kv => !kv.Value.IsExpired)
                    .OrderBy(kv => kv.Value.CreatedAt)
                    .FirstOrDefault().Key,
                _ => null
            };

            if (keyToRemove != null && _strings.TryRemove(keyToRemove, out _))
                evicted++;
            else
                break; // 没有更多可淘汰的 key
        }

        Interlocked.Add(ref _evictionCount, evicted);
        return evicted;
    }

    /// <summary>
    /// 清理所有过期 key
    /// </summary>
    public int CleanExpired()
    {
        var expiredKeys = _strings
            .Where(kv => kv.Value.IsExpired)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _strings.TryRemove(key, out _);
        }

        return expiredKeys.Count;
    }

    /// <summary>
    /// 获取内存使用估算（字节）
    /// </summary>
    public long EstimateMemoryUsage()
    {
        long total = 0;
        
        // String 存储估算
        foreach (var kv in _strings)
        {
            total += kv.Key.Length * 2;  // UTF-16
            total += kv.Value.Value.Length * 2;
            total += 64;  // 对象开销
        }

        // Hash 存储估算
        foreach (var hash in _hashes)
        {
            total += hash.Key.Length * 2;
            foreach (var field in hash.Value)
            {
                total += field.Key.Length * 2;
                total += field.Value.Length * 2;
            }
            total += 128;  // 字典开销
        }

        // ZSet 存储估算
        foreach (var zset in _zsets)
        {
            total += zset.Key.Length * 2;
            total += zset.Value.Count * (64 + 32);  // member + score
            total += 128;  // 字典开销
        }

        return total;
    }

    public string? StringGet(string key)
    {
        if (_strings.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _strings.TryRemove(key, out _);
                return null;
            }
            entry.Access(); // 更新访问信息（LRU/LFU）
            return entry.Value;
        }
        return null;
    }

    public bool StringDelete(string key) => _strings.TryRemove(key, out _);
    public bool StringExists(string key) => StringGet(key) != null;

    // ── Hash 操作 ──
    public bool HashSet(string key, string field, string value)
    {
        var hash = _hashes.GetOrAdd(key, _ => new());
        hash[field] = value;
        return true;
    }

    public string? HashGet(string key, string field)
    {
        return _hashes.TryGetValue(key, out var hash) && hash.TryGetValue(field, out var value) ? value : null;
    }

    public bool HashDelete(string key, string field)
    {
        return _hashes.TryGetValue(key, out var hash) && hash.TryRemove(field, out _);
    }

    public Dictionary<string, string> HashGetAll(string key)
    {
        return _hashes.TryGetValue(key, out var hash) ? new Dictionary<string, string>(hash) : new();
    }

    // ── ZSet 操作 ──
    public bool ZSetAdd(string key, string member, double score)
    {
        var zset = _zsets.GetOrAdd(key, _ => new());
        zset[member] = score;
        return true;
    }

    public double? ZSetScore(string key, string member)
    {
        return _zsets.TryGetValue(key, out var zset) && zset.TryGetValue(member, out var score) ? score : null;
    }

    public List<(string Member, double Score)> ZSetRange(string key, int start, int stop)
    {
        if (!_zsets.TryGetValue(key, out var zset))
            return new();

        var items = zset.OrderBy(kv => kv.Value).ToList();
        var count = items.Count;
        if (count == 0) return new();

        // 处理负索引（-1 表示最后一个元素）
        var end = stop < 0 ? count + stop + 1 : Math.Min(stop + 1, count);
        if (end <= start) return new();

        return items.Skip(start).Take(end - start).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public bool ZSetRemove(string key, string member)
    {
        return _zsets.TryGetValue(key, out var zset) && zset.TryRemove(member, out _);
    }

    // ── 通用 ──
    /// <summary>
    /// 估算有效 key 数量（排除已过期但未清理的 key）
    /// </summary>
    public long Count => _strings.Count + _hashes.Count + _zsets.Count;
    public long ActiveCount => _strings.Count(kv => !kv.Value.IsExpired) + _hashes.Count + _zsets.Count;

    public List<string> GetKeysByPrefix(string prefix)
    {
        return _strings.Keys.Where(k => k.StartsWith(prefix)).ToList();
    }

    public void Clear()
    {
        _strings.Clear();
        _hashes.Clear();
        _zsets.Clear();
    }
}

/// <summary>
/// String 条目（含过期时间和访问追踪）
/// 线程安全：使用 Interlocked 更新访问统计
/// </summary>
internal sealed class StringEntry
{
    public string Value { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public DateTimeOffset CreatedAt { get; }

    // 使用 Volatile.Read/Write 保证跨线程可见性
    private long _accessCount;
    private long _lastAccessedTicks;

    public long AccessCount => Volatile.Read(ref _accessCount);
    public DateTimeOffset LastAccessed => new DateTimeOffset(Volatile.Read(ref _lastAccessedTicks), TimeSpan.Zero);

    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;

    public StringEntry(string value, TimeSpan? expiry)
    {
        Value = value;
        ExpiresAt = expiry.HasValue ? DateTimeOffset.UtcNow.Add(expiry.Value) : null;
        CreatedAt = DateTimeOffset.UtcNow;
        // _lastAccessedTicks 默认为 0，不需要 Volatile.Write（首次访问时再更新）
    }

    public void Access()
    {
        Interlocked.Increment(ref _accessCount);
        Volatile.Write(ref _lastAccessedTicks, DateTimeOffset.UtcNow.Ticks);
    }
}
