// ============================================================================
// 量子核（QuantumCore）— SQL 层单元测试
// ============================================================================

using Xunit;
using Xunit.Abstractions;
using QuantumCore.Sql;

namespace QuantumCore.Tests;

public class SqlTests
{
    private readonly ITestOutputHelper _output;
    private readonly SqlQuery _sql;

    public SqlTests(ITestOutputHelper output)
    {
        _output = output;
        _sql = new SqlQuery();
    }

    // ── CREATE TABLE ──

    [Fact]
    public void CreateTable_ShouldWork()
    {
        var result = _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact]
    public void CreateTable_Duplicate_ShouldThrow()
    {
        _sql.Execute("CREATE TABLE t1 (id INTEGER PRIMARY KEY)");
        Assert.Throws<SqlException>(() =>
            _sql.Execute("CREATE TABLE t1 (id INTEGER PRIMARY KEY)"));
    }

    // ── INSERT ──

    [Fact]
    public void Insert_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        var result = _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");
        Assert.Equal(1, result.AffectedRows);
    }

    [Fact]
    public void Insert_MultipleRows_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        var result = _sql.Execute(
            "INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Charlie', 35)");
        Assert.Equal(3, result.AffectedRows);
    }

    // ── SELECT ──

    [Fact]
    public void Select_All_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25)");

        var result = _sql.Execute("SELECT * FROM users");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alice", result.Rows[0]["name"]);
        Assert.Equal("Bob", result.Rows[1]["name"]);
    }

    [Fact]
    public void Select_WithWhere_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Charlie', 35)");

        var result = _sql.Execute("SELECT * FROM users WHERE age > 28");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_Columns_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");

        var result = _sql.Execute("SELECT name, age FROM users");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["name"]);
        Assert.Equal(30L, result.Rows[0]["age"]);
        Assert.False(result.Rows[0].ContainsKey("id"));
    }

    [Fact]
    public void Select_WithOrderBy_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Charlie', 35)");

        var result = _sql.Execute("SELECT * FROM users ORDER BY age DESC");
        Assert.Equal("Charlie", result.Rows[0]["name"]);
        Assert.Equal("Alice", result.Rows[1]["name"]);
        Assert.Equal("Bob", result.Rows[2]["name"]);
    }

    [Fact]
    public void Select_WithLimit_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Charlie', 35)");

        var result = _sql.Execute("SELECT * FROM users LIMIT 2");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_WhereEqual_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25)");

        var result = _sql.Execute("SELECT * FROM users WHERE name = 'Bob'");
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0]["name"]);
    }

    // ── UPDATE ──

    [Fact]
    public void Update_WithWhere_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25)");

        var result = _sql.Execute("UPDATE users SET age = 31 WHERE name = 'Alice'");
        Assert.Equal(1, result.AffectedRows);

        var check = _sql.Execute("SELECT * FROM users WHERE name = 'Alice'");
        Assert.Equal(31L, check.Rows[0]["age"]);
    }

    [Fact]
    public void Update_WithoutWhere_ShouldUpdateAll()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25)");

        var result = _sql.Execute("UPDATE users SET age = 0");
        Assert.Equal(2, result.AffectedRows);
    }

    // ── DELETE ──

    [Fact]
    public void Delete_WithWhere_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25)");

        var result = _sql.Execute("DELETE FROM users WHERE name = 'Bob'");
        Assert.Equal(1, result.AffectedRows);

        var check = _sql.Execute("SELECT * FROM users");
        Assert.Single(check.Rows);
        Assert.Equal("Alice", check.Rows[0]["name"]);
    }

    // ── 复杂查询 ──

    [Fact]
    public void Select_CombinedConditions_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Charlie', 35)");

        var result = _sql.Execute("SELECT * FROM users WHERE age >= 25 AND age <= 30");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_InCondition_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Charlie', 35)");

        var result = _sql.Execute("SELECT * FROM users WHERE name IN ('Alice', 'Charlie')");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_Like_ShouldWork()
    {
        _sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        _sql.Execute("INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30), (2, 'Bob', 25), (3, 'Amanda', 35)");

        var result = _sql.Execute("SELECT * FROM users WHERE name LIKE 'A%'");
        Assert.Equal(2, result.Rows.Count);
    }

    // ── B+Tree 索引 ──

    [Fact]
    public void BPlusTree_RangeSearch_ShouldWork()
    {
        var tree = new BPlusTree(order: 4);

        // 插入 100 个有序键
        for (int i = 0; i < 100; i++)
            tree.Insert(i, i);

        // 范围查询 [20, 30)
        var results = tree.RangeSearch(20, 30);
        Assert.Equal(10, results.Count);
        Assert.Equal(20, results[0]);
        Assert.Equal(29, results[^1]);
    }

    [Fact]
    public void BPlusTree_Search_ShouldWork()
    {
        var tree = new BPlusTree(order: 4);

        for (int i = 0; i < 50; i++)
            tree.Insert(i * 10, i);

        var results = tree.Search(30);
        Assert.Single(results);
        Assert.Equal(3, results[0]);
    }

    // ── 完整工作流 ──

    [Fact]
    public void FullWorkflow_CreateInsertSelectUpdateDelete()
    {
        // 创建表
        _sql.Execute("CREATE TABLE products (id INTEGER PRIMARY KEY, name TEXT, price INTEGER, stock INTEGER)");

        // 插入数据
        _sql.Execute("INSERT INTO products (id, name, price, stock) VALUES (1, 'Widget', 999, 100)");
        _sql.Execute("INSERT INTO products (id, name, price, stock) VALUES (2, 'Gadget', 1999, 50)");
        _sql.Execute("INSERT INTO products (id, name, price, stock) VALUES (3, 'Doohickey', 499, 200)");

        // 查询
        var all = _sql.Execute("SELECT * FROM products");
        Assert.Equal(3, all.Rows.Count);

        var expensive = _sql.Execute("SELECT * FROM products WHERE price > 1000");
        Assert.Single(expensive.Rows);

        // 更新
        _sql.Execute("UPDATE products SET stock = 99 WHERE name = 'Widget'");
        var updated = _sql.Execute("SELECT * FROM products WHERE name = 'Widget'");
        Assert.Equal(99L, updated.Rows[0]["stock"]);

        // 删除
        _sql.Execute("DELETE FROM products WHERE id = 2");
        var remaining = _sql.Execute("SELECT * FROM products");
        Assert.Equal(2, remaining.Rows.Count);
    }
}
