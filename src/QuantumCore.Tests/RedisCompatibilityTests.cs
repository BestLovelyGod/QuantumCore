// ============================================================================
// 量子核（QuantumCore）�?Redis 语法兼容性测�?
// ============================================================================

using Xunit;
using QuantumCore.Sql.Dialect;

namespace QuantumCore.Tests;

public class RedisCompatibilityTests
{
    private static RedisDialect CreateRedis() => new();

    // ── String 操作 ──

    [Fact]
    public void Set_Get_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET name Alice");
        var result = redis.ExecuteCommand("GET name");
        Assert.Equal("Alice", result.Value);
    }

    [Fact]
    public void Get_NonExistent_ShouldReturnNull()
    {
        var redis = CreateRedis();
        var result = redis.ExecuteCommand("GET nonexistent");
        Assert.Null(result.Value);
        Assert.Equal("null", result.Type);
    }

    [Fact]
    public void Del_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET key1 val1");
        redis.ExecuteCommand("SET key2 val2");
        var del = redis.ExecuteCommand("DEL key1");
        Assert.Equal(1L, del.Value);
        Assert.Null(redis.ExecuteCommand("GET key1").Value);
        Assert.Equal("val2", redis.ExecuteCommand("GET key2").Value);
    }

    [Fact]
    public void Exists_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET key1 val1");
        Assert.Equal(1L, redis.ExecuteCommand("EXISTS key1").Value);
        Assert.Equal(0L, redis.ExecuteCommand("EXISTS key2").Value);
    }

    [Fact]
    public void Incr_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET counter 10");
        var result = redis.ExecuteCommand("INCR counter");
        Assert.Equal(11L, result.Value);
        Assert.Equal("11", redis.ExecuteCommand("GET counter").Value);
    }

    [Fact]
    public void IncrBy_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET counter 10");
        var result = redis.ExecuteCommand("INCRBY counter 5");
        Assert.Equal(15L, result.Value);
    }

    [Fact]
    public void MSet_MGet_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("MSET k1 v1 k2 v2 k3 v3");
        var result = redis.ExecuteCommand("MGET k1 k2 k3");
        Assert.Equal(3, result.Items!.Count);
        Assert.Equal("v1", result.Items[0]);
        Assert.Equal("v2", result.Items[1]);
        Assert.Equal("v3", result.Items[2]);
    }

    [Fact]
    public void SetNx_ShouldWork()
    {
        var redis = CreateRedis();
        Assert.Equal(1L, redis.ExecuteCommand("SETNX key1 val1").Value);
        Assert.Equal(0L, redis.ExecuteCommand("SETNX key1 val2").Value);
        Assert.Equal("val1", redis.ExecuteCommand("GET key1").Value);
    }

    // ── Hash 操作 ──

    [Fact]
    public void HSet_HGet_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("HSET user:1 name Alice");
        redis.ExecuteCommand("HSET user:1 age 30");
        Assert.Equal("Alice", redis.ExecuteCommand("HGET user:1 name").Value);
        Assert.Equal("30", redis.ExecuteCommand("HGET user:1 age").Value);
    }

    [Fact]
    public void HGetAll_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("HSET user:1 name Alice");
        redis.ExecuteCommand("HSET user:1 age 30");
        var result = redis.ExecuteCommand("HGETALL user:1");
        Assert.Equal(4, result.Items!.Count); // name, Alice, age, 30
    }

    [Fact]
    public void HDel_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("HSET user:1 name Alice");
        redis.ExecuteCommand("HSET user:1 age 30");
        var del = redis.ExecuteCommand("HDEL user:1 name");
        Assert.Equal(1L, del.Value);
        Assert.Null(redis.ExecuteCommand("HGET user:1 name").Value);
    }

    [Fact]
    public void HLen_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("HSET user:1 name Alice");
        redis.ExecuteCommand("HSET user:1 age 30");
        Assert.Equal(2L, redis.ExecuteCommand("HLEN user:1").Value);
    }

    // ── ZSet 操作 ──

    [Fact]
    public void ZAdd_ZRange_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("ZADD leaderboard 100 Alice");
        redis.ExecuteCommand("ZADD leaderboard 200 Bob");
        redis.ExecuteCommand("ZADD leaderboard 150 Charlie");
        var result = redis.ExecuteCommand("ZRANGE leaderboard 0 -1");
        Assert.Equal(3, result.Items!.Count);
        Assert.Equal("Alice", result.Items[0]);   // 100
        Assert.Equal("Charlie", result.Items[1]);  // 150
        Assert.Equal("Bob", result.Items[2]);      // 200
    }

    [Fact]
    public void ZScore_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("ZADD scores 42.5 player1");
        var result = redis.ExecuteCommand("ZSCORE scores player1");
        Assert.Equal("42.5", result.Value);
    }

    [Fact]
    public void ZRem_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("ZADD zs 1 a");
        redis.ExecuteCommand("ZADD zs 2 b");
        var rem = redis.ExecuteCommand("ZREM zs a");
        Assert.Equal(1L, rem.Value);
        Assert.Equal(1L, redis.ExecuteCommand("ZCARD zs").Value);
    }

    [Fact]
    public void ZRangeByScore_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("ZADD prices 10 apple");
        redis.ExecuteCommand("ZADD prices 25 banana");
        redis.ExecuteCommand("ZADD prices 50 cherry");
        redis.ExecuteCommand("ZADD prices 100 date");
        var result = redis.ExecuteCommand("ZRANGEBYSCORE prices 20 60");
        Assert.Equal(2, result.Items!.Count);
    }

    // ── List 操作 ──

    [Fact]
    public void LPush_LPop_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("LPUSH mystack val1");
        redis.ExecuteCommand("LPUSH mystack val2");
        Assert.Equal("val2", redis.ExecuteCommand("LPOP mystack").Value);
        Assert.Equal("val1", redis.ExecuteCommand("LPOP mystack").Value);
    }

    [Fact]
    public void RPush_RPop_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("RPUSH myqueue val1");
        redis.ExecuteCommand("RPUSH myqueue val2");
        Assert.Equal("val2", redis.ExecuteCommand("RPOP myqueue").Value);
        Assert.Equal("val1", redis.ExecuteCommand("RPOP myqueue").Value);
    }

    [Fact]
    public void LRange_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("RPUSH mylist a");
        redis.ExecuteCommand("RPUSH mylist b");
        redis.ExecuteCommand("RPUSH mylist c");
        redis.ExecuteCommand("RPUSH mylist d");
        var result = redis.ExecuteCommand("LRANGE mylist 1 2");
        Assert.Equal(2, result.Items!.Count);
        Assert.Equal("b", result.Items[0]);
        Assert.Equal("c", result.Items[1]);
    }

    // ── 通用命令 ──

    [Fact]
    public void Ping_ShouldReturnPong()
    {
        var redis = CreateRedis();
        var result = redis.ExecuteCommand("PING");
        Assert.Equal("PONG", result.Value);
    }

    [Fact]
    public void DbSize_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET k1 v1");
        redis.ExecuteCommand("SET k2 v2");
        Assert.Equal(2L, redis.ExecuteCommand("DBSIZE").Value);
    }

    [Fact]
    public void Keys_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET user:1 Alice");
        redis.ExecuteCommand("SET user:2 Bob");
        redis.ExecuteCommand("SET order:1 widget");
        var result = redis.ExecuteCommand("KEYS user:*");
        Assert.Equal(2, result.Items!.Count);
    }

    // ── 混合操作 ──

    [Fact]
    public void MixedOperations_ShouldWork()
    {
        var redis = CreateRedis();
        redis.ExecuteCommand("SET session:abc user123");
        redis.ExecuteCommand("HSET user:123 name Alice age 30");
        redis.ExecuteCommand("ZADD scores 95 Alice");
        redis.ExecuteCommand("RPUSH queue task1 task2 task3");

        Assert.Equal("user123", redis.ExecuteCommand("GET session:abc").Value);
        Assert.Equal("Alice", redis.ExecuteCommand("HGET user:123 name").Value);
        Assert.Equal(1L, redis.ExecuteCommand("ZSCORE scores Alice").Value != null ? 1 : 0);
        Assert.Equal(3L, redis.ExecuteCommand("LLEN queue").Value);
    }
}
