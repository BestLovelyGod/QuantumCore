// ============================================================================
// 量子核（QuantumCore）— SQL 查询执行器
// 执行解析后的 SQL AST，操作内存中的表数据
// ============================================================================

using QuantumCore.Disk;

namespace QuantumCore.Sql;

/// <summary>
/// 表结构定义
/// </summary>
internal sealed class TableSchema
{
    public string Name { get; }
    public List<ColumnDefinition> Columns { get; }

    public TableSchema(string name, List<ColumnDefinition> columns)
    {
        Name = name;
        Columns = columns;
    }

    public int GetColumnIndex(string name)
    {
        var idx = Columns.FindIndex(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) throw new SqlException($"列 '{name}' 在表 '{Name}' 中不存在");
        return idx;
    }
}

/// <summary>
/// 内存中的行数据
/// </summary>
internal sealed class Row
{
    public object?[] Values { get; }
    public long Offset { get; set; } // Bitcask 中的偏移量

    public Row(int columnCount)
    {
        Values = new object?[columnCount];
    }
}

/// <summary>
/// 查询结果
/// </summary>
public sealed class QueryResult
{
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public int AffectedRows { get; set; }
}

/// <summary>
/// SQL 查询执行器
/// </summary>
internal sealed class QueryExecutor
{
    private readonly Dictionary<string, TableSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Row>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, BPlusTree>> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Bitcask? _storage;

    // ── 事务状态 ──
    private bool _inTransaction;
    private Dictionary<string, List<Row>>? _transactionOriginals;

    public QueryExecutor(Bitcask? storage = null)
    {
        _storage = storage;
    }

    /// <summary>
    /// 执行 SQL 语句
    /// </summary>
    public QueryResult Execute(SqlStatement statement)
    {
        return statement.Type switch
        {
            SqlStatementType.CreateTable => ExecuteCreateTable((CreateTableStatement)statement),
            SqlStatementType.Insert => ExecuteInsert((InsertStatement)statement),
            SqlStatementType.Select => ExecuteSelect((SelectStatement)statement),
            SqlStatementType.Update => ExecuteUpdate((UpdateStatement)statement),
            SqlStatementType.Delete => ExecuteDelete((DeleteStatement)statement),
            SqlStatementType.BeginTransaction => ExecuteBegin(),
            SqlStatementType.CommitTransaction => ExecuteCommit(),
            SqlStatementType.RollbackTransaction => ExecuteRollback(),
            _ => new QueryResult() // SHOW 等未实现语句返回空结果
        };
    }

