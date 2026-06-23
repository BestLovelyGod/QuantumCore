// ============================================================================
// 量子核（QuantumCore）— Redis 方言插件
// 将 Redis 命令映射到 QuantumCore KV 操作
// 支持：SET/GET/DEL/EXISTS/EXPIRE/HSET/HGET/ZADD/ZRANGE 等
// ============================================================================

using System.Linq;

namespace QuantumCore.Sql.Dialect;

/// <summary>
/// Redis 命令执行结果
/// </summary>
public sealed class RedisResult
{
    public string Type { get; set; } = "string"; // string / integer / array / bulk / null
    public object? Value { get; set; }
    public List<object?>? Items { get; set; }

    public static RedisResult Ok() => new() { Type = "ok", Value = "OK" };
    public static RedisResult Integer(long n) => new() { Type = "integer", Value = n };
    public static RedisResult Bulk(string? s) => new() { Type = "bulk", Value = s };
    public static RedisResult Null() => new() { Type = "null", Value = null };
    public static RedisResult Array(List<object?> items) => new() { Type = "array", Items = items };
}

/// <summary>
/// Redis 方言插件
/// 将 Redis 命令语法转换为量子核内部 KV 操作
/// </summary>
public sealed class RedisDialect : IDialectPlugin
{
    private readonly Dictionary<string, string> _kvStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _hashStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<double, string>> _zsetStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _expiry = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Redis";
    public string Version => "7.0";

    public void RegisterKeywords(Dictionary<string, TokenType> keywords)
    {
        // Redis 命令作为标识符处理，不注册为关键字
    }

    public void RegisterTokenTypes(List<TokenType> tokenTypes) { }

    public bool TrySkipColumnConstraint(SqlParser parser) => false;
    public bool TrySkipTableOption(SqlParser parser) => false;
    public WhereCondition? TryParseWhereExtension(SqlParser parser, string column) => null;
    public string? TryParseSelectExtension(SqlParser parser) => null;

    /// <summary>
    /// 尝试解析 Redis 命令
    /// Redis 命令格式：COMMAND arg1 arg2 ...
    /// </summary>
    public SqlStatement? TryParseStatement(SqlParser parser)
    {
        if (parser.Peek().Type != TokenType.IDENTIFIER) return null;

        var cmd = parser.Peek().Value.ToUpperInvariant();
        if (!IsRedisCommand(cmd)) return null;

        // 收集所有 token 直到分号或 EOF
        var tokens = new List<Token>();
        while (parser.Peek().Type != TokenType.SEMICOLON && parser.Peek().Type != TokenType.EOF)
            tokens.Add(parser.Advance());

        if (tokens.Count == 0) return null;

        // 返回一个特殊的 CreateTableStatement 作为 Redis 命令的载体
        // ExecuteRedisCommand 会在 QueryExecutor 中被调用
        return new CreateTableStatement("__redis__", new(), false);
    }

    public QueryResult? AfterInsert(InsertStatement stmt, QueryResult result) => null;

    /// <summary>
    /// 是否是 Redis 命令
    /// </summary>
    private static bool IsRedisCommand(string word)
    {
        return word is "SET" or "GET" or "DEL" or "EXISTS" or "EXPIRE" or "TTL" or "KEYS" or "PING" or "DBSIZE"
            or "HSET" or "HGET" or "HDEL" or "HEXISTS" or "HGETALL" or "HKEYS" or "HVALS" or "HLEN"
            or "ZADD" or "ZRANGE" or "ZSCORE" or "ZREM" or "ZCARD" or "ZRANGEBYSCORE"
            or "LPUSH" or "RPUSH" or "LPOP" or "RPOP" or "LLEN" or "LRANGE"
            or "INCR" or "DECR" or "INCRBY" or "DECRBY"
            or "SETEX" or "SETNX" or "MSET" or "MGET";
    }

