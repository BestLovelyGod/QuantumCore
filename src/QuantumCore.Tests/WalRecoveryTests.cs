// ============================================================================
// 量子核（QuantumCore）— Bitcask 崩溃恢复测试
// ============================================================================

using Xunit;

namespace QuantumCore.Tests;

public class WalRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public WalRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"quantumcore_wal_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Restart_ShouldRebuildIndex()
    {
        // 第一个实例：写入数据
        var store1 = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = true
        });

        await store1.StringSetAsync("wal_key1", "wal_value1");
        await store1.StringSetAsync("wal_key2", "wal_value2");
        store1.Dispose();

        // 第二个实例：模拟重启，Bitcask 自动重建索引
        var store2 = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = true
        });

        var val1 = await store2.StringGetAsync("wal_key1");
        var val2 = await store2.StringGetAsync("wal_key2");
        Assert.Equal("wal_value1", val1);
        Assert.Equal("wal_value2", val2);

        store2.Dispose();
    }

    [Fact]
    public async Task Delete_ShouldPersist()
    {
        var store1 = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = true
        });

        await store1.StringSetAsync("del_key", "del_value");
        await store1.StringDeleteAsync("del_key");
        store1.Dispose();

        // 重启后删除应生效
        var store2 = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = true
        });

        var val = await store2.StringGetAsync("del_key");
        Assert.Null(val);

        store2.Dispose();
    }

    [Fact]
    public async Task BitcaskDirectory_ShouldExist()
    {
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = true
        });

        await store.StringSetAsync("test", "value");

        var bitcaskDir = Path.Combine(_tempDir, "bitcask");
        Assert.True(Directory.Exists(bitcaskDir));

        var logFile = Path.Combine(bitcaskDir, "active.log");
        Assert.True(File.Exists(logFile));

        store.Dispose();
    }
}
