# QuantumCore

嵌入式混合存储引擎 — SQLite + Redis 的融合体。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)

## 概述

**QuantumCore** 是一款面向 **Windows + .NET 10** 平台的高性能嵌入式混合存储引擎。纯 .NET 实现，零外部依赖，进程内调用零网络开销。

| 特性 | 说明 |
|------|------|
| 类型 | 嵌入式库（非独立服务） |
| 目标平台 | Windows x64 + .NET 10 |
| 类比 | SQLite + Redis 的融合体 |
| 使用方式 | 进程内调用，零网络开销 |
| 职责边界 | KV 读写 + 持久化 + TTL + 崩溃恢复 + SQL 查询 |

## 核心特性

- **双引擎架构**：Bitcask 追加日志 + 内存 KV，统一管理
- **极致性能**：Soft Write 纯内存路径，热读可达百万级 ops/sec
- **SQL 引擎**：完整 SQL Parser + B+Tree 索引 + 查询执行器
- **ADO.NET 兼容**：IDbConnection / IDbCommand / IDataReader 标准接口
- **事务支持**：BEGIN / COMMIT / ROLLBACK + 快照回滚
- **方言插件**：MySQL / PostgreSQL / Redis 语法兼容
- **Soft/Hard 写入**：Soft Write 仅写内存，后台异步刷盘；Hard Write 同步落盘保证数据安全

## 方言插件

| 方言 | 说明 | 状态 |
|------|------|------|
| MySQL | AUTO_INCREMENT / ENGINE / BETWEEN / IS NULL / SHOW 等 | 已实现 |
| PostgreSQL | SERIAL / EXPLAIN / VACUUM / GRANT 等 | 已实现 |
| Redis | SET/GET/HSET/ZADD/LPUSH 等 30+ 命令 | 已实现 |
| 自定义 | 实现 IDialectPlugin 接口即可 | 已实现 |

## 性能

Soft Write 走纯内存路径（无磁盘 I/O），读取穿透磁盘。通过 `dotnet test` 自带的 benchmark 测试可自行验证。

```bash
dotnet test --filter "FullyQualifiedName~ThroughputBenchmarks"
```

## 快速开始

```csharp
// 1. KV 操作（Redis 风格）
using var store = new HybridStore(new QuantumCoreOptions { DataDirectory = "./data" });
await store.StringSetAsync("key", "value");
var val = await store.StringGetAsync("key");

// 2. SQL 操作
using var sql = new SqlQuery();
sql.Execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)");
sql.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");

// 3. ADO.NET 操作（可替换 SQLite）
using var conn = new QuantumCoreConnection(":memory:");
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT * FROM users WHERE id = @id";
cmd.Parameters.AddWithValue("@id", 1);
using var reader = cmd.ExecuteReader();

// 4. MySQL 兼容模式
using var mysql = new SqlQuery().WithDialect(new MySqlDialect());

// 5. Redis 命令
var redis = new RedisDialect();
redis.ExecuteCommand("SET name Alice");
redis.ExecuteCommand("HSET user:1 name Alice");
```

## 为什么选择 Windows + .NET 10

| 特性 | 说明 | 量子核的利用 |
|------|------|-------------|
| **FileOptions.WriteThrough** | 绕过 OS 缓存，数据直达磁盘 | Bitcask 引擎硬写模式保证数据安全 |
| **ConcurrentDictionary** | 无锁并发读写 | 内存引擎热数据存储，纳秒级访问 |
| **ConcurrentQueue** | 无锁并发入队/出队 | Soft Write 脏队列，写入零拷贝 |
| **Interlocked / Volatile** | 原子操作 + 内存屏障 | 淘汰计数器、访问统计的线程安全 |
| **Timer + Task.Run** | 异步定时刷盘 | Soft Write 后台刷盘，不阻塞主线程 |
| **System.Security.Cryptography** | .NET 内置 CRC32 | Bitcask 记录校验，防止数据损坏 |
| **Span\<T\> / BinaryPrimitives** | 零拷贝二进制读写 | Bitcask 编解码，避免数组分配 |
| **BCL 集合** | List/Dictionary/Queue | B+Tree 索引、SQL 执行器、事务快照 |
| **System.Text.Json** | 高性能 JSON 序列化 | 配置文件加载、审计日志持久化 |
| **DbConnection/DbCommand** | ADO.NET 标准接口 | 可直接替换 SQLite，ORM 零修改接入 |

> **选型逻辑**：量子核是纯托管代码，不依赖 P/Invoke 或平台特定 API。选择 Windows + .NET 10 的原因是目标用户群和部署环境在 Windows 平台，.NET 10 提供了极致的 JIT 优化和丰富的 BCL 支持。

## 架构

```
┌─────────────────────────────────────────────────┐
│              IHybridStore 接口                   │
│         (KV / SQL / ADO.NET 三层)               │
└───────────┬───────────────────────┬─────────────┘
            │                       │
┌───────────▼──────────┐ ┌─────────▼──────────────┐
│   Memory Engine      │ │    Disk Engine          │
│   (类 Redis)         │ │    (Bitcask)            │
│   ConcurrentDict     │ │    追加日志 + 内存索引   │
│   LRU/LFU/TTL 淘汰   │ │    顺序写 + 压缩合并    │
└───────────┬──────────┘ └─────────┬──────────────┘
            │                       │
            └───────────┬───────────┘
                        ▼
              ┌─────────────────┐
              │  HybridStore    │
              │  Soft/Hard 写入 │
              └─────────────────┘
```

## 项目结构

```
QuantumCore/
├── src/
│   ├── QuantumCore/
│   │   ├── HybridStore.cs              # 混合存储实现
│   │   ├── IHybridStore.cs             # 存储接口
│   │   ├── QuantumCoreOptions.cs       # 配置
│   │   ├── Memory/
│   │   │   └── MemoryEngine.cs         # 内存引擎（LRU/LFU/FIFO 淘汰）
│   │   ├── Disk/
│   │   │   ├── Bitcask.cs              # Bitcask 追加日志引擎
│   │   │   └── DiskEngine.cs           # 磁盘引擎封装
│   │   ├── Sql/
│   │   │   ├── SqlTokenizer.cs         # 词法分析器
│   │   │   ├── SqlParser.cs            # 语法分析器
│   │   │   ├── Ast.cs                  # SQL AST 模型
│   │   │   ├── BPlusTree.cs            # B+Tree 索引
│   │   │   ├── QueryExecutor.cs        # 查询执行器
│   │   │   ├── SqlQuery.cs             # SQL 查询接口
│   │   │   └── Dialect/
│   │   │       ├── IDialectPlugin.cs   # 方言插件接口
│   │   │       ├── BuiltInDialects.cs  # MySQL + PostgreSQL
│   │   │       └── RedisDialect.cs     # Redis 命令兼容
│   │   └── AdoNet/
│   │       ├── QuantumCoreConnection.cs
│   │       ├── QuantumCoreCommand.cs
│   │       ├── QuantumCoreDataReader.cs
│   │       ├── QuantumCoreTransaction.cs
│   │       └── QuantumCoreParameter.cs
│   └── QuantumCore.Tests/              # 单元测试
└── doc/
```

## 测试

```bash
# 运行全部测试
dotnet test

# 运行核心测试（排除 benchmark）
dotnet test --filter "FullyQualifiedName!~Benchmark&FullyQualifiedName!~Throughput"
```

## License

[MIT](LICENSE)