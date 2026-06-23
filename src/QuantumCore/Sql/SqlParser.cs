// ============================================================================
// 量子核（QuantumCore）— SQL 语法分析器
// 将 Token 流解析为 AST
// ============================================================================

using QuantumCore.Sql.Dialect;

namespace QuantumCore.Sql;

/// <summary>
/// SQL 语法分析器 — 递归下降解析
/// 支持：CREATE TABLE / INSERT / SELECT / UPDATE / DELETE
/// </summary>
public sealed class SqlParser
{
    private readonly List<Token> _tokens;
    private int _pos;
    private readonly DialectRegistry? _dialects;

    public SqlParser(string sql) : this(sql, null) { }

    public SqlParser(string sql, DialectRegistry? dialects)
    {
        _dialects = dialects;
        var tokenizer = new SqlTokenizer(sql);
        _tokens = tokenizer.Tokenize();
    }

    /// <summary>
    /// 解析 SQL 语句
    /// </summary>
    public SqlStatement Parse()
    {
        var token = Peek();

        return token.Type switch
        {
            TokenType.CREATE => ParseCreateTable(),
            TokenType.INSERT => ParseInsert(),
            TokenType.SELECT => ParseSelect(),
            TokenType.UPDATE => ParseUpdate(),
            TokenType.DELETE => ParseDelete(),
            TokenType.BEGIN => ParseBegin(),
            TokenType.COMMIT => ParseCommit(),
            TokenType.ROLLBACK => ParseRollback(),
            _ => TryPluginStatement() ?? throw new SqlParseException(
                $"意外的 token '{token.Value}'，期望 SQL 关键字",
                token.Line, token.Column)
        };
    }

    // ── CREATE TABLE ──

    private CreateTableStatement ParseCreateTable()
    {
        Expect(TokenType.CREATE);
        Expect(TokenType.TABLE);

        // MySQL: CREATE TABLE IF NOT EXISTS
        bool ifNotExists = false;
        if (Match(TokenType.IF))
        {
            Match(TokenType.NOT);
            Expect(TokenType.EXISTS);
            ifNotExists = true;
        }

        var tableName = Expect(TokenType.IDENTIFIER).Value;
        Expect(TokenType.LPAREN);

        var columns = new List<ColumnDefinition>();
        while (Peek().Type != TokenType.RPAREN)
        {
            if (columns.Count > 0) Expect(TokenType.COMMA);

            // MySQL: 跳过约束行（PRIMARY KEY(col)、UNIQUE KEY、INDEX、CONSTRAINT 等）
            if (Peek().Type is TokenType.PRIMARY or TokenType.UNIQUE or TokenType.INDEX or TokenType.KEY
                or TokenType.CONSTRAINT or TokenType.CHECK or TokenType.FOREIGN)
            {
                SkipConstraintLine();
                continue;
            }

            // 插件：跳过方言特有的列约束
            if (TryPluginColumnConstraint()) continue;

            columns.Add(ParseColumnDefinition());
        }

        Expect(TokenType.RPAREN);

        // 跳过表级选项（ENGINE=InnoDB, CHARSET=utf8 等），插件优先
        while (TryPluginTableOption()) { }
        SkipTableOptions();

        return new CreateTableStatement(tableName, columns, ifNotExists);
    }

    /// <summary>
    /// 跳过约束定义行（PRIMARY KEY (col), UNIQUE KEY, INDEX 等）
    /// </summary>
    private void SkipConstraintLine()
    {
        // 跳过第一个关键字（PRIMARY/UNIQUE/INDEX/KEY/CONSTRAINT/FOREIGN/CHECK）
        if (Peek().Type == TokenType.KEY) Advance();
        else Advance();

        // PRIMARY KEY / UNIQUE KEY 后面可能跟 KEY 名称
        if (Peek().Type == TokenType.KEY) Advance();

        // 跳过括号中的列列表 (col1, col2)
        if (Peek().Type == TokenType.LPAREN)
        {
            int depth = 0;
            do
            {
                if (Peek().Type == TokenType.LPAREN) depth++;
                if (Peek().Type == TokenType.RPAREN) depth--;
                Advance();
            }
            while (depth > 0 && Peek().Type != TokenType.EOF);
        }

        // 跳过剩余的表级选项直到逗号或右括号
        while (Peek().Type != TokenType.COMMA && Peek().Type != TokenType.RPAREN && Peek().Type != TokenType.EOF)
            Advance();
    }

