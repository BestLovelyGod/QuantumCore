// ============================================================================
// 量子核（QuantumCore）— 吞吐量基准测试
// 输出实际 ops/sec 数据
// ============================================================================

using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace QuantumCore.Tests;

public class ThroughputBenchmarks
{
    private readonly ITestOutputHelper _output;

    public ThroughputBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Benchmark_All()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"qc_bench_{Guid.NewGuid():N}");
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = tempDir,
            WalEnabled = false // 纯性能测试
        });

        try
        {
            // ── 1. StringSet ──
            var sw = Stopwatch.StartNew();
            var count = 50_000;
            for (int i = 0; i < count; i++)
                await store.StringSetAsync($"sk_{i}", $"val_{i}");
            sw.Stop();
            var setOps = count / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"StringSet:  {setOps:N0} ops/sec  ({count} ops in {sw.ElapsedMilliseconds}ms)");

            // ── 2. StringGet（热数据，纯内存） ──
            sw.Restart();
            for (int i = 0; i < 100_000; i++)
                await store.StringGetAsync($"sk_{i % count}");
            sw.Stop();
            var getHotOps = 100_000 / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"StringGet (hot):  {getHotOps:N0} ops/sec");

            // ── 3. StringGet（冷数据，穿透到 Bitcask） ──
            var coldStore = new HybridStore(new QuantumCoreOptions
            {
                DataDirectory = Path.Combine(tempDir, "cold"),
                WalEnabled = false
            });
            // 预写数据到磁盘
            for (int i = 0; i < 10_000; i++)
                await coldStore.StringSetAsync($"ck_{i}", $"cval_{i}");
            coldStore.Dispose();

            // 重新打开（内存为空，读取全部穿透到磁盘）
            var coldStore2 = new HybridStore(new QuantumCoreOptions
            {
                DataDirectory = Path.Combine(tempDir, "cold"),
                WalEnabled = false
            });
            sw.Restart();
            for (int i = 0; i < 10_000; i++)
                await coldStore2.StringGetAsync($"ck_{i}");
            sw.Stop();
            var getColdOps = 10_000 / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"StringGet (cold):  {getColdOps:N0} ops/sec");
            coldStore2.Dispose();

            // ── 4. HashSet ──
            sw.Restart();
            for (int i = 0; i < 20_000; i++)
                await store.HashSetAsync($"hash_{i % 100}", "f", $"v{i}");
            sw.Stop();
            var hashOps = 20_000 / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"HashSet:     {hashOps:N0} ops/sec");

            // ── 5. ZSetAdd ──
            sw.Restart();
            for (int i = 0; i < 20_000; i++)
                await store.ZSetAddAsync($"zset_{i % 100}", $"m{i}", i);
            sw.Stop();
            var zsetOps = 20_000 / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"ZSetAdd:     {zsetOps:N0} ops/sec");

            // ── 6. 混合读写（70% 读 / 30% 写） ──
            var random = new Random(42);
            sw.Restart();
            var mixedCount = 50_000;
            for (int i = 0; i < mixedCount; i++)
            {
                if (random.Next(10) < 7) // 70% 读
                    await store.StringGetAsync($"sk_{random.Next(count)}");
                else // 30% 写
                    await store.StringSetAsync($"mk_{i}", $"mv{i}");
            }
            sw.Stop();
            var mixedOps = mixedCount / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"Mixed (7R/3W): {mixedOps:N0} ops/sec");

            // ── 总结 ──
            _output.WriteLine("");
            _output.WriteLine("=== 对比参考 ===");
            _output.WriteLine("MySQL InnoDB:    ~1,000-5,000 ops/sec (单连接)");
            _output.WriteLine("Redis:           ~100,000 ops/sec");
            _output.WriteLine("SQLite:          ~5,000-20,000 ops/sec");
            _output.WriteLine($"QuantumCore:     ~{setOps:N0} ops/sec (写) / ~{getHotOps:N0} ops/sec (读)");
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
