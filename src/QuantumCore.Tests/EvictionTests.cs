// ============================================================================
// 量子核（QuantumCore）— 淘汰策略单元测试
// ============================================================================

using Xunit;

namespace QuantumCore.Tests;

/// <summary>
/// 淘汰策略测试 — 验证 LRU/LFU/FIFO 淘汰逻辑
/// </summary>
public class EvictionTests : IDisposable
{
    private readonly string _dataDir;

    public EvictionTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"eviction-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, true);
    }

    // ── LRU 淘汰 ──

    [Fact]
    public void LRU_EvictsLeastRecentlyUsed()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 3,
            EvictionPolicy = EvictionPolicy.LRU
        });

        // 写入 3 个 key（达到上限）
        store.StringSetAsync("key1", "value1").Wait();
        store.StringSetAsync("key2", "value2").Wait();
        store.StringSetAsync("key3", "value3").Wait();

        // 访问 key1 和 key2（更新 LRU 顺序）
        store.StringGetAsync("key1").Wait();
        store.StringGetAsync("key2").Wait();

        // 写入第 4 个 key，应淘汰 key3（最近最少访问）
        store.StringSetAsync("key4", "value4").Wait();

        // key3 应被淘汰
        var result = store.StringGetAsync("key3").Result;
        Assert.Null(result);

        // key1, key2, key4 应存在
        Assert.Equal("value1", store.StringGetAsync("key1").Result);
        Assert.Equal("value2", store.StringGetAsync("key2").Result);
        Assert.Equal("value4", store.StringGetAsync("key4").Result);

        // 验证淘汰统计
        Assert.Equal(1, store.EvictionCount);

        store.Dispose();
    }

    [Fact]
    public void LRU_EvictsExpiredKeysFirst()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 3,
            EvictionPolicy = EvictionPolicy.LRU
        });

        // 写入 3 个 key，key1 和 key2 立即过期
        store.StringSetAsync("key1", "value1", TimeSpan.FromMilliseconds(1)).Wait();
        store.StringSetAsync("key2", "value2", TimeSpan.FromMilliseconds(1)).Wait();
        store.StringSetAsync("key3", "value3").Wait();

        // 等待过期
        Thread.Sleep(50);

        // 写入第 4 个 key，应淘汰过期的 key
        store.StringSetAsync("key4", "value4").Wait();

        // key3 应仍然存在（最后写入）
        Assert.Equal("value3", store.StringGetAsync("key3").Result);

        store.Dispose();
    }

    // ── LFU 淘汰 ──

    [Fact]
    public void LFU_EvictsLeastFrequent()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 3,
            EvictionPolicy = EvictionPolicy.LFU
        });

        // 写入 3 个 key
        store.StringSetAsync("key1", "value1").Wait();
        store.StringSetAsync("key2", "value2").Wait();
        store.StringSetAsync("key3", "value3").Wait();

        // 高频访问 key1 和 key2
        for (int i = 0; i < 10; i++)
        {
            store.StringGetAsync("key1").Wait();
            store.StringGetAsync("key2").Wait();
        }

        // 写入第 4 个 key，应淘汰 key3（访问次数最少）
        store.StringSetAsync("key4", "value4").Wait();

        // key3 应被淘汰
        var result = store.StringGetAsync("key3").Result;
        Assert.Null(result);

        // key1, key2, key4 应存在
        Assert.Equal("value1", store.StringGetAsync("key1").Result);
        Assert.Equal("value2", store.StringGetAsync("key2").Result);
        Assert.Equal("value4", store.StringGetAsync("key4").Result);

        store.Dispose();
    }

    // ── FIFO 淘汰 ──

    [Fact]
    public void FIFO_EvictsOldest()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 3,
            EvictionPolicy = EvictionPolicy.FIFO
        });

        // 写入 3 个 key
        store.StringSetAsync("key1", "value1").Wait();
        store.StringSetAsync("key2", "value2").Wait();
        store.StringSetAsync("key3", "value3").Wait();

        // 高频访问 key1（FIFO 不考虑访问频率）
        for (int i = 0; i < 10; i++)
        {
            store.StringGetAsync("key1").Wait();
        }

        // 写入第 4 个 key，应淘汰 key1（最早写入）
        store.StringSetAsync("key4", "value4").Wait();

        // key1 应被淘汰
        var result = store.StringGetAsync("key1").Result;
        Assert.Null(result);

        // key2, key3, key4 应存在
        Assert.Equal("value2", store.StringGetAsync("key2").Result);
        Assert.Equal("value3", store.StringGetAsync("key3").Result);
        Assert.Equal("value4", store.StringGetAsync("key4").Result);

        store.Dispose();
    }

    // ── 批量淘汰 ──

    [Fact]
    public void EvictBatch_RemovesMultipleKeys()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 10,
            EvictionPolicy = EvictionPolicy.LRU
        });

        // 写入 5 个 key
        for (int i = 1; i <= 5; i++)
        {
            store.StringSetAsync($"key{i}", $"value{i}").Wait();
        }

        // 批量淘汰 3 个
        var evicted = store.EvictBatch(3);
        Assert.Equal(3, evicted);

        // 应只剩 2 个 key
        Assert.Equal(2, store.DatabaseSizeAsync().Result);

        store.Dispose();
    }

    // ── 过期清理 ──

    [Fact]
    public void CleanExpired_RemovesExpiredKeys()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 100
        });

        // 写入一些 key，部分设置短 TTL
        store.StringSetAsync("key1", "value1", TimeSpan.FromMilliseconds(10)).Wait();
        store.StringSetAsync("key2", "value2", TimeSpan.FromMilliseconds(10)).Wait();
        store.StringSetAsync("key3", "value3").Wait();  // 永不过期

        // 等待过期
        Thread.Sleep(50);

        // 清理过期 key
        var cleaned = store.CleanExpired();
        Assert.Equal(2, cleaned);

        // key3 应仍然存在
        Assert.Equal("value3", store.StringGetAsync("key3").Result);

        store.Dispose();
    }

    // ── 内存估算 ──

    [Fact]
    public void EstimateMemoryUsage_ReturnsPositiveValue()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir
        });

        // 写入一些数据
        store.StringSetAsync("key1", "value1").Wait();
        store.StringSetAsync("key2", "value2").Wait();

        var memoryUsage = store.EstimateMemoryUsage();
        Assert.True(memoryUsage > 0);

        store.Dispose();
    }

    // ── 并发淘汰 ──

    [Fact]
    public void ConcurrentSet_WithEviction_IsThreadSafe()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _dataDir,
            MaxKeyCount = 50,
            EvictionPolicy = EvictionPolicy.LRU
        });

        // 并发写入 100 个 key（超过上限）
        var tasks = Enumerable.Range(1, 100).Select(i =>
            store.StringSetAsync($"key{i}", $"value{i}"));

        Task.WhenAll(tasks).Wait();

        // 应不超过 MaxKeyCount
        var size = store.DatabaseSizeAsync().Result;
        Assert.True(size <= 50);

        // 应有淘汰发生
        Assert.True(store.EvictionCount > 0);

        store.Dispose();
    }
}