    /// <summary>
    /// 跳过表级选项（ENGINE=InnoDB, CHARSET=utf8, AUTO_INCREMENT=1, COMMENT='...' 等）
    /// </summary>
    private void SkipTableOptions()
    {
        while (Peek().Type is TokenType.ENGINE or TokenType.CHARSET or TokenType.COLLATE
            or TokenType.AUTO_INCREMENT or TokenType.COMMENT or TokenType.DEFAULT)
        {
            Advance(); // 关键字
            Match(TokenType.EQ); // 可选等号
            // 跳过值（可能是标识符、字符串、数字）
            while (Peek().Type != TokenType.SEMICOLON && Peek().Type != TokenType.EOF
                && Peek().Type != TokenType.IDENTIFIER)
                Advance();
            // 如果后面跟的是标识符（如 InnoDB），继续跳
            if (Peek().Type == TokenType.IDENTIFIER) Advance();
        }
    }

    private ColumnDefinition ParseColumnDefinition()
    {
        var name = Expect(TokenType.IDENTIFIER).Value;
        var dataType = Expect(TokenType.IDENTIFIER).Value.ToUpperInvariant();

        // MySQL: 跳过类型长度 VARCHAR(255)、BIGINT(20) 等
        if (Peek().Type == TokenType.LPAREN)
        {
            int depth = 0;
            do
            {
                if (Peek().Type == TokenType.LPAREN) depth++;
                if (Peek().Type == TokenType.RPAREN) depth--;
                Advance();
            }
            while (depth > 0 && Peek().Type != TokenType.EOF);
        }

        // MySQL: 跳过 UNSIGNED / ZEROFILL
        while (Peek().Type == TokenType.UNSIGNED)
            Advance();

        bool isPrimaryKey = false;
        bool isNullable = true;
        string? defaultValue = null;

        // 解析约束
        while (Peek().Type is TokenType.PRIMARY or TokenType.NULL or TokenType.DEFAULT
            or TokenType.NOT or TokenType.AUTO_INCREMENT or TokenType.UNIQUE
            or TokenType.COMMENT)
        {
            if (Peek().Type == TokenType.COMMENT)
            {
                Advance(); // COMMENT
                if (Peek().Type == TokenType.STRING) Advance(); // 跳过注释字符串
                continue;
            }

            if (Match(TokenType.PRIMARY))
            {
                Match(TokenType.KEY);
                isPrimaryKey = true;
                isNullable = false;
            }
            else if (Match(TokenType.NOT))
            {
                Expect(TokenType.NULL);
                isNullable = false;
            }
            else if (Match(TokenType.AUTO_INCREMENT))
            {
                // MySQL: AUTO_INCREMENT，忽略
            }
            else if (Match(TokenType.UNIQUE))
            {
                Match(TokenType.KEY); // 可选
            }
            else if (Match(TokenType.NULL))
            {
                isNullable = true;
            }
            else if (Match(TokenType.DEFAULT))
            {
                defaultValue = ParseLiteral()?.ToString();
            }
        }

        return new ColumnDefinition(name, dataType, isPrimaryKey, isNullable, defaultValue);
    }

    // ── INSERT INTO ──

    private InsertStatement ParseInsert()
    {
        Expect(TokenType.INSERT);
        Expect(TokenType.INTO);

        var tableName = Expect(TokenType.IDENTIFIER).Value;
        Expect(TokenType.LPAREN);

        var columns = new List<string>();
        while (Peek().Type != TokenType.RPAREN)
        {
            if (columns.Count > 0) Expect(TokenType.COMMA);
            columns.Add(Expect(TokenType.IDENTIFIER).Value);
        }
        Expect(TokenType.RPAREN);

        Expect(TokenType.VALUES);

        var values = new List<List<object?>>();
        do
        {
            if (values.Count > 0) Expect(TokenType.COMMA);
            Expect(TokenType.LPAREN);

            var row = new List<object?>();
            while (Peek().Type != TokenType.RPAREN)
            {
                if (row.Count > 0) Expect(TokenType.COMMA);
                row.Add(ParseLiteral());
            }
            Expect(TokenType.RPAREN);
            values.Add(row);
        }
        while (Peek().Type == TokenType.COMMA);

        return new InsertStatement(tableName, columns, values);
    }

    // ── SELECT ──

