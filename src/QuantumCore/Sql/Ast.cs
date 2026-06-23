// ============================================================================
// 量子核（QuantumCore）— SQL AST 模型
// 支持的语句：CREATE TABLE / INSERT / SELECT / UPDATE / DELETE / BEGIN / COMMIT / ROLLBACK
// ============================================================================

namespace QuantumCore.Sql;

// ── 语句类型 ──
public enum SqlStatementType
{
    CreateTable,
    Insert,
    Select,
    Update,
    Delete,
    BeginTransaction,
    CommitTransaction,
    RollbackTransaction
}

// ── SQL 语句基类 ──
public abstract record SqlStatement(SqlStatementType Type);

// ── BEGIN / COMMIT / ROLLBACK ──
public record BeginTransactionStatement() : SqlStatement(SqlStatementType.BeginTransaction);
public record CommitTransactionStatement() : SqlStatement(SqlStatementType.CommitTransaction);
public record RollbackTransactionStatement() : SqlStatement(SqlStatementType.RollbackTransaction);

// ── CREATE TABLE ──
public record CreateTableStatement(
    string TableName,
    List<ColumnDefinition> Columns,
    bool IfNotExists = false
) : SqlStatement(SqlStatementType.CreateTable);

public record ColumnDefinition(
    string Name,
    string DataType,
    bool IsPrimaryKey = false,
    bool IsNullable = true,
    string? DefaultValue = null
);

// ── INSERT INTO ──
public record InsertStatement(
    string TableName,
    List<string> Columns,
    List<List<object?>> Values
) : SqlStatement(SqlStatementType.Insert);

// ── SELECT ──
public record SelectStatement(
    string TableName,
    List<string> Columns,        // * 或列名列表
    WhereClause? Where,
    OrderByClause? OrderBy,
    int? Limit = null
) : SqlStatement(SqlStatementType.Select);

// ── UPDATE ──
public record UpdateStatement(
    string TableName,
    List<SetClause> Sets,
    WhereClause? Where
) : SqlStatement(SqlStatementType.Update);

public record SetClause(string Column, object? Value);

// ── DELETE FROM ──
public record DeleteStatement(
    string TableName,
    WhereClause? Where
) : SqlStatement(SqlStatementType.Delete);

// ── WHERE 子句 ──
public record WhereClause(WhereCondition Condition);

public abstract record WhereCondition;
public record BinaryCondition(
    string Column,
    ComparisonOperator Operator,
    object? Value
) : WhereCondition;

public record LogicalCondition(
    LogicalOperator Operator,
    WhereCondition Left,
    WhereCondition Right
) : WhereCondition;

public record InCondition(
    string Column,
    List<object?> Values
) : WhereCondition;

public enum ComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Like
}

public enum LogicalOperator
{
    And,
    Or
}

// ── ORDER BY ──
public record OrderByClause(
    string Column,
    SortDirection Direction = SortDirection.Asc
);

public enum SortDirection
{
    Asc,
    Desc
}
