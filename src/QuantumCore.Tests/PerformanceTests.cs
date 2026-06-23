// ============================================================================
// 量子核（QuantumCore）— 性能基准测试
// 验证纳秒级内存访问和写入吞吐量
// ============================================================================

using System.Diagnostics;
using Xunit;

namespace QuantumCore.Tests;

public class PerformanceTests : IDisposable
{
    private readonly HybridStore _store;
    private readonly string _tempDir;

    public PerformanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"quantumcore_perf_{Guid.NewGuid():N}");
        _store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = _tempDir,
            WalEnabled = false // 关闭 WAL 以测试纯性能
        });
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task StringSet_Throughput()
    {
        var count = 10_000;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
        {
            await _store.StringSetAsync($"key_{i}", $"value_{i}");
        }

        sw.Stop();
        var opsPerSec = count / sw.Elapsed.TotalSeconds;

        // 混合存储含磁盘写入，至少 1500 ops/sec
        Assert.True(opsPerSec > 1500, $"StringSet 吞吐量不足: {opsPerSec:F0} ops/sec");
    }

    [Fact]
    public async Task StringGet_Throughput()
    {
        // 预写数据
        for (int i = 0; i < 1000; i++)
        {
            await _store.StringSetAsync($"key_{i}", $"value_{i}");
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 10_000; i++)
        {
            await _store.StringGetAsync($"key_{i % 1000}");
        }

        sw.Stop();
        var opsPerSec = 10_000 / sw.Elapsed.TotalSeconds;

        // 内存优先读取，至少 5000 ops/sec
        Assert.True(opsPerSec > 5000, $"StringGet 吞吐量不足: {opsPerSec:F0} ops/sec");
    }

    [Fact]
    public async Task HashSet_Throughput()
    {
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 5000; i++)
        {
            await _store.HashSetAsync($"hash_{i % 100}", "field", $"val_{i}");
        }

        sw.Stop();
        var opsPerSec = 5000 / sw.Elapsed.TotalSeconds;

        Assert.True(opsPerSec > 1000, $"HashSet 吞吐量不足: {opsPerSec:F0} ops/sec");
    }

    [Fact]
    public async Task ZSetAdd_Throughput()
    {
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 5000; i++)
        {
            await _store.ZSetAddAsync($"zset_{i % 100}", $"member_{i}", i);
        }

        sw.Stop();
        var opsPerSec = 5000 / sw.Elapsed.TotalSeconds;

        Assert.True(opsPerSec > 1000, $"ZSetAdd 吞吐量不足: {opsPerSec:F0} ops/sec");
    }
}