    private SelectStatement ParseSelect()
    {
        Expect(TokenType.SELECT);

        // 列列表（支持 COUNT(*) 等聚合函数）
        var columns = new List<string>();
        if (Match(TokenType.STAR))
        {
            columns.Add("*");
        }
        else if (Peek().Type == TokenType.IDENTIFIER &&
                 Peek().Value.ToUpperInvariant() == "COUNT" &&
                 Peek(1).Type == TokenType.LPAREN)
        {
            Advance(); // COUNT
            Expect(TokenType.LPAREN);
            if (Match(TokenType.STAR)) columns.Add("COUNT(*)");
            else columns.Add($"COUNT({Expect(TokenType.IDENTIFIER).Value})");
            Expect(TokenType.RPAREN);
        }
        else
        {
            columns.Add(Expect(TokenType.IDENTIFIER).Value);
            while (Match(TokenType.COMMA))
            {
                // 支持 COUNT(*) 等聚合函数在列表中
                if (Peek().Type == TokenType.IDENTIFIER &&
                    Peek().Value.ToUpperInvariant() == "COUNT" &&
                    Peek(1).Type == TokenType.LPAREN)
                {
                    Advance();
                    Expect(TokenType.LPAREN);
                    if (Match(TokenType.STAR)) columns.Add("COUNT(*)");
                    else columns.Add($"COUNT({Expect(TokenType.IDENTIFIER).Value})");
                    Expect(TokenType.RPAREN);
                }
                else
                {
                    columns.Add(Expect(TokenType.IDENTIFIER).Value);
                }
            }
        }

        Expect(TokenType.FROM);
        var tableName = Expect(TokenType.IDENTIFIER).Value;

        // WHERE
        WhereClause? where = null;
        if (Match(TokenType.WHERE))
        {
            where = new WhereClause(ParseWhereCondition());
        }

        // ORDER BY
        OrderByClause? orderBy = null;
        if (Match(TokenType.ORDER))
        {
            Expect(TokenType.BY);
            var col = Expect(TokenType.IDENTIFIER).Value;
            var dir = Match(TokenType.DESC) ? SortDirection.Desc : SortDirection.Asc;
            orderBy = new OrderByClause(col, dir);
        }

        // LIMIT（支持 MySQL 语法：LIMIT count 或 LIMIT offset, count）
        int? limit = null;
        if (Match(TokenType.LIMIT))
        {
            var first = int.Parse(Expect(TokenType.INTEGER).Value);
            if (Match(TokenType.COMMA))
            {
                // MySQL: LIMIT offset, count → 忽略 offset（简化实现）
                var count = int.Parse(Expect(TokenType.INTEGER).Value);
                limit = count;
            }
            else
            {
                limit = first;
            }
        }

        return new SelectStatement(tableName, columns, where, orderBy, limit);
    }

    // ── UPDATE ──

    private UpdateStatement ParseUpdate()
    {
        Expect(TokenType.UPDATE);
        var tableName = Expect(TokenType.IDENTIFIER).Value;
        Expect(TokenType.SET);

        var sets = new List<SetClause>();
        do
        {
            if (sets.Count > 0) Expect(TokenType.COMMA);
            var col = Expect(TokenType.IDENTIFIER).Value;
            Expect(TokenType.EQ);
            var val = ParseLiteral();
            sets.Add(new SetClause(col, val));
        }
        while (Match(TokenType.COMMA));

        WhereClause? where = null;
        if (Match(TokenType.WHERE))
        {
            where = new WhereClause(ParseWhereCondition());
        }

        return new UpdateStatement(tableName, sets, where);
    }

    // ── DELETE FROM ──

    private DeleteStatement ParseDelete()
    {
        Expect(TokenType.DELETE);
        Expect(TokenType.FROM);
        var tableName = Expect(TokenType.IDENTIFIER).Value;

        WhereClause? where = null;
        if (Match(TokenType.WHERE))
        {
            where = new WhereClause(ParseWhereCondition());
        }

        return new DeleteStatement(tableName, where);
    }

    // ── WHERE 条件 ──

    private WhereCondition ParseWhereCondition()
    {
        var left = ParseWhereAtom();

        while (Peek().Type is TokenType.AND or TokenType.OR)
        {
            var op = Match(TokenType.AND) ? LogicalOperator.And : LogicalOperator.Or;
            var right = ParseWhereAtom();
            left = new LogicalCondition(op, left, right);
        }

        return left;
    }

