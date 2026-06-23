// ============================================================================
// 量子核（QuantumCore）— 内置 SQL 方言插件
// MySQL + PostgreSQL 兼容，合并维护
// ============================================================================

namespace QuantumCore.Sql.Dialect;

/// <summary>
/// MySQL 方言插件
/// 处理 MySQL 特有语法：AUTO_INCREMENT、UNSIGNED、ENGINE、CHARSET 等
/// </summary>
public sealed class MySqlDialect : IDialectPlugin
{
    public string Name => "MySQL";
    public string Version => "8.0";

    public void RegisterKeywords(Dictionary<string, TokenType> keywords)
    {
        keywords["AUTO_INCREMENT"] = TokenType.AUTO_INCREMENT;
        keywords["UNSIGNED"] = TokenType.UNSIGNED;
        keywords["ENGINE"] = TokenType.ENGINE;
        keywords["CHARSET"] = TokenType.CHARSET;
        keywords["COLLATE"] = TokenType.COLLATE;
        keywords["COMMENT"] = TokenType.COMMENT;
        keywords["REFERENCES"] = TokenType.REFERENCES;
        keywords["UNIQUE"] = TokenType.UNIQUE;
        keywords["INDEX"] = TokenType.INDEX;
        keywords["IF"] = TokenType.IF;
        keywords["EXISTS"] = TokenType.EXISTS;
        keywords["REPLACE"] = TokenType.REPLACE;
        keywords["SHOW"] = TokenType.SHOW;
        keywords["DATABASES"] = TokenType.DATABASES;
        keywords["TABLES"] = TokenType.TABLES;
        keywords["CONSTRAINT"] = TokenType.CONSTRAINT;
        keywords["CHECK"] = TokenType.CHECK;
        keywords["FOREIGN"] = TokenType.FOREIGN;
        keywords["BETWEEN"] = TokenType.BETWEEN;
        keywords["IS"] = TokenType.IS;
    }

    public void RegisterTokenTypes(List<TokenType> tokenTypes) { }

    public bool TrySkipColumnConstraint(SqlParser parser)
    {
        if (parser.Peek().Type == TokenType.AUTO_INCREMENT)
        { parser.Advance(); return true; }

        if (parser.Peek().Type == TokenType.COMMENT)
        {
            parser.Advance();
            if (parser.Peek().Type == TokenType.STRING) parser.Advance();
            return true;
        }

        if (parser.Peek().Type == TokenType.UNIQUE)
        {
            parser.Advance();
            if (parser.Peek().Type == TokenType.KEY) parser.Advance();
            return true;
        }

        return false;
    }

    public bool TrySkipTableOption(SqlParser parser)
    {
        var t = parser.Peek().Type;
        if (t is TokenType.ENGINE or TokenType.CHARSET or TokenType.COLLATE or TokenType.AUTO_INCREMENT)
        {
            parser.Advance();
            if (parser.Peek().Type == TokenType.EQ) parser.Advance();
            while (parser.Peek().Type != TokenType.SEMICOLON && parser.Peek().Type != TokenType.EOF
                && parser.Peek().Type != TokenType.IDENTIFIER)
                parser.Advance();
            if (parser.Peek().Type == TokenType.IDENTIFIER) parser.Advance();
            return true;
        }
        return false;
    }

    public WhereCondition? TryParseWhereExtension(SqlParser parser, string column) => null;
    public string? TryParseSelectExtension(SqlParser parser) => null;

    public SqlStatement? TryParseStatement(SqlParser parser)
    {
        if (parser.Peek().Type == TokenType.SHOW)
        {
            while (parser.Peek().Type != TokenType.SEMICOLON && parser.Peek().Type != TokenType.EOF)
                parser.Advance();
            return new CreateTableStatement("__show__", new());
        }
        return null;
    }

    public QueryResult? AfterInsert(InsertStatement stmt, QueryResult result) => null;
}

/// <summary>
/// PostgreSQL 方言插件
/// 处理 PostgreSQL 特有语法：SERIAL、GENERATED ALWAYS AS IDENTITY、EXPLAIN 等
/// </summary>
public sealed class PostgreSqlDialect : IDialectPlugin
{
    public string Name => "PostgreSQL";
    public string Version => "16";

    public void RegisterKeywords(Dictionary<string, TokenType> keywords)
    {
        keywords["SERIAL"] = TokenType.IDENTIFIER;
        keywords["BIGSERIAL"] = TokenType.IDENTIFIER;
        keywords["BOOLEAN"] = TokenType.IDENTIFIER;
        keywords["TRUE"] = TokenType.IDENTIFIER;
        keywords["FALSE"] = TokenType.IDENTIFIER;
        keywords["ILIKE"] = TokenType.LIKE;
        keywords["RETURNING"] = TokenType.RETURNING;
        keywords["EXPLAIN"] = TokenType.EXPLAIN;
        keywords["ANALYZE"] = TokenType.ANALYZE;
        keywords["VACUUM"] = TokenType.VACUUM;
        keywords["CONFLICT"] = TokenType.CONFLICT;
        keywords["DO"] = TokenType.DO;
        keywords["NOTHING"] = TokenType.NOTHING;
        keywords["ON"] = TokenType.ON;
        keywords["GENERATED"] = TokenType.GENERATED;
        keywords["ALWAYS"] = TokenType.ALWAYS;
        keywords["IDENTITY"] = TokenType.IDENTITY;
        keywords["EXTENSION"] = TokenType.EXTENSION;
        keywords["SCHEMA"] = TokenType.SCHEMA;
        keywords["GRANT"] = TokenType.GRANT;
        keywords["REVOKE"] = TokenType.REVOKE;
    }

    public void RegisterTokenTypes(List<TokenType> tokenTypes) { }

    public bool TrySkipColumnConstraint(SqlParser parser)
    {
        if (parser.Peek().Type == TokenType.GENERATED)
        {
            parser.Advance();
            if (parser.Peek().Type == TokenType.ALWAYS) parser.Advance();
            if (parser.Peek().Type == TokenType.AS) parser.Advance();
            if (parser.Peek().Type == TokenType.IDENTITY) parser.Advance();
            if (parser.Peek().Type == TokenType.LPAREN)
            {
                int depth = 0;
                do
                {
                    if (parser.Peek().Type == TokenType.LPAREN) depth++;
                    if (parser.Peek().Type == TokenType.RPAREN) depth--;
                    parser.Advance();
                }
                while (depth > 0 && parser.Peek().Type != TokenType.EOF);
            }
            return true;
        }
        return false;
    }

    public bool TrySkipTableOption(SqlParser parser) => false;
    public WhereCondition? TryParseWhereExtension(SqlParser parser, string column) => null;
    public string? TryParseSelectExtension(SqlParser parser) => null;

    public SqlStatement? TryParseStatement(SqlParser parser)
    {
        if (parser.Peek().Type is TokenType.EXPLAIN or TokenType.VACUUM or TokenType.ANALYZE
            or TokenType.GRANT or TokenType.REVOKE)
        {
            while (parser.Peek().Type != TokenType.SEMICOLON && parser.Peek().Type != TokenType.EOF)
                parser.Advance();
            return new CreateTableStatement("__pg_skip__", new());
        }
        return null;
    }

    public QueryResult? AfterInsert(InsertStatement stmt, QueryResult result) => null;
}