    // ── BEGIN ──
    private QueryResult ExecuteBegin()
    {
        if (_inTransaction) throw new SqlException("已经在一个事务中");

        _inTransaction = true;
        // 快照当前所有表数据（用于回滚）
        _transactionOriginals = new Dictionary<string, List<Row>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _tables)
        {
            _transactionOriginals[kv.Key] = new List<Row>(kv.Value.Select(r =>
            {
                var copy = new Row(r.Values.Length);
                Array.Copy(r.Values, copy.Values, r.Values.Length);
                return copy;
            }));
        }
        return new QueryResult();
    }

    // ── COMMIT ──
    private QueryResult ExecuteCommit()
    {
        if (!_inTransaction) throw new SqlException("没有活动事务");

        _inTransaction = false;
        _transactionOriginals = null;
        return new QueryResult();
    }

    // ── ROLLBACK ──
    private QueryResult ExecuteRollback()
    {
        if (!_inTransaction) throw new SqlException("没有活动事务");
        if (_transactionOriginals == null) throw new SqlException("事务快照丢失");

        // 恢复所有表到事务开始前的状态
        foreach (var kv in _transactionOriginals)
        {
            _tables[kv.Key] = kv.Value;
        }

        _inTransaction = false;
        _transactionOriginals = null;
        return new QueryResult();
    }

    // ── CREATE TABLE ──

    private QueryResult ExecuteCreateTable(CreateTableStatement stmt)
    {
        if (_schemas.ContainsKey(stmt.TableName) && !stmt.IfNotExists)
            throw new SqlException($"表 '{stmt.TableName}' 已存在");

        if (_schemas.ContainsKey(stmt.TableName) && stmt.IfNotExists)
            return new QueryResult(); // IF NOT EXISTS，表已存在，静默跳过

        var schema = new TableSchema(stmt.TableName, stmt.Columns);
        _schemas[stmt.TableName] = schema;
        _tables[stmt.TableName] = new List<Row>();

        // 为主键列创建 B+Tree 索引
        var pkCol = stmt.Columns.FirstOrDefault(c => c.IsPrimaryKey);
        if (pkCol != null)
        {
            _indexes.TryAdd(stmt.TableName, new Dictionary<string, BPlusTree>(StringComparer.OrdinalIgnoreCase));
            _indexes[stmt.TableName][pkCol.Name] = new BPlusTree();
        }

        return new QueryResult { AffectedRows = 0 };
    }

    // ── INSERT ──

    private QueryResult ExecuteInsert(InsertStatement stmt)
    {
        var schema = GetSchema(stmt.TableName);
        var table = _tables[stmt.TableName];

        int inserted = 0;
        foreach (var values in stmt.Values)
        {
            var row = new Row(schema.Columns.Count);

            for (int i = 0; i < stmt.Columns.Count && i < values.Count; i++)
            {
                var colIdx = schema.GetColumnIndex(stmt.Columns[i]);
                row.Values[colIdx] = values[i];
            }

            // 填充默认值
            for (int i = 0; i < row.Values.Length; i++)
            {
                if (row.Values[i] == null && schema.Columns[i].DefaultValue != null)
                    row.Values[i] = schema.Columns[i].DefaultValue;
            }

            // 持久化到 Bitcask
            if (_storage != null)
            {
                var key = $"{stmt.TableName}:{table.Count}";
                var value = SerializeRow(row);
                _storage.Set(key, value);
                row.Offset = table.Count;
            }

            table.Add(row);

            // 更新索引
            if (_indexes.TryGetValue(stmt.TableName, out var indexes))
            {
                foreach (var idx in indexes.Values)
                {
                    var pkIdx = schema.Columns.FindIndex(c => c.IsPrimaryKey);
                    if (pkIdx >= 0)
                        idx.Insert(row.Values[pkIdx]!, table.Count - 1);
                }
            }

            inserted++;
        }

        return new QueryResult { AffectedRows = inserted };
    }

    // ── SELECT ──

    private QueryResult ExecuteSelect(SelectStatement stmt)
    {
        var schema = GetSchema(stmt.TableName);
        var table = _tables[stmt.TableName];
        var result = new QueryResult();

        // 检测聚合函数
        var hasCount = stmt.Columns.Any(c => c.StartsWith("COUNT("));
        var selectAll = stmt.Columns.Contains("*");
        var outputCols = selectAll
            ? schema.Columns.Select(c => c.Name).ToList()
            : stmt.Columns;

        result.Columns = outputCols;

        // 过滤行
        IEnumerable<Row> rows = table;

        if (stmt.Where != null)
        {
            rows = rows.Where(row => EvaluateWhere(row, stmt.Where.Condition, schema));
        }

        // 排序
        if (stmt.OrderBy != null)
        {
            var colIdx = schema.GetColumnIndex(stmt.OrderBy.Column);
            rows = stmt.OrderBy.Direction == SortDirection.Asc
                ? rows.OrderBy(r => r.Values[colIdx])
                : rows.OrderByDescending(r => r.Values[colIdx]);
        }

        // 限制
        if (stmt.Limit.HasValue)
            rows = rows.Take(stmt.Limit.Value);

        var rowList = rows.ToList();

        // 聚合查询
        if (hasCount)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var col in outputCols)
            {
                dict[col] = (long)rowList.Count;
            }
            result.Rows.Add(dict);
            return result;
        }

        // 组装结果
        foreach (var row in rowList)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var col in outputCols)
            {
                var idx = schema.GetColumnIndex(col);
                dict[col] = row.Values[idx];
            }
            result.Rows.Add(dict);
        }

        return result;
    }

    // ── UPDATE ──

    private QueryResult ExecuteUpdate(UpdateStatement stmt)
    {
        var schema = GetSchema(stmt.TableName);
        var table = _tables[stmt.TableName];

        int updated = 0;
        foreach (var row in table)
        {
            if (stmt.Where != null && !EvaluateWhere(row, stmt.Where.Condition, schema))
                continue;

            foreach (var set in stmt.Sets)
            {
                var colIdx = schema.GetColumnIndex(set.Column);
                row.Values[colIdx] = set.Value;
            }

            // 持久化
            if (_storage != null)
            {
                var key = $"{stmt.TableName}:{row.Offset}";
                _storage.Set(key, SerializeRow(row));
            }

            updated++;
        }

        return new QueryResult { AffectedRows = updated };
    }

    // ── DELETE ──

    private QueryResult ExecuteDelete(DeleteStatement stmt)
    {
        var schema = GetSchema(stmt.TableName);
        var table = _tables[stmt.TableName];

        int deleted = 0;
        for (int i = table.Count - 1; i >= 0; i--)
        {
            if (stmt.Where != null && !EvaluateWhere(table[i], stmt.Where.Condition, schema))
                continue;

            // 标记删除
            if (_storage != null)
            {
                var key = $"{stmt.TableName}:{table[i].Offset}";
                _storage.Delete(key);
            }

            table.RemoveAt(i);
            deleted++;
        }

        return new QueryResult { AffectedRows = deleted };
    }

    // ── WHERE 求值 ──

    private bool EvaluateWhere(Row row, WhereCondition condition, TableSchema schema)
    {
        return condition switch
        {
            BinaryCondition bc => EvaluateBinary(row, bc, schema),
            LogicalCondition lc => EvaluateLogical(row, lc, schema),
            InCondition ic => EvaluateIn(row, ic, schema),
            _ => true
        };
    }

    private bool EvaluateBinary(Row row, BinaryCondition bc, TableSchema schema)
    {
        var colIdx = schema.GetColumnIndex(bc.Column);
        var left = row.Values[colIdx];
        var right = bc.Value;

        return bc.Operator switch
        {
            ComparisonOperator.Equal => CompareValues(left, right) == 0,
            ComparisonOperator.NotEqual => CompareValues(left, right) != 0,
            ComparisonOperator.LessThan => CompareValues(left, right) < 0,
            ComparisonOperator.LessThanOrEqual => CompareValues(left, right) <= 0,
            ComparisonOperator.GreaterThan => CompareValues(left, right) > 0,
            ComparisonOperator.GreaterThanOrEqual => CompareValues(left, right) >= 0,
            ComparisonOperator.Like => LikeMatch(left?.ToString() ?? "", right?.ToString() ?? ""),
            _ => false
        };
    }

    private bool EvaluateLogical(Row row, LogicalCondition lc, TableSchema schema)
    {
        var left = EvaluateWhere(row, lc.Left, schema);
        var right = EvaluateWhere(row, lc.Right, schema);

        return lc.Operator switch
        {
            LogicalOperator.And => left && right,
            LogicalOperator.Or => left || right,
            _ => false
        };
    }

    private bool EvaluateIn(Row row, InCondition ic, TableSchema schema)
    {
        var colIdx = schema.GetColumnIndex(ic.Column);
        var val = row.Values[colIdx];
        return ic.Values.Any(v => CompareValues(val, v) == 0);
    }

    // ── 工具方法 ──

    private TableSchema GetSchema(string tableName)
    {
        if (!_schemas.TryGetValue(tableName, out var schema))
            throw new SqlException($"表 '{tableName}' 不存在");
        return schema;
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        // 数值类型统一转换为 double 比较
        if (IsNumeric(a) && IsNumeric(b))
        {
            var da = Convert.ToDouble(a);
            var db = Convert.ToDouble(b);
            return da.CompareTo(db);
        }

        if (a is IComparable ca && b.GetType() == a.GetType())
            return ca.CompareTo(b);

        return a.ToString()!.CompareTo(b.ToString());
    }

    private static bool IsNumeric(object value) =>
        value is int or long or float or double or decimal or short or byte;

    private static bool LikeMatch(string text, string pattern)
    {
        // 简单 LIKE 匹配：支持 %（任意字符）和 _（单个字符）
        var regexPattern = "^";
        foreach (var ch in pattern)
        {
            regexPattern += ch switch
            {
                '%' => ".*",
                '_' => ".",
                '.' or '(' or ')' or '[' or ']' or '^' or '$' or '{' or '}' or '\\' or '+' or '?' => "\\" + ch,
                _ => ch
            };
        }
        regexPattern += "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string SerializeRow(Row row)
    {
        return string.Join("\t", row.Values.Select(v => v?.ToString() ?? "NULL"));
    }
}

/// <summary>
/// SQL 执行异常
/// </summary>
public class SqlException : Exception
{
    public SqlException(string message) : base(message) { }
}
