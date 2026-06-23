// ============================================================================
// 量子核（QuantumCore）— SQL 词法分析器
// 将 SQL 字符串拆分为 Token 流
// ============================================================================

namespace QuantumCore.Sql;

/// <summary>
/// Token 类型
/// </summary>
public enum TokenType
{
    // 关键字
    CREATE, TABLE, INSERT, INTO, VALUES, SELECT, FROM, WHERE,
    UPDATE, SET, DELETE, AND, OR, NOT, IN, LIKE,
    ORDER, BY, ASC, DESC, LIMIT, PRIMARY, KEY, NULL,
    AS, DEFAULT, BEGIN, COMMIT, ROLLBACK, TRANSACTION,
    BETWEEN, IS, AUTO_INCREMENT, UNSIGNED,
    ENGINE, CHARSET, COLLATE, COMMENT, REFERENCES,
    UNIQUE, INDEX, IF, EXISTS, REPLACE,
    SHOW, DATABASES, TABLES,
    CONSTRAINT, CHECK, FOREIGN,
    // PostgreSQL 扩展
    RETURNING, EXPLAIN, ANALYZE, VACUUM,
    CONFLICT, DO, NOTHING, ON,
    GENERATED, ALWAYS, IDENTITY,
    EXTENSION, SCHEMA, GRANT, REVOKE,

    // 符号
    LPAREN, RPAREN,      // ( )
    COMMA,               // ,
    SEMICOLON,           // ;
    STAR,                // *
    DOT,                 // .
    EQ, NEQ, LT, LTE, GT, GTE,  // = != < <= > >=
    QUESTION,            // ? (MySQL 参数占位符)

    // 字面量
    IDENTIFIER,          // 标识符
    INTEGER,             // 整数
    FLOAT,               // 浮点数
    STRING,              // 字符串
    PARAMETER,           // @param

    // 结束
    EOF
}

/// <summary>
/// Token 数据
/// </summary>
public record Token(TokenType Type, string Value, int Line, int Column);

/// <summary>
/// SQL 词法分析器
/// </summary>
public sealed class SqlTokenizer
{
    private readonly string _input;
    private int _pos;
    private int _line = 1;
    private int _column = 1;

