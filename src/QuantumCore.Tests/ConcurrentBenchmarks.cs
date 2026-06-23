// ============================================================================
// 量子核（QuantumCore）— 并发读写基准测试
// 对比 Redis 单线程模型 vs QuantumCore 多线程并发
// ============================================================================

using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace QuantumCore.Tests;

public class ConcurrentBenchmarks
{
    private readonly ITestOutputHelper _output;

    public ConcurrentBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ConcurrentReadWrite(int concurrency)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"qc_conc_{concurrency}_{Guid.NewGuid():N}");
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = tempDir,
            WalEnabled = false
        });

        try
        {
            // 预写数据
            for (int i = 0; i < 10_000; i++)
                await store.StringSetAsync($"key_{i}", $"value_{i}");

            var opsPerThread = 50_000;
            var totalOps = opsPerThread * concurrency;
            var sw = Stopwatch.StartNew();

            // ── 并发读写（80% 读 / 20% 写） ──
            var tasks = new Task[concurrency];
            for (int t = 0; t < concurrency; t++)
            {
                var threadId = t;
                tasks[t] = Task.Run(async () =>
                {
                    var random = new Random(threadId);
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        if (random.Next(10) < 8) // 80% 读
                            await store.StringGetAsync($"key_{random.Next(10_000)}");
                        else // 20% 写
                            await store.StringSetAsync($"wkey_{threadId}_{i}", $"wval");
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"并发={concurrency}  总ops={totalOps}  耗时={sw.ElapsedMilliseconds}ms  吞吐={opsPerSec:N0} ops/sec");
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ConcurrentRead_Only(int concurrency)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"qc_read_{concurrency}_{Guid.NewGuid():N}");
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = tempDir,
            WalEnabled = false
        });

        try
        {
            // 预写数据
            for (int i = 0; i < 50_000; i++)
                await store.StringSetAsync($"rkey_{i}", $"rval_{i}");

            var opsPerThread = 100_000;
            var totalOps = opsPerThread * concurrency;
            var sw = Stopwatch.StartNew();

            // ── 纯并发读 ──
            var tasks = new Task[concurrency];
            for (int t = 0; t < concurrency; t++)
            {
                var threadId = t;
                tasks[t] = Task.Run(async () =>
                {
                    var random = new Random(threadId);
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        await store.StringGetAsync($"rkey_{random.Next(50_000)}");
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"并发={concurrency}  纯读  总ops={totalOps}  耗时={sw.ElapsedMilliseconds}ms  吞吐={opsPerSec:N0} ops/sec");
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ConcurrentWrite_Only(int concurrency)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"qc_write_{concurrency}_{Guid.NewGuid():N}");
        var store = new HybridStore(new QuantumCoreOptions
        {
            DataDirectory = tempDir,
            WalEnabled = false
        });

        try
        {
            var opsPerThread = 20_000;
            var totalOps = opsPerThread * concurrency;
            var sw = Stopwatch.StartNew();

            // ── 纯并发写 ──
            var tasks = new Task[concurrency];
            for (int t = 0; t < concurrency; t++)
            {
                var threadId = t;
                tasks[t] = Task.Run(async () =>
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        await store.StringSetAsync($"wkey_{threadId}_{i}", $"wval_{i}");
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"并发={concurrency}  纯写  总ops={totalOps}  耗时={sw.ElapsedMilliseconds}ms  吞吐={opsPerSec:N0} ops/sec");
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
