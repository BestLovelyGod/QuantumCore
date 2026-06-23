// ============================================================================
// 量子核（QuantumCore）— 方言注册中心
// 管理所有已注册的 SQL 方言插件
// ============================================================================

namespace QuantumCore.Sql.Dialect;

/// <summary>
/// 方言注册中心 — 管理 SQL 方言插件
/// </summary>
public sealed class DialectRegistry
{
    private readonly List<IDialectPlugin> _plugins = new();

    /// <summary>
    /// 注册一个方言插件
    /// </summary>
    public void Register(IDialectPlugin plugin)
    {
        _plugins.Add(plugin);
    }

    /// <summary>
    /// 获取所有已注册的插件
    /// </summary>
    public IReadOnlyList<IDialectPlugin> Plugins => _plugins.AsReadOnly();

    /// <summary>
    /// 按名称获取插件
    /// </summary>
    public IDialectPlugin? Get(string name)
    {
        return _plugins.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 注册所有插件的关键词到 Tokenizer
    /// </summary>
    public void ApplyKeywords(Dictionary<string, TokenType> keywords)
    {
        foreach (var plugin in _plugins)
            plugin.RegisterKeywords(keywords);
    }

    /// <summary>
    /// 注册所有插件的 Token 类型
    /// </summary>
    public void ApplyTokenTypes(List<TokenType> tokenTypes)
    {
        foreach (var plugin in _plugins)
            plugin.RegisterTokenTypes(tokenTypes);
    }
}
