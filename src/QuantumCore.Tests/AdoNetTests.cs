// ============================================================================
// 量子核（QuantumCore）— ADO.NET + 事务 + 参数化查询测试
// ============================================================================

using Xunit;
using Xunit.Abstractions;
using QuantumCore.AdoNet;

namespace QuantumCore.Tests;

public class AdoNetTests
{
    private readonly ITestOutputHelper _output;

    public AdoNetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── ADO.NET 基础 ──

    [Fact]
    public void Connection_Open_Close_ShouldWork()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
        conn.Close();
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
    }

    [Fact]
    public void Command_ExecuteNonQuery_ShouldWork()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)";
        var affected = cmd.ExecuteNonQuery();
        Assert.Equal(0, affected);
    }

    [Fact]
    public void Command_ExecuteScalar_ShouldWork()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM users WHERE id = 1";
        var result = cmd.ExecuteScalar();
        Assert.Equal("Alice", result);
    }

    [Fact]
    public void ExecuteReader_ShouldWork()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name, age) VALUES (2, 'Bob', 25)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users ORDER BY age";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("Bob", reader.GetString(1));
        Assert.Equal(25, reader.GetInt32(2));

        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString(1));

        Assert.False(reader.Read());
    }

    // ── 参数化查询 ──

    [Fact]
    public void ParameterizedQuery_ShouldWork()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name, age) VALUES (1, 'Alice', 30)");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name, age) VALUES (2, 'Bob', 25)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM users WHERE age > @age";

        var param = cmd.CreateParameter();
        param.ParameterName = "@age";
        param.Value = 28;
        cmd.Parameters.Add(param);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void ParameterizedQuery_String_ShouldWork()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM users WHERE name = @name";

        var param = cmd.CreateParameter();
        param.ParameterName = "@name";
        param.Value = "Alice";
        cmd.Parameters.Add(param);

        var result = cmd.ExecuteScalar();
        Assert.Equal(1L, result);
    }

    // ── 事务 ──

    [Fact]
    public void Transaction_Commit_ShouldPersist()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");

        using (var tx = conn.BeginTransaction())
        {
            ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        var count = cmd.ExecuteScalar();
        Assert.Equal(1L, count);
    }

    [Fact]
    public void Transaction_Rollback_ShouldRevert()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");

        using (var tx = conn.BeginTransaction())
        {
            ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");
            tx.Rollback();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        var count = cmd.ExecuteScalar();
        Assert.Equal(0L, count);
    }

    [Fact]
    public void Transaction_Rollback_AfterMultipleInserts()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");

        // 先插入一些数据
        ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");
        ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (2, 'Bob')");

        // 在事务中插入更多数据，然后回滚
        using (var tx = conn.BeginTransaction())
        {
            ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (3, 'Charlie')");
            ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (4, 'Diana')");
            tx.Rollback();
        }

        // 只有事务前的 2 条数据
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        var count = cmd.ExecuteScalar();
        Assert.Equal(2L, count);
    }

    [Fact]
    public void Transaction_Dispose_ShouldRollback()
    {
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        ExecuteNonQuery(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");

        using (var tx = conn.BeginTransaction())
        {
            ExecuteNonQuery(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");
            // 不 Commit，Dispose 时应该自动回滚
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        var count = cmd.ExecuteScalar();
        Assert.Equal(0L, count);
    }

    // ── SQL 兼容性 ──

    [Fact]
    public void SQLiteStyle_ShouldWork()
    {
        // 模拟 SQLite 风格的使用方式
        using var conn = new QuantumCoreConnection(":memory:");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE products (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    price REAL DEFAULT 0.0,
                    stock INTEGER DEFAULT 0
                )";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO products (id, name, price, stock) VALUES (1, 'Widget', 9.99, 100)";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, price FROM products WHERE price > @minPrice";

            var param = cmd.CreateParameter();
            param.ParameterName = "@minPrice";
            param.Value = 5.0;
            cmd.Parameters.Add(param);

            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Widget", reader.GetString(0));
        }
    }

    // ── 辅助方法 ──

    private static void ExecuteNonQuery(QuantumCoreConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