    private WhereCondition ParseWhereAtom()
    {
        // ( ... )
        if (Match(TokenType.LPAREN))
        {
            var cond = ParseWhereCondition();
            Expect(TokenType.RPAREN);
            return cond;
        }

        // NOT
        if (Match(TokenType.NOT))
        {
            var cond = ParseWhereAtom();
            return new LogicalCondition(LogicalOperator.And, cond, new BinaryCondition("__always_false", ComparisonOperator.Equal, null));
        }

        // column BETWEEN a AND b
        if (Peek().Type == TokenType.IDENTIFIER &&
            Peek(1).Type == TokenType.BETWEEN)
        {
            var col = Expect(TokenType.IDENTIFIER).Value;
            Expect(TokenType.BETWEEN);
            var low = ParseLiteral();
            Expect(TokenType.AND);
            var high = ParseLiteral();
            // 转换为 low <= col AND col <= high
            return new LogicalCondition(LogicalOperator.And,
                new BinaryCondition(col, ComparisonOperator.GreaterThanOrEqual, low),
                new BinaryCondition(col, ComparisonOperator.LessThanOrEqual, high));
        }

        // column IS NULL / column IS NOT NULL
        if (Peek().Type == TokenType.IDENTIFIER &&
            Peek(1).Type == TokenType.IS)
        {
            var col = Expect(TokenType.IDENTIFIER).Value;
            Expect(TokenType.IS);
            if (Match(TokenType.NOT))
            {
                Expect(TokenType.NULL);
                return new BinaryCondition(col, ComparisonOperator.NotEqual, null);
            }
            else
            {
                Expect(TokenType.NULL);
                return new BinaryCondition(col, ComparisonOperator.Equal, null);
            }
        }

        // column IN (...)
        if (Peek().Type == TokenType.IDENTIFIER &&
            Peek(1).Type == TokenType.IN)
        {
            var col = Expect(TokenType.IDENTIFIER).Value;
            Expect(TokenType.IN);
            Expect(TokenType.LPAREN);

            var values = new List<object?>();
            while (Peek().Type != TokenType.RPAREN)
            {
                if (values.Count > 0) Expect(TokenType.COMMA);
                values.Add(ParseLiteral());
            }
            Expect(TokenType.RPAREN);
            return new InCondition(col, values);
        }

        // column op value
        var column = Expect(TokenType.IDENTIFIER).Value;
        var op = ParseComparisonOp();
        var value = ParseLiteral();
        return new BinaryCondition(column, op, value);
    }

    private ComparisonOperator ParseComparisonOp()
    {
        var token = Peek();
        Advance();
        return token.Type switch
        {
            TokenType.EQ => ComparisonOperator.Equal,
            TokenType.NEQ => ComparisonOperator.NotEqual,
            TokenType.LT => ComparisonOperator.LessThan,
            TokenType.LTE => ComparisonOperator.LessThanOrEqual,
            TokenType.GT => ComparisonOperator.GreaterThan,
            TokenType.GTE => ComparisonOperator.GreaterThanOrEqual,
            TokenType.LIKE => ComparisonOperator.Like,
            _ => throw new SqlParseException(
                $"期望比较运算符，得到 '{token.Value}'",
                token.Line, token.Column)
        };
    }

    // ── 工具方法 ──

    private object? ParseLiteral()
    {
        var token = Peek();
        switch (token.Type)
        {
            case TokenType.STRING:
                Advance();
                return token.Value;
            case TokenType.INTEGER:
                Advance();
                return long.Parse(token.Value);
            case TokenType.FLOAT:
                Advance();
                return double.Parse(token.Value);
            case TokenType.NULL:
                Advance();
                return null;
            case TokenType.PARAMETER:
                Advance();
                return token.Value;
            case TokenType.QUESTION:
                Advance();
                return "?";
            case TokenType.IDENTIFIER:
                Advance();
                return token.Value;
            default:
                throw new SqlParseException(
                    $"期望字面量，得到 '{token.Value}'",
                    token.Line, token.Column);
        }
    }

    internal Token Peek(int offset = 0)
    {
        var idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    internal Token Advance()
    {
        var token = _tokens[_pos];
        _pos++;
        return token;
    }

    private Token Expect(TokenType type)
    {
        var token = Peek();
        if (token.Type != type)
            throw new SqlParseException(
                $"期望 {type}，得到 '{token.Value}' ({token.Type})",
                token.Line, token.Column);
        return Advance();
    }

    private bool Match(TokenType type)
    {
        if (Peek().Type == type) { Advance(); return true; }
        return false;
    }

    // ── 插件扩展 ──

    private SqlStatement? TryPluginStatement()
    {
        if (_dialects == null) return null;
        foreach (var plugin in _dialects.Plugins)
        {
            var stmt = plugin.TryParseStatement(this);
            if (stmt != null) return stmt;
        }
        return null;
    }

    private bool TryPluginColumnConstraint()
    {
        if (_dialects == null) return false;
        foreach (var plugin in _dialects.Plugins)
        {
            if (plugin.TrySkipColumnConstraint(this)) return true;
        }
        return false;
    }

    private bool TryPluginTableOption()
    {
        if (_dialects == null) return false;
        foreach (var plugin in _dialects.Plugins)
        {
            if (plugin.TrySkipTableOption(this)) return true;
        }
        return false;
    }

    // ── BEGIN / COMMIT / ROLLBACK ──

    private SqlStatement ParseBegin()
    {
        Expect(TokenType.BEGIN);
        Match(TokenType.TRANSACTION); // 可选：BEGIN 或 BEGIN TRANSACTION
        return new BeginTransactionStatement();
    }

    private SqlStatement ParseCommit()
    {
        Expect(TokenType.COMMIT);
        Match(TokenType.TRANSACTION);
        return new CommitTransactionStatement();
    }

    private SqlStatement ParseRollback()
    {
        Expect(TokenType.ROLLBACK);
        Match(TokenType.TRANSACTION);
        return new RollbackTransactionStatement();
    }
}
