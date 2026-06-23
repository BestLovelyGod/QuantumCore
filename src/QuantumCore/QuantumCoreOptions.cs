// ============================================================================
// 量子核（QuantumCore）— 存储配置
// ============================================================================

namespace QuantumCore;

/// <summary>
/// 量子核配置
/// </summary>
public class QuantumCoreOptions
{
    /// <summary>数据存储根目录</summary>
    public string DataDirectory { get; set; } = "./data";

    /// <summary>Memory Engine 最大内存（字节），默认 256MB</summary>
    public long MaxMemoryBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>Memory Engine 最大 key 数量，默认 100000</summary>
    public int MaxKeyCount { get; set; } = 100_000;

    /// <summary>淘汰策略</summary>
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.LRU;

    /// <summary>WAL 预写日志是否启用</summary>
    public bool WalEnabled { get; set; } = true;

    /// <summary>磁盘持久化间隔（秒）</summary>
    public int PersistIntervalSeconds { get; set; } = 60;

    /// <summary>验证配置是否合法，抛出 ArgumentException 如果不合法</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new ArgumentException("DataDirectory 不能为空", nameof(DataDirectory));
        if (MaxMemoryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMemoryBytes), MaxMemoryBytes, "MaxMemoryBytes 必须大于 0");
        if (MaxKeyCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxKeyCount), MaxKeyCount, "MaxKeyCount 必须大于 0");
        if (PersistIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(PersistIntervalSeconds), PersistIntervalSeconds, "PersistIntervalSeconds 必须大于 0");
    }
}

/// <summary>
/// 内存淘汰策略
/// </summary>
public enum EvictionPolicy
{
    LRU,  // 最近最少使用
    LFU,  // 最不经常使用
    FIFO  // 先进先出
}
