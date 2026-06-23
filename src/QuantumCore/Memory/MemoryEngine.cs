// ============================================================================
// 量子核（QuantumCore）— 内存引擎骨架
// 类 Redis 的内存 KV 存储，纳秒级访问
//
// 淘汰策略（O(1) 实现）：
//   LRU — 双向链表 + 哈希表，访问时移到尾部，淘汰头部
//   LFU — 频率桶 + 哈希表，minFreq 桶尾部淘汰
//   FIFO — 入队顺序链表，头部淘汰
// ============================================================================

using System.Collections.Concurrent;
using System.Threading;

namespace QuantumCore.Memory;

/// <summary>
/// 内存引擎 — 热数据存储
/// 提供 String / Hash / ZSet 数据结构，支持 LRU/LFU/TTL 淘汰
/// 线程安全：使用 ConcurrentDictionary + 读写锁
///
/// 淘汰追踪使用经典 O(1) 数据结构：
///   LRU: LinkedList + Dictionary — 访问 O(1)，淘汰 O(1)
///   LFU: 频率桶（minFreq + Dictionary<int, LinkedList>）— 访问 O(1)，淘汰 O(1)
///   FIFO: LinkedList + Dictionary — 入队 O(1)，淘汰 O(1)
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

    // ── O(1) 淘汰追踪 ──
    // LRU: 双向链表按访问时间排序，尾部最近访问，头部最久未访问
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    // LFU: 频率桶，key → (freq, node)
    private readonly Dictionary<string, (int Freq, LinkedListNode<string> Node)> _lfuNodes = new();
    private readonly Dictionary<int, LinkedList<string>> _lfuBuckets = new();
    private int _minFreq = 1;
    // FIFO: 入队顺序
    private readonly LinkedList<string> _fifoList = new();
    private readonly Dictionary<string, LinkedListNode<string>> _fifoNodes = new();
    // 淘汰锁（保护链表操作，粒度远小于 ConcurrentDictionary）
    private readonly object _evictLock = new();

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

        // 新 key 时递增计数并注册淘汰追踪（旧 key 覆盖不递增）
        var isNew = !_strings.ContainsKey(key);
        _strings[key] = new StringEntry(value, expiry);
        if (isNew)
        {
            Interlocked.Increment(ref _keyCount);
            RegisterEvictKey(key);
        }

        // 每 100 次写入清理一次过期 key（延迟到写入后，不阻塞热路径）
        if (Volatile.Read(ref _writeCount) % 100 == 0)
        {
            Task.Run(() => CleanExpired());
        }
        Interlocked.Increment(ref _writeCount);

        return true;
    }

    // ── O(1) 淘汰追踪：注册/更新/移除 ──

    /// <summary>
    /// 注册新 key 到所有淘汰追踪结构
    /// </summary>
    private void RegisterEvictKey(string key)
    {
        lock (_evictLock)
        {
            // LRU: 插入尾部（最新）
            var lruNode = _lruList.AddLast(key);
            _lruNodes[key] = lruNode;

            // LFU: 频率 1 的桶
            if (!_lfuBuckets.TryGetValue(1, out var bucket))
            {
                bucket = new LinkedList<string>();
                _lfuBuckets[1] = bucket;
            }
            var lfuNode = bucket.AddLast(key);
            _lfuNodes[key] = (1, lfuNode);
            _minFreq = 1;

            // FIFO: 插入尾部（最新）
            var fifoNode = _fifoList.AddLast(key);
            _fifoNodes[key] = fifoNode;
        }
    }

    /// <summary>
    /// 从所有淘汰追踪结构中移除 key
    /// </summary>
    private void UnregisterEvictKey(string key)
    {
        lock (_evictLock)
        {
            if (_lruNodes.TryGetValue(key, out var lruNode))
            {
                _lruList.Remove(lruNode);
                _lruNodes.Remove(key);
            }
            if (_lfuNodes.TryGetValue(key, out var lfuEntry))
            {
                if (_lfuBuckets.TryGetValue(lfuEntry.Freq, out var bucket))
                    bucket.Remove(lfuEntry.Node);
                _lfuNodes.Remove(key);
                // 清理空桶，更新 minFreq
                if (_minFreq == lfuEntry.Freq && !_lfuBuckets.ContainsKey(_minFreq))
                    _minFreq++;
            }
            if (_fifoNodes.TryGetValue(key, out var fifoNode))
            {
                _fifoList.Remove(fifoNode);
                _fifoNodes.Remove(key);
            }
        }
    }

    /// <summary>
    /// LRU 触碰：将 key 移到链表尾部（最近访问），O(1)
    /// </summary>
    private void TouchLru(string key)
    {
        lock (_evictLock)
        {
            if (_lruNodes.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddLast(node);
            }
        }
    }

    /// <summary>
    /// LFU 触碰：将 key 移到 freq+1 的桶，O(1)
    /// </summary>
    private void TouchLfu(string key)
    {
        lock (_evictLock)
        {
            if (!_lfuNodes.TryGetValue(key, out var entry)) return;

            var oldFreq = entry.Freq;
            var newFreq = oldFreq + 1;

            // 从旧桶移除
            if (_lfuBuckets.TryGetValue(oldFreq, out var oldBucket))
            {
                oldBucket.Remove(entry.Node);
                if (oldBucket.Count == 0)
                {
                    _lfuBuckets.Remove(oldFreq);
                    if (_minFreq == oldFreq)
                        _minFreq = newFreq;
                }
            }

            // 加入新桶
            if (!_lfuBuckets.TryGetValue(newFreq, out var newBucket))
            {
                newBucket = new LinkedList<string>();
                _lfuBuckets[newFreq] = newBucket;
            }
            var newNode = newBucket.AddLast(key);
            _lfuNodes[key] = (newFreq, newNode);
        }
    }

    // ── 淘汰逻辑 ──

    /// <summary>
    /// LRU 淘汰：移除链表头部（最久未访问），O(1)
    /// </summary>
    private bool EvictLRU()
    {
        string? keyToRemove = null;
        lock (_evictLock)
        {
            // 跳过已过期的 key
            var node = _lruList.First;
            while (node != null)
            {
                if (_strings.TryGetValue(node.Value, out var entry) && !entry.IsExpired)
                {
                    keyToRemove = node.Value;
                    break;
                }
                var next = node.Next;
                // 清理已过期 key 的追踪
                UnregisterEvictKeyInternal(node.Value);
                node = next;
            }
        }

        if (keyToRemove != null)
        {
            _strings.TryRemove(keyToRemove, out _);
            UnregisterEvictKey(keyToRemove);
            return true;
        }
        return false;
    }

    /// <summary>
    /// LFU 淘汰：移除 minFreq 桶尾部（同频率最久未访问），O(1)
    /// </summary>
    private bool EvictLFU()
    {
        string? keyToRemove = null;
        lock (_evictLock)
        {
            // 从 minFreq 桶开始找可淘汰的 key
            while (_lfuBuckets.TryGetValue(_minFreq, out var bucket) && bucket.Count > 0)
            {
                var node = bucket.First!;
                if (_strings.TryGetValue(node.Value, out var entry) && !entry.IsExpired)
                {
                    keyToRemove = node.Value;
                    break;
                }
                // 已过期，跳过并清理
                var next = node.Next;
                UnregisterEvictKeyInternal(node.Value);
                node = next;
            }
        }

        if (keyToRemove != null)
        {
            _strings.TryRemove(keyToRemove, out _);
            UnregisterEvictKey(keyToRemove);
            return true;
        }
        return false;
    }

    /// <summary>
    /// FIFO 淘汰：移除链表头部（最早写入），O(1)
    /// </summary>
    private bool EvictFIFO()
    {
        string? keyToRemove = null;
        lock (_evictLock)
        {
            var node = _fifoList.First;
            while (node != null)
            {
                if (_strings.TryGetValue(node.Value, out var entry) && !entry.IsExpired)
                {
                    keyToRemove = node.Value;
                    break;
                }
                var next = node.Next;
                UnregisterEvictKeyInternal(node.Value);
                node = next;
            }
        }

        if (keyToRemove != null)
        {
            _strings.TryRemove(keyToRemove, out _);
            UnregisterEvictKey(keyToRemove);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 内部移除（不递归，用于 Evict 批量清理过期 key 的追踪）
    /// </summary>
    private void UnregisterEvictKeyInternal(string key)
    {
        // 注意：调用方已持有 _evictLock，直接操作
        if (_lruNodes.TryGetValue(key, out var lruNode))
        {
            _lruList.Remove(lruNode);
            _lruNodes.Remove(key);
        }
        if (_lfuNodes.TryGetValue(key, out var lfuEntry))
        {
            if (_lfuBuckets.TryGetValue(lfuEntry.Freq, out var bucket))
                bucket.Remove(lfuEntry.Node);
            _lfuNodes.Remove(key);
        }
        if (_fifoNodes.TryGetValue(key, out var fifoNode))
        {
            _fifoList.Remove(fifoNode);
            _fifoNodes.Remove(key);
        }
    }

    /// <summary>
    /// 淘汰指定数量的 key（优先淘汰过期 key，再按策略淘汰）
    /// </summary>
    public int EvictBatch(int count, EvictionPolicy? policy = null)
    {
        var effectivePolicy = policy ?? _options.EvictionPolicy;
        var evicted = 0;

        // 第一步：优先淘汰过期 key（O(n) 但仅在触发淘汰时执行）
        var expiredKeys = _strings
            .Where(kv => kv.Value.IsExpired)
            .Select(kv => kv.Key)
            .Take(count)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_strings.TryRemove(key, out _))
            {
                UnregisterEvictKey(key);
                evicted++;
            }
        }

        // 第二步：按策略 O(1) 淘汰
        while (evicted < count)
        {
            bool evictedOne = effectivePolicy switch
            {
                EvictionPolicy.LRU => EvictLRU(),
                EvictionPolicy.LFU => EvictLFU(),
                EvictionPolicy.FIFO => EvictFIFO(),
                _ => false
            };

            if (evictedOne)
                evicted++;
            else
                break;
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
            if (_strings.TryRemove(key, out _))
            {
                UnregisterEvictKey(key);
                Interlocked.Decrement(ref _keyCount);
            }
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
                UnregisterEvictKey(key);
                return null;
            }
            entry.Access(); // 更新访问计数（LFU 统计）
            TouchLru(key);  // 移到 LRU 尾部（最近访问）
            TouchLfu(key);  // 移到 LFU 高频桶
            return entry.Value;
        }
        return null;
    }

    public bool StringDelete(string key)
    {
        var removed = _strings.TryRemove(key, out _);
        if (removed)
        {
            UnregisterEvictKey(key);
            Interlocked.Decrement(ref _keyCount);
        }
        return removed;
    }
    public bool StringExists(string key) => StringGet(key) != null;

    // ── Hash 操作 ──
    public bool HashSet(string key, string field, string value)
    {
        var hash = _hashes.GetOrAdd(key, _ =>
        {
            // 新 Hash key：注册淘汰追踪
            RegisterEvictKey(key);
            Interlocked.Increment(ref _keyCount);
            return new();
        });
        hash[field] = value;
        return true;
    }

    public string? HashGet(string key, string field)
    {
        return _hashes.TryGetValue(key, out var hash) && hash.TryGetValue(field, out var value) ? value : null;
    }

    public bool HashDelete(string key, string field)
    {
        if (_hashes.TryGetValue(key, out var hash) && hash.TryRemove(field, out _))
        {
            // 如果 field 删完了，移除整个 key
            if (hash.IsEmpty)
            {
                _hashes.TryRemove(key, out _);
                UnregisterEvictKey(key);
                Interlocked.Decrement(ref _keyCount);
            }
            return true;
        }
        return false;
    }

    public Dictionary<string, string> HashGetAll(string key)
    {
        return _hashes.TryGetValue(key, out var hash) ? new Dictionary<string, string>(hash) : new();
    }

    // ── ZSet 操作 ──
    public bool ZSetAdd(string key, string member, double score)
    {
        var zset = _zsets.GetOrAdd(key, _ =>
        {
            // 新 ZSet key：注册淘汰追踪
            RegisterEvictKey(key);
            Interlocked.Increment(ref _keyCount);
            return new();
        });
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
        if (_zsets.TryGetValue(key, out var zset) && zset.TryRemove(member, out _))
        {
            // 如果 member 删完了，移除整个 key
            if (zset.IsEmpty)
            {
                _zsets.TryRemove(key, out _);
                UnregisterEvictKey(key);
                Interlocked.Decrement(ref _keyCount);
            }
            return true;
        }
        return false;
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
        lock (_evictLock)
        {
            _lruList.Clear();
            _lruNodes.Clear();
            _lfuNodes.Clear();
            _lfuBuckets.Clear();
            _fifoList.Clear();
            _fifoNodes.Clear();
            _minFreq = 1;
        }
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