    /// <summary>
    /// 执行 Redis 命令（由外部调用）
    /// </summary>
    public RedisResult ExecuteCommand(string commandLine)
    {
        var parts = ParseCommand(commandLine);
        if (parts.Count == 0) return RedisResult.Ok();

        var cmd = parts[0].ToUpperInvariant();
        var args = parts.Skip(1).ToList();

        return cmd switch
        {
            // ── String 操作 ──
            "SET" => CmdSet(args),
            "GET" => CmdGet(args),
            "SETEX" => CmdSetEx(args),
            "SETNX" => CmdSetNx(args),
            "MSET" => CmdMSet(args),
            "MGET" => CmdMGet(args),
            "INCR" => CmdIncr(args),
            "DECR" => CmdDecr(args),
            "INCRBY" => CmdIncrBy(args),
            "DECRBY" => CmdDecrBy(args),

            // ── 通用 ──
            "DEL" => CmdDel(args),
            "EXISTS" => CmdExists(args),
            "EXPIRE" => CmdExpire(args),
            "TTL" => CmdTtl(args),
            "KEYS" => CmdKeys(args),
            "PING" => RedisResult.Bulk("PONG"),
            "DBSIZE" => RedisResult.Integer(_kvStore.Count + _hashStore.Count + _zsetStore.Count),

            // ── Hash 操作 ──
            "HSET" => CmdHSet(args),
            "HGET" => CmdHGet(args),
            "HDEL" => CmdHDel(args),
            "HEXISTS" => CmdHExists(args),
            "HGETALL" => CmdHGetAll(args),
            "HKEYS" => CmdHKeys(args),
            "HVALS" => CmdHVals(args),
            "HLEN" => CmdHLen(args),

            // ── ZSet 操作 ──
            "ZADD" => CmdZAdd(args),
            "ZRANGE" => CmdZRange(args),
            "ZSCORE" => CmdZScore(args),
            "ZREM" => CmdZRem(args),
            "ZCARD" => CmdZCard(args),
            "ZRANGEBYSCORE" => CmdZRangeByScore(args),

            // ── List 操作（简化：用字符串模拟） ──
            "LPUSH" => CmdLPush(args),
            "RPUSH" => CmdRPush(args),
            "LPOP" => CmdLPop(args),
            "RPOP" => CmdRPop(args),
            "LLEN" => CmdLLen(args),
            "LRANGE" => CmdLRange(args),

            _ => throw new SqlException($"未知的 Redis 命令: {cmd}")
        };
    }

    // ── String 命令 ──

    private RedisResult CmdSet(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("SET 需要至少 2 个参数");
        _kvStore[args[0]] = args[1];
        return RedisResult.Ok();
    }

    private RedisResult CmdGet(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("GET 需要至少 1 个参数");
        return _kvStore.TryGetValue(args[0], out var val) ? RedisResult.Bulk(val) : RedisResult.Null();
    }

    private RedisResult CmdSetEx(List<string> args)
    {
        if (args.Count < 3) throw new SqlException("SETEX 需要 3 个参数");
        _kvStore[args[0]] = args[2];
        _expiry[args[0]] = DateTime.UtcNow.AddSeconds(int.Parse(args[1]));
        return RedisResult.Ok();
    }

    private RedisResult CmdSetNx(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("SETNX 需要至少 2 个参数");
        if (_kvStore.ContainsKey(args[0])) return RedisResult.Integer(0);
        _kvStore[args[0]] = args[1];
        return RedisResult.Integer(1);
    }

