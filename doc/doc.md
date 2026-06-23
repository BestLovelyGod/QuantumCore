# 量子核（QuantumCore）参考文档目录

## 📚 文档索引

| 文档 | 说明 |
|------|------|
| [README.md](../README.md) | 项目主文档（特性、使用方式、性能数据） |
| [warning.md](../warning.md) | 注意事项（内存安全、并发、方言插件） |

## 🔗 外部参考

| 参考 | 链接 | 说明 |
|------|------|------|
| Bitcask 论文 | https://dl.acm.org/doi/10.1145/2150976.2150982 | Bitcask 追加日志存储模型 |
| ADO.NET | https://learn.microsoft.com/dotnet/framework/data/ado-net/ | 数据访问标准接口 |
| B+Tree | https://en.wikipedia.org/wiki/B%2B_tree | B+Tree 索引数据结构 |
| MySQL 语法 | https://dev.mysql.com/doc/refman/8.0/en/ | MySQL 语法参考 |
| PostgreSQL 语法 | https://www.postgresql.org/docs/16/ | PostgreSQL 语法参考 |
| Redis 命令 | https://redis.io/commands | Redis 命令参考 |

## 📊 测试覆盖

| 测试文件 | 数量 | 说明 |
|----------|------|------|
| HybridStoreTests.cs | 12 | KV 存储基础操作 |
| HybridIntegrationTests.cs | 5 | 双引擎集成测试 |
| PerformanceTests.cs | 4 | 读写吞吐基准 |
| ConcurrentBenchmarks.cs | 9 | 并发性能基准 |
| WalRecoveryTests.cs | 3 | 崩溃恢复测试 |
| SqlTests.cs | 19 | SQL 引擎测试 |
| AdoNetTests.cs | 13 | ADO.NET + 事务测试 |
| MySqlCompatibilityTests.cs | 13 | MySQL 方言测试 |
| RedisCompatibilityTests.cs | 23 | Redis 命令测试 |
| ThroughputBenchmarks.cs | 1 | 吞吐量基准 |
| **合计** | **101** | **✅ 全部通过** |

---

*最后更新：2026年6月6日*
