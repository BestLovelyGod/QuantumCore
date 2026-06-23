# 量子核（QuantumCore）注意事项

## 🚨 严禁事项（Must Not）

本项目追求极致底层性能，大量使用 `unsafe` 代码、P/Invoke 和非托管内存。**必须彻底杜绝以下问题：**

### 1. 内存访问违规 (Memory Access Violations)
- 数组越界
- 访问已释放内存
- 悬挂指针
- **后果**：进程直接崩溃 (Crash)

### 2. 句柄/资源泄漏 (Handle Leaks)
- 未关闭的文件句柄
- 未释放的 `VirtualAlloc` 内存
- 未 Dispose 的 `MemoryMappedFile`
- **后果**：系统资源耗尽或文件锁定

### 3. 线程安全
- Memory Engine 使用 `ConcurrentDictionary`，但组合操作需要加锁
- Bitcask 使用 `object _lock` 保护写入
- 方言插件不得修改核心 Parser 状态

### 必须使用的防护手段
- `try-finally` 确保资源释放
- `SafeHandle` 管理非托管资源
- 单元测试覆盖所有异常路径

---

## ⚠️ 注意事项

### 1. 性能
- Soft Write：纯内存路径，无磁盘 I/O
- Hard Write：同步 fsync，保证数据安全
- 读取：内存优先，穿透磁盘
- 详细性能可通过 `dotnet test --filter ThroughputBenchmarks` 自行验证

### 2. 数据安全
- Soft Write：进程崩溃会丢最近几秒数据
- Hard Write：同步 fsync，不丢数据
- 崩溃恢复：Bitcask 日志重放重建索引

### 3. 方言插件
- 插件通过 `IDialectPlugin` 接口扩展
- 插件可跳过不支持的语法，但不得改变核心行为
- MySQL/PostgreSQL 已合并为 `BuiltInDialects.cs`
- Redis 因语法差异大，独立维护

### 4. ADO.NET 兼容
- 实现了标准 IDbConnection / IDbCommand / IDataReader
- 参数化查询使用 `@param` 占位符
- 事务通过 BEGIN/COMMIT/ROLLBACK 管理
- 可直接替换 SQLite 连接字符串

### 5. 缓存策略
- TTL 过期：支持精确时间过期
- LRU/LFU 淘汰：可配置策略
- 避免死锁（锁顺序：Memory → Disk）

### 6. 集成方式
- 作为嵌入式库被应用层调用
- 进程内调用，零网络开销
- 必须支持多模块同时使用

---

*最后更新：2026年6月23日*
