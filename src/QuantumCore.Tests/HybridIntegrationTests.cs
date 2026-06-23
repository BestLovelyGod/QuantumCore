// ============================================================================
// 量子核（QuantumCore）— 混合存储集成测试
// 验证 Memory ↔ Disk 双引擎协同工作
// ============================================================================

using Xunit;

namespace QuantumCore.Tests;

public class HybridIntegrationTests : IDisposable
{
    private readonly HybridStore _store;
    private readonly string _tempDir;

    public HybridIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"quantumcore_integ_{Guid.NewGuid():N}");
        _store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = true
        });
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ReadThrough_MemoryToDisk()
    {
        // 写入数据
        await _store.StringSetAsync("cache_key", "cached_value");

        // 验证内存可读
        var memResult = await _store.StringGetAsync("cache_key");
        Assert.Equal("cached_value", memResult);

        // 验证 Bitcask 日志已持久化
        var bitcaskDir = Path.Combine(_tempDir, "bitcask");
        Assert.True(Directory.Exists(bitcaskDir));
        var logFile = Path.Combine(bitcaskDir, "active.log");
        Assert.True(File.Exists(logFile));
    }

    [Fact]
    public async Task LargeValue_ShouldPersist()
    {
        var largeValue = new string('X', 100_000); // 100KB
        await _store.StringSetAsync("large", largeValue);

        var result = await _store.StringGetAsync("large");
        Assert.Equal(largeValue.Length, result!.Length);
    }

    [Fact]
    public async Task ConcurrentWrites_ShouldNotCorrupt()
    {
        var tasks = Enumerable.Range(0, 100).Select(i =>
            _store.StringSetAsync($"key_{i}", $"value_{i}"));

        await Task.WhenAll(tasks);

        for (int i = 0; i < 100; i++)
        {
            var val = await _store.StringGetAsync($"key_{i}");
            Assert.Equal($"value_{i}", val);
        }
    }

    [Fact]
    public async Task MixedOperations_ShouldWork()
    {
        // 同时操作 String、Hash、ZSet
        await _store.StringSetAsync("s1", "string_val");
        await _store.HashSetAsync("h1", "field", "hash_val");
        await _store.ZSetAddAsync("z1", "member", 42.5);

        Assert.Equal("string_val", await _store.StringGetAsync("s1"));
        Assert.Equal("hash_val", await _store.HashGetAsync("h1", "field"));
        Assert.Equal(42.5, await _store.ZSetScoreAsync("z1", "member"));
    }

    [Fact]
    public async Task Delete_ShouldRemoveFromMemoryAndDisk()
    {
        await _store.StringSetAsync("to_delete", "value");
        await _store.StringDeleteAsync("to_delete");

        Assert.Null(await _store.StringGetAsync("to_delete"));
        Assert.False(await _store.KeyExistsAsync("to_delete"));
    }
}