    private RedisResult CmdMSet(List<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i += 2)
            _kvStore[args[i]] = args[i + 1];
        return RedisResult.Ok();
    }

    private RedisResult CmdMGet(List<string> args)
    {
        var results = new List<object?>();
        foreach (var a in args) { _kvStore.TryGetValue(a, out var v); results.Add(v); }
        return RedisResult.Array(results);
    }

    private RedisResult CmdIncr(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("INCR 需要至少 1 个参数");
        return CmdIncrDelta(args, 1);
    }

    private RedisResult CmdDecr(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("DECR 需要至少 1 个参数");
        return CmdIncrDelta(args, -1);
    }

    private RedisResult CmdIncrBy(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("INCRBY 需要 2 个参数");
        return CmdIncrDelta(new List<string> { args[0] }, long.Parse(args[1]));
    }

    private RedisResult CmdDecrBy(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("DECRBY 需要 2 个参数");
        return CmdIncrDelta(new List<string> { args[0] }, -long.Parse(args[1]));
    }

    private RedisResult CmdIncrDelta(List<string> args, long delta)
    {
        var key = args[0];
        long current = _kvStore.TryGetValue(key, out var val) && long.TryParse(val, out var v) ? v : 0;
        current += delta;
        _kvStore[key] = current.ToString();
        return RedisResult.Integer(current);
    }

    // ── 通用命令 ──

    private RedisResult CmdDel(List<string> args)
    {
        int count = 0;
        foreach (var key in args)
        {
            if (_kvStore.Remove(key)) count++;
            if (_hashStore.Remove(key)) count++;
            if (_zsetStore.Remove(key)) count++;
            _expiry.Remove(key);
        }
        return RedisResult.Integer(count);
    }

    private RedisResult CmdExists(List<string> args)
    {
        int count = args.Count(a => _kvStore.ContainsKey(a) || _hashStore.ContainsKey(a) || _zsetStore.ContainsKey(a));
        return RedisResult.Integer(count);
    }

    private RedisResult CmdExpire(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("EXPIRE 需要 2 个参数");
        _expiry[args[0]] = DateTime.UtcNow.AddSeconds(int.Parse(args[1]));
        return RedisResult.Integer(1);
    }

    private RedisResult CmdTtl(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("TTL 需要至少 1 个参数");
        if (!_expiry.TryGetValue(args[0], out var exp)) return RedisResult.Integer(-1);
        var remaining = (exp - DateTime.UtcNow).TotalSeconds;
        return remaining > 0 ? RedisResult.Integer((long)remaining) : RedisResult.Integer(-2);
    }

    private RedisResult CmdKeys(List<string> args)
    {
        var pattern = args.Count > 0 ? args[0] : "*";
        var allKeysSet = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _kvStore.Keys) allKeysSet.Add(k);
        foreach (var k in _hashStore.Keys) allKeysSet.Add(k);
        foreach (var k in _zsetStore.Keys) allKeysSet.Add(k);
        var regex = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matched = new List<object?>();
        foreach (var k in allKeysSet) { if (regex.IsMatch(k)) matched.Add(k); }
        return RedisResult.Array(matched);
    }

    // ── Hash 命令 ──

    private RedisResult CmdHSet(List<string> args)
    {
        if (args.Count < 3) throw new SqlException("HSET 需要至少 3 个参数");
        if (!_hashStore.ContainsKey(args[0]))
            _hashStore[args[0]] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _hashStore[args[0]][args[1]] = args[2];
        return RedisResult.Ok();
    }

    private RedisResult CmdHGet(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("HGET 需要至少 2 个参数");
        if (_hashStore.TryGetValue(args[0], out var hash) && hash.TryGetValue(args[1], out var val))
            return RedisResult.Bulk(val);
        return RedisResult.Null();
    }

    private RedisResult CmdHDel(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("HDEL 需要至少 2 个参数");
        if (!_hashStore.TryGetValue(args[0], out var hash)) return RedisResult.Integer(0);
        int count = 0;
        for (int i = 1; i < args.Count; i++)
            if (hash.Remove(args[i])) count++;
        return RedisResult.Integer(count);
    }

    private RedisResult CmdHExists(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("HEXISTS 需要至少 2 个参数");
        if (_hashStore.TryGetValue(args[0], out var hash) && hash.ContainsKey(args[1]))
            return RedisResult.Integer(1);
        return RedisResult.Integer(0);
    }

    private RedisResult CmdHGetAll(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("HGETALL 需要至少 1 个参数");
        if (!_hashStore.TryGetValue(args[0], out var hash)) return RedisResult.Array(new());
        var items = new List<object?>();
        foreach (var kv in hash) { items.Add(kv.Key); items.Add(kv.Value); }
        return RedisResult.Array(items);
    }

    private RedisResult CmdHKeys(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("HKEYS 需要至少 1 个参数");
        if (!_hashStore.TryGetValue(args[0], out var hash)) return RedisResult.Array(new());
        var keys = new List<object?>(); foreach (var k in hash.Keys) keys.Add(k); return RedisResult.Array(keys);
    }

    private RedisResult CmdHVals(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("HVALS 需要至少 1 个参数");
        if (!_hashStore.TryGetValue(args[0], out var hash)) return RedisResult.Array(new());
        var vals = new List<object?>(); foreach (var v in hash.Values) vals.Add(v); return RedisResult.Array(vals);
    }

    private RedisResult CmdHLen(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("HLEN 需要至少 1 个参数");
        if (!_hashStore.TryGetValue(args[0], out var hash)) return RedisResult.Integer(0);
        return RedisResult.Integer(hash.Count);
    }

    // ── ZSet 命令 ──

    private RedisResult CmdZAdd(List<string> args)
    {
        if (args.Count < 3) throw new SqlException("ZADD 需要至少 3 个参数");
        if (!_zsetStore.ContainsKey(args[0]))
            _zsetStore[args[0]] = new SortedDictionary<double, string>();
        var added = 0;
        for (int i = 1; i + 1 < args.Count; i += 2)
        {
            var score = double.Parse(args[i]);
            var member = args[i + 1];
            if (!_zsetStore[args[0]].ContainsValue(member)) added++;
            // 移除旧的相同 member
            double oldKey = double.NaN;
            foreach (var kv2 in _zsetStore[args[0]]) { if (kv2.Value == member) { oldKey = kv2.Key; break; } }
            if (!double.IsNaN(oldKey)) _zsetStore[args[0]].Remove(oldKey);
            _zsetStore[args[0]][score] = member;
        }
        return RedisResult.Integer(added);
    }

    private RedisResult CmdZRange(List<string> args)
    {
        if (args.Count < 3) throw new SqlException("ZRANGE 需要至少 3 个参数");
        if (!_zsetStore.TryGetValue(args[0], out var zset)) return RedisResult.Array(new());
        var start = int.Parse(args[1]);
        var stop = int.Parse(args[2]);
        var sorted = new List<object?>();
        foreach (var kv in zset) sorted.Add(kv.Value);
        var end = stop < 0 ? sorted.Count + stop + 1 : Math.Min(stop + 1, sorted.Count);
        if (end <= start) return RedisResult.Array(new());
        var result = new List<object?>();
        for (int i = start; i < end && i < sorted.Count; i++) result.Add(sorted[i]);
        return RedisResult.Array(result);
    }

    private RedisResult CmdZScore(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("ZSCORE 需要至少 2 个参数");
        if (!_zsetStore.TryGetValue(args[0], out var zset)) return RedisResult.Null();
        foreach (var kv in zset) { if (kv.Value == args[1]) return RedisResult.Bulk(kv.Key.ToString()); }
        return RedisResult.Null();
    }

    private RedisResult CmdZRem(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("ZREM 需要至少 2 个参数");
        if (!_zsetStore.TryGetValue(args[0], out var zset)) return RedisResult.Integer(0);
        var toRemove = new List<double>();
        foreach (var kv in zset) { if (kv.Value == args[1]) toRemove.Add(kv.Key); }
        foreach (var k in toRemove) zset.Remove(k);
        return RedisResult.Integer(toRemove.Count);
    }

    private RedisResult CmdZCard(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("ZCARD 需要至少 1 个参数");
        if (!_zsetStore.TryGetValue(args[0], out var zset)) return RedisResult.Integer(0);
        return RedisResult.Integer(zset.Count);
    }

    private RedisResult CmdZRangeByScore(List<string> args)
    {
        if (args.Count < 3) throw new SqlException("ZRANGEBYSCORE 需要至少 3 个参数");
        if (!_zsetStore.TryGetValue(args[0], out var zset)) return RedisResult.Array(new());
        var min = double.Parse(args[1]);
        var max = double.Parse(args[2]);
        var items = new List<object?>();
        foreach (var kv in zset) { if (kv.Key >= min && kv.Key <= max) items.Add(kv.Value); }
        return RedisResult.Array(items);
    }

    // ── List 命令（简化实现） ──

    private RedisResult CmdLPush(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("LPUSH 需要至少 2 个参数");
        var key = args[0];
        var list = GetOrCreateList(key);
        for (int i = 1; i < args.Count; i++)
            list.Insert(0, args[i]);
        return RedisResult.Integer(list.Count);
    }

    private RedisResult CmdRPush(List<string> args)
    {
        if (args.Count < 2) throw new SqlException("RPUSH 需要至少 2 个参数");
        var key = args[0];
        var list = GetOrCreateList(key);
        for (int i = 1; i < args.Count; i++)
            list.Add(args[i]);
        return RedisResult.Integer(list.Count);
    }

    private RedisResult CmdLPop(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("LPOP 需要至少 1 个参数");
        if (!IsListKey(args[0])) return RedisResult.Null();
        var list = GetList(args[0]);
        if (list.Count == 0) return RedisResult.Null();
        var val = list[0];
        list.RemoveAt(0);
        return RedisResult.Bulk(val);
    }

    private RedisResult CmdRPop(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("RPOP 需要至少 1 个参数");
        if (!IsListKey(args[0])) return RedisResult.Null();
        var list = GetList(args[0]);
        if (list.Count == 0) return RedisResult.Null();
        var val = list[^1];
        list.RemoveAt(list.Count - 1);
        return RedisResult.Bulk(val);
    }

    private RedisResult CmdLLen(List<string> args)
    {
        if (args.Count < 1) throw new SqlException("LLEN 需要至少 1 个参数");
        if (!IsListKey(args[0])) return RedisResult.Integer(0);
        return RedisResult.Integer(GetList(args[0]).Count);
    }

    private RedisResult CmdLRange(List<string> args)
    {
        if (args.Count < 3) throw new SqlException("LRANGE 需要至少 3 个参数");
        if (!IsListKey(args[0])) return RedisResult.Array(new());
        var list = GetList(args[0]);
        var start = int.Parse(args[1]);
        var stop = int.Parse(args[2]);
        var end = stop < 0 ? list.Count + stop : Math.Min(stop + 1, list.Count);
        if (end <= start) return RedisResult.Array(new());
        var result = new List<object?>();
        for (int i = start; i < end && i < list.Count; i++) result.Add(list[i]);
        return RedisResult.Array(result);
    }

    // ── List 辅助 ──

    private readonly Dictionary<string, List<string>> _listStore = new(StringComparer.OrdinalIgnoreCase);

    private List<string> GetOrCreateList(string key)
    {
        if (!_listStore.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _listStore[key] = list;
        }
        return list;
    }

    private List<string> GetList(string key) =>
        _listStore.TryGetValue(key, out var list) ? list : new();

    private bool IsListKey(string key) => _listStore.ContainsKey(key);

    // ── 工具 ──

    private static List<string> ParseCommand(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        foreach (var ch in commandLine.Trim())
        {
            if (ch == '"' || ch == '\'')
            {
                if (inQuotes && ch == quoteChar) { inQuotes = false; }
                else if (!inQuotes) { inQuotes = true; quoteChar = ch; }
                else current.Append(ch);
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}