    // 关键字映射
    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CREATE"] = TokenType.CREATE, ["TABLE"] = TokenType.TABLE,
        ["INSERT"] = TokenType.INSERT, ["INTO"] = TokenType.INTO,
        ["VALUES"] = TokenType.VALUES, ["SELECT"] = TokenType.SELECT,
        ["FROM"] = TokenType.FROM, ["WHERE"] = TokenType.WHERE,
        ["UPDATE"] = TokenType.UPDATE, ["SET"] = TokenType.SET,
        ["DELETE"] = TokenType.DELETE, ["AND"] = TokenType.AND,
        ["OR"] = TokenType.OR, ["NOT"] = TokenType.NOT,
        ["IN"] = TokenType.IN, ["LIKE"] = TokenType.LIKE,
        ["ORDER"] = TokenType.ORDER, ["BY"] = TokenType.BY,
        ["ASC"] = TokenType.ASC, ["DESC"] = TokenType.DESC,
        ["LIMIT"] = TokenType.LIMIT, ["PRIMARY"] = TokenType.PRIMARY,
        ["KEY"] = TokenType.KEY, ["NULL"] = TokenType.NULL,
        ["AS"] = TokenType.AS, ["DEFAULT"] = TokenType.DEFAULT,
        ["BEGIN"] = TokenType.BEGIN, ["COMMIT"] = TokenType.COMMIT,
        ["ROLLBACK"] = TokenType.ROLLBACK, ["TRANSACTION"] = TokenType.TRANSACTION,
        ["BETWEEN"] = TokenType.BETWEEN, ["IS"] = TokenType.IS,
        ["AUTO_INCREMENT"] = TokenType.AUTO_INCREMENT, ["UNSIGNED"] = TokenType.UNSIGNED,
        ["ENGINE"] = TokenType.ENGINE, ["CHARSET"] = TokenType.CHARSET,
        ["COLLATE"] = TokenType.COLLATE, ["COMMENT"] = TokenType.COMMENT,
        ["REFERENCES"] = TokenType.REFERENCES, ["UNIQUE"] = TokenType.UNIQUE,
        ["INDEX"] = TokenType.INDEX, ["IF"] = TokenType.IF, ["EXISTS"] = TokenType.EXISTS,
        ["REPLACE"] = TokenType.REPLACE, ["SHOW"] = TokenType.SHOW,
        ["DATABASES"] = TokenType.DATABASES, ["TABLES"] = TokenType.TABLES,
        // PostgreSQL
        ["RETURNING"] = TokenType.RETURNING, ["EXPLAIN"] = TokenType.EXPLAIN,
        ["ANALYZE"] = TokenType.ANALYZE, ["VACUUM"] = TokenType.VACUUM,
        ["CONFLICT"] = TokenType.CONFLICT, ["DO"] = TokenType.DO,
        ["NOTHING"] = TokenType.NOTHING, ["ON"] = TokenType.ON,
        ["GENERATED"] = TokenType.GENERATED, ["ALWAYS"] = TokenType.ALWAYS,
        ["IDENTITY"] = TokenType.IDENTITY, ["EXTENSION"] = TokenType.EXTENSION,
        ["SCHEMA"] = TokenType.SCHEMA, ["GRANT"] = TokenType.GRANT,
        ["REVOKE"] = TokenType.REVOKE,
    };

    public SqlTokenizer(string input)
    {
        _input = input;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_pos < _input.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _input.Length) break;

            var ch = _input[_pos];

            // 标识符或关键字
            if (char.IsLetter(ch) || ch == '_')
            {
                tokens.Add(ReadIdentifier());
                continue;
            }

            // 数字
            if (char.IsDigit(ch))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // 字符串
            if (ch == '\'' || ch == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            // 参数 @param
            if (ch == '@')
            {
                tokens.Add(ReadParameter());
                continue;
            }

            // 符号
            tokens.Add(ReadSymbol());
        }

        tokens.Add(new Token(TokenType.EOF, "", _line, _column));
        return tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _input.Length)
        {
            var ch = _input[_pos];

            if (char.IsWhiteSpace(ch))
            {
                Advance();
                continue;
            }

            // 单行注释 -- 或 //
            if ((_pos + 1 < _input.Length) && _input[_pos] == '-' && _input[_pos + 1] == '-')
            {
                while (_pos < _input.Length && _input[_pos] != '\n') Advance();
                continue;
            }

            // 多行注释 /* */
            if ((_pos + 1 < _input.Length) && _input[_pos] == '/' && _input[_pos + 1] == '*')
            {
                Advance(); Advance();
                while (_pos + 1 < _input.Length && !(_input[_pos] == '*' && _input[_pos + 1] == '/'))
                    Advance();
                if (_pos + 1 < _input.Length) { Advance(); Advance(); }
                continue;
            }

            break;
        }
    }

    private Token ReadIdentifier()
    {
        var start = _pos;
        var startCol = _column;
        while (_pos < _input.Length && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'))
            Advance();

        var word = _input[start.._pos];
        var type = Keywords.TryGetValue(word, out var kw) ? kw : TokenType.IDENTIFIER;
        return new Token(type, word, _line, startCol);
    }

    private Token ReadNumber()
    {
        var start = _pos;
        var startCol = _column;
        var isFloat = false;

        while (_pos < _input.Length && char.IsDigit(_input[_pos])) Advance();

        if (_pos < _input.Length && _input[_pos] == '.' &&
            _pos + 1 < _input.Length && char.IsDigit(_input[_pos + 1]))
        {
            isFloat = true;
            Advance(); // .
            while (_pos < _input.Length && char.IsDigit(_input[_pos])) Advance();
        }

        var value = _input[start.._pos];
        return new Token(isFloat ? TokenType.FLOAT : TokenType.INTEGER, value, _line, startCol);
    }

    private Token ReadString()
    {
        var quote = _input[_pos];
        var startCol = _column;
        Advance(); // skip opening quote

        var start = _pos;
        while (_pos < _input.Length && _input[_pos] != quote)
        {
            if (_input[_pos] == '\\' && _pos + 1 < _input.Length) Advance(); // 转义字符
            Advance();
        }

        var value = _input[start.._pos];
        if (_pos < _input.Length) Advance(); // skip closing quote

        return new Token(TokenType.STRING, value, _line, startCol);
    }

    private Token ReadParameter()
    {
        var start = _pos;
        var startCol = _column;
        Advance(); // skip @

        while (_pos < _input.Length && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'))
            Advance();

        return new Token(TokenType.PARAMETER, _input[start.._pos], _line, startCol);
    }

    private Token ReadSymbol()
    {
        var ch = _input[_pos];
        var startCol = _column;
        var startLine = _line;

        switch (ch)
        {
            case '(': Advance(); return new Token(TokenType.LPAREN, "(", startLine, startCol);
            case ')': Advance(); return new Token(TokenType.RPAREN, ")", startLine, startCol);
            case ',': Advance(); return new Token(TokenType.COMMA, ",", startLine, startCol);
            case ';': Advance(); return new Token(TokenType.SEMICOLON, ";", startLine, startCol);
            case '*': Advance(); return new Token(TokenType.STAR, "*", startLine, startCol);
            case '?': Advance(); return new Token(TokenType.QUESTION, "?", startLine, startCol);
            case '.': Advance(); return new Token(TokenType.DOT, ".", startLine, startCol);
            case '=': Advance(); return new Token(TokenType.EQ, "=", startLine, startCol);
            case '!':
                if (_pos + 1 < _input.Length && _input[_pos + 1] == '=')
                { Advance(); Advance(); return new Token(TokenType.NEQ, "!=", startLine, startCol); }
                throw new SqlParseException($"意外的字符 '!' 在位置 {_pos}", _line, startCol);
            case '<':
                if (_pos + 1 < _input.Length && _input[_pos + 1] == '=')
                { Advance(); Advance(); return new Token(TokenType.LTE, "<=", startLine, startCol); }
                Advance(); return new Token(TokenType.LT, "<", startLine, startCol);
            case '>':
                if (_pos + 1 < _input.Length && _input[_pos + 1] == '=')
                { Advance(); Advance(); return new Token(TokenType.GTE, ">=", startLine, startCol); }
                Advance(); return new Token(TokenType.GT, ">", startLine, startCol);
            default:
                throw new SqlParseException($"意外的字符 '{ch}' 在位置 {_pos}", _line, startCol);
        }
    }

    private void Advance()
    {
        if (_pos < _input.Length)
        {
            if (_input[_pos] == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }
    }
}

/// <summary>
/// SQL 解析异常
/// </summary>
public class SqlParseException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public SqlParseException(string message, int line, int column)
        : base($"SQL 解析错误 (行 {line}, 列 {column}): {message}")
    {
        Line = line;
        Column = column;
    }
}
