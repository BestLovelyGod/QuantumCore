// ============================================================================
// 量子核（QuantumCore）— 混合存储引擎接口
// ============================================================================

namespace QuantumCore;

/// <summary>
/// 混合存储引擎统一接口
/// 提供 Redis 风格的 KV 操作和 SQL 风格的查询
/// </summary>
public interface IHybridStore : IDisposable
{
    // ── String 操作 ──
    Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null);
    Task<bool> StringSetHardAsync(string key, string value, TimeSpan? expiry = null); // 硬写：先落盘再写内存
    Task<string?> StringGetAsync(string key);
    Task<bool> StringDeleteAsync(string key);
    Task<bool> StringExistsAsync(string key);

    // ── Hash 操作 ──
    Task<bool> HashSetAsync(string key, string field, string value);
    Task<string?> HashGetAsync(string key, string field);
    Task<bool> HashDeleteAsync(string key, string field);
    Task<Dictionary<string, string>> HashGetAllAsync(string key);

    // ── ZSet（有序集合）操作 ──
    Task<bool> ZSetAddAsync(string key, string member, double score);
    Task<double?> ZSetScoreAsync(string key, string member);
    Task<List<(string Member, double Score)>> ZSetRangeAsync(string key, int start, int stop);
    Task<bool> ZSetRemoveAsync(string key, string member);

    // ── 通用操作 ──
    Task<bool> KeyDeleteAsync(string key);
    Task<bool> KeyExistsAsync(string key);
    Task<bool> KeyExpireAsync(string key, TimeSpan expiry);
    Task<long> DatabaseSizeAsync();
    Task<List<string>> PrefixSearchAsync(string prefix);
}
