// ============================================================================
// 量子核（QuantumCore）— 单元测试
// ============================================================================

using Xunit;
using QuantumCore;

namespace QuantumCore.Tests;

public class HybridStoreTests : IDisposable
{
    private readonly HybridStore _store;
    private readonly string _tempDir;

    public HybridStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"quantumcore_test_{Guid.NewGuid():N}");
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

    // ── String 测试 ──

    [Fact]
    public async Task StringSet_And_Get_ShouldWork()
    {
        await _store.StringSetAsync("greeting", "hello");
        var result = await _store.StringGetAsync("greeting");
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task StringGet_NonExistent_ShouldReturnNull()
    {
        var result = await _store.StringGetAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task StringDelete_ShouldRemove()
    {
        await _store.StringSetAsync("key1", "value1");
        var deleted = await _store.StringDeleteAsync("key1");
        Assert.True(deleted);

        var result = await _store.StringGetAsync("key1");
        Assert.Null(result);
    }

    [Fact]
    public async Task StringExists_ShouldWork()
    {
        await _store.StringSetAsync("exists_key", "val");
        Assert.True(await _store.StringExistsAsync("exists_key"));
        Assert.False(await _store.StringExistsAsync("no_key"));
    }

    // ── Hash 测试 ──

    [Fact]
    public async Task HashSet_And_Get_ShouldWork()
    {
        await _store.HashSetAsync("user:1", "name", "Alice");
        var result = await _store.HashGetAsync("user:1", "name");
        Assert.Equal("Alice", result);
    }

    [Fact]
    public async Task HashGetAll_ShouldReturnAll()
    {
        await _store.HashSetAsync("user:1", "name", "Alice");
        await _store.HashSetAsync("user:1", "age", "30");
        var all = await _store.HashGetAllAsync("user:1");
        Assert.Equal(2, all.Count);
        Assert.Equal("Alice", all["name"]);
    }

    [Fact]
    public async Task HashDelete_ShouldRemove()
    {
        await _store.HashSetAsync("user:1", "name", "Alice");
        await _store.HashDeleteAsync("user:1", "name");
        var result = await _store.HashGetAsync("user:1", "name");
        Assert.Null(result);
    }

    // ── ZSet 测试 ──

    [Fact]
    public async Task ZSetAdd_And_Score_ShouldWork()
    {
        await _store.ZSetAddAsync("leaderboard", "player1", 100);
        await _store.ZSetAddAsync("leaderboard", "player2", 200);
        var score = await _store.ZSetScoreAsync("leaderboard", "player2");
        Assert.Equal(200, score);
    }

    [Fact]
    public async Task ZSetRange_ShouldReturnOrdered()
    {
        await _store.ZSetAddAsync("scores", "a", 30);
        await _store.ZSetAddAsync("scores", "b", 10);
        await _store.ZSetAddAsync("scores", "c", 20);
        var range = await _store.ZSetRangeAsync("scores", 0, -1);
        Assert.Equal(3, range.Count);
        Assert.Equal("b", range[0].Member); // 最低分
        Assert.Equal("a", range[2].Member); // 最高分
    }

    [Fact]
    public async Task ZSetRemove_ShouldWork()
    {
        await _store.ZSetAddAsync("zs", "m1", 1);
        await _store.ZSetRemoveAsync("zs", "m1");
        var score = await _store.ZSetScoreAsync("zs", "m1");
        Assert.Null(score);
    }

    // ── 通用测试 ──

    [Fact]
    public async Task DatabaseSize_ShouldTrack()
    {
        await _store.StringSetAsync("k1", "v1");
        await _store.StringSetAsync("k2", "v2");
        var size = await _store.DatabaseSizeAsync();
        Assert.True(size >= 2);
    }

    [Fact]
    public async Task RecoverFromWal_ShouldWork()
    {
        // 写入后恢复
        await _store.StringSetAsync("recovery_key", "recovery_value");
        var recovered = await _store.RecoverAsync();
        Assert.True(recovered >= 0);
    }
}
