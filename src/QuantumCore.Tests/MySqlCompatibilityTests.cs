// ============================================================================
// 量子核（QuantumCore）— MySQL 语法兼容性测试
// ============================================================================

using Xunit;
using QuantumCore.Sql;
using QuantumCore.Sql.Dialect;

namespace QuantumCore.Tests;

public class MySqlCompatibilityTests
{
    private static SqlQuery CreateMySql()
    {
        return new SqlQuery().WithDialect(new MySqlDialect());
    }

    [Fact]
    public void CreateTable_AutoIncrement_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY AUTO_INCREMENT, name VARCHAR(255) NOT NULL, age INT UNSIGNED DEFAULT 0, email TEXT)");
    }

    [Fact]
    public void CreateTable_IfNotExists_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY)");
        sql.Execute("CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY)");
    }

    [Fact]
    public void CreateTable_EngineAndCharset_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE logs (id INTEGER PRIMARY KEY, message TEXT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
    }

    [Fact]
    public void CreateTable_Comment_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY COMMENT 'uid', name VARCHAR(100) COMMENT 'name')");
    }

    [Fact]
    public void Insert_MySQLStyle_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");
        sql.Execute("INSERT INTO users (id, name, age) VALUES (2, 'Bob', 25)");
        var result = sql.Execute("SELECT * FROM users");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_LimitOffset_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");
        sql.Execute("INSERT INTO users (id, name) VALUES (1, 'A'), (2, 'B'), (3, 'C'), (4, 'D'), (5, 'E')");
        var result = sql.Execute("SELECT * FROM users LIMIT 2, 3");
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Select_Between_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE products (id INTEGER PRIMARY KEY, price INTEGER)");
        sql.Execute("INSERT INTO products (id, price) VALUES (1, 10), (2, 25), (3, 50), (4, 100)");
        var result = sql.Execute("SELECT * FROM products WHERE price BETWEEN 20 AND 60");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_IsNull_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");
        sql.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
        sql.Execute("INSERT INTO users (id, name) VALUES (2, NULL)");
        var result = sql.Execute("SELECT * FROM users WHERE name IS NULL");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Select_IsNotNull_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");
        sql.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
        sql.Execute("INSERT INTO users (id, name) VALUES (2, NULL)");
        var result = sql.Execute("SELECT * FROM users WHERE name IS NOT NULL");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void ShowDatabases_ShouldNotThrow()
    {
        using var sql = CreateMySql();
        var result = sql.Execute("SHOW DATABASES");
        Assert.NotNull(result);
    }

    [Fact]
    public void ShowTables_ShouldNotThrow()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY)");
        var result = sql.Execute("SHOW TABLES");
        Assert.NotNull(result);
    }

    [Fact]
    public void QuestionMark_ShouldParse()
    {
        var parser = new SqlParser("SELECT * FROM users WHERE id = ?");
        var stmt = parser.Parse();
        Assert.NotNull(stmt);
    }

    [Fact]
    public void FullMySQL_CreateTable_ShouldWork()
    {
        using var sql = CreateMySql();
        sql.Execute("CREATE TABLE IF NOT EXISTS orders (id BIGINT PRIMARY KEY AUTO_INCREMENT, user_id INTEGER NOT NULL, product_name VARCHAR(100) NOT NULL, quantity INTEGER UNSIGNED DEFAULT 1, price DECIMAL(10, 2) DEFAULT 0.00, status VARCHAR(20) DEFAULT 'pending', INDEX idx_user_id (user_id), UNIQUE KEY uk_order_id (id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
        sql.Execute("INSERT INTO orders (id, user_id, product_name, quantity, price, status) VALUES (1, 1, 'Widget', 2, 9.99, 'pending')");
        sql.Execute("INSERT INTO orders (id, user_id, product_name, quantity, price, status) VALUES (2, 2, 'Gadget', 1, 19.99, 'completed')");
        var result = sql.Execute("SELECT * FROM orders WHERE status = 'pending'");
        Assert.Single(result.Rows);
        var count = sql.Execute("SELECT COUNT(*) FROM orders");
        Assert.Equal(2L, count.Rows[0]["COUNT(*)"]);
    }
}
