// ============================================================================
// 量子核（QuantumCore）— SQL 方言插件接口
// 每个数据库方言作为独立插件，可单独维护
// ============================================================================

namespace QuantumCore.Sql.Dialect;

/// <summary>
/// SQL 方言插件接口
/// 实现此接口即可为量子核添加新数据库方言的兼容支持
/// </summary>
public interface IDialectPlugin
{
    /// <summary>方言名称（如 "MySQL"、"PostgreSQL"）</summary>
    string Name { get; }

    /// <summary>方言版本</summary>
    string Version { get; }

    /// <summary>
    /// 注册方言特有的关键字到 Tokenizer
    /// </summary>
    void RegisterKeywords(Dictionary<string, TokenType> keywords);

    /// <summary>
    /// 注册方言特有的 Token 类型
    /// </summary>
    void RegisterTokenTypes(List<TokenType> tokenTypes);

    /// <summary>
    /// 在 Parser 解析 CREATE TABLE 列定义时调用
    /// 返回 true 表示已处理（跳过），false 表示未识别
    /// </summary>
    bool TrySkipColumnConstraint(SqlParser parser);

    /// <summary>
    /// 在 Parser 解析 CREATE TABLE 表级选项时调用
    /// 返回 true 表示已处理（跳过），false 表示未识别
    /// </summary>
    bool TrySkipTableOption(SqlParser parser);

    /// <summary>
    /// 在 Parser 解析 WHERE 条件时调用
    /// 返回处理后的条件，null 表示未识别
    /// </summary>
    WhereCondition? TryParseWhereExtension(SqlParser parser, string column);

    /// <summary>
    /// 在 Parser 解析 SELECT 列时调用
    /// 返回处理后的列名，null 表示未识别
    /// </summary>
    string? TryParseSelectExtension(SqlParser parser);

    /// <summary>
    /// 在 Parser 解析语句开头时调用
    /// 返回处理后的语句，null 表示未识别
    /// </summary>
    SqlStatement? TryParseStatement(SqlParser parser);

    /// <summary>
    /// 在 QueryExecutor 执行 INSERT 后调用（用于 RETURNING 等）
    /// </summary>
    QueryResult? AfterInsert(InsertStatement stmt, QueryResult result);
}
