// ============================================================================
// 量子核（QuantumCore）— Bitcask 追加日志引擎
// 顺序写 + 内存索引 + 定期合并压缩
//
// 文件格式（每条记录）：
//   [4B CRC32] [4B key_len] [8B timestamp] [key_len B key] [4B val_len] [val_len B value]
//   CRC32 覆盖范围：key_len + timestamp + key + val_len + value
//   总记录头 = 16 字节 + key + value
// ============================================================================

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QuantumCore.Disk;

/// <summary>
/// Bitcask 记录 — 一条追加日志中的数据
/// </summary>
internal readonly record struct BitcaskEntry(
    long Offset,
    long Timestamp,
    string Key,
    string? Value,       // null 表示删除标记
    int TotalSize
);

/// <summary>
/// Bitcask 追加日志引擎
/// 所有写入顺序追加到日志文件，内存中维护 key → offset 索引
/// </summary>
internal sealed class Bitcask : IDisposable
{
    private readonly string _logDir;
    private FileStream _activeLog;
    private readonly Dictionary<string, BitcaskEntry> _index = new();
    private readonly object _lock = new();
    private long _currentOffset;
    private bool _disposed;
    private int _compacting; // 防止并发压缩

    // ── 常量 ──
    private const string ActiveLogName = "active.log";
    private const string OldLogPrefix = "segment_";

    public Bitcask(string dataDirectory)
    {
        _logDir = Path.Combine(dataDirectory, "bitcask");
        Directory.CreateDirectory(_logDir);

        var activePath = Path.Combine(_logDir, ActiveLogName);
        _activeLog = new FileStream(
            activePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024, // 64KB 缓冲区
            FileOptions.WriteThrough);

        _currentOffset = _activeLog.Length;

        // 启动时重放日志重建索引（异常时释放文件句柄）
        try
        {
            RebuildIndex();
        }
        catch
        {
            _activeLog.Dispose();
            _activeLog = null!;
            throw;
        }
    }

    /// <summary>
    /// 写入一条记录（追加到日志末尾）
    /// </summary>
    public void Set(string key, string value)
    {
        lock (_lock)
        {
            var record = EncodeEntry(key, value);

            _activeLog.Seek(_currentOffset, SeekOrigin.Begin);
            _activeLog.Write(record);
            _activeLog.Flush();

            _index[key] = new BitcaskEntry(
                Offset: _currentOffset,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Key: key,
                Value: value,
                TotalSize: record.Length);

            _currentOffset += record.Length;
        }
    }

    /// <summary>
    /// 写入删除标记（tombstone）
    /// </summary>
    public void Delete(string key)
    {
        lock (_lock)
        {
            var record = EncodeEntry(key, null);

            _activeLog.Seek(_currentOffset, SeekOrigin.Begin);
            _activeLog.Write(record);
            _activeLog.Flush();

            _index.Remove(key);

            _currentOffset += record.Length;
        }
    }

    /// <summary>
    /// 读取一条记录（通过索引直接定位文件偏移量）
    /// </summary>
    public string? Get(string key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var entry))
                return null;

            // 检查 tombstone（删除标记）：valLen < 0 表示已删除
            if (entry.Value == null)
                return null;

            _activeLog.Seek(entry.Offset, SeekOrigin.Begin);
            var buffer = new byte[entry.TotalSize];
            _activeLog.ReadExactly(buffer);

            var decoded = DecodeEntry(buffer);
            return decoded.Value;
        }
    }

    /// <summary>
    /// 检查 key 是否存在
    /// </summary>
    public bool Exists(string key)
    {
        lock (_lock)
        {
            return _index.ContainsKey(key);
        }
    }

    /// <summary>
    /// 获取所有 key（用于范围查询或遍历）
    /// </summary>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_lock)
            {
                return _index.Keys.ToList().AsReadOnly();
            }
        }
    }

    public long Count
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }

    // ── 压缩合并 ──

    /// <summary>
    /// 合并压缩：将活跃日志中有效数据重写到新文件，删除旧数据
    /// 使用临时文件 + File.Replace 保证原子替换，避免句柄损坏
    /// </summary>
    public async Task CompactAsync()
    {
        // 防止并发压缩
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
            return;

        try
        {
            BitcaskEntry[] snapshot;
            lock (_lock)
            {
                snapshot = _index.Values.ToArray();
            }

        // 1. 将当前活跃日志转为旧段文件
        var segmentName = $"{OldLogPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.log";
        var segmentPath = Path.Combine(_logDir, segmentName);
        var activePath = Path.Combine(_logDir, ActiveLogName);
        var tempPath = activePath + ".compacting";

        lock (_lock)
        {
            _activeLog.Flush();
            File.Move(activePath, segmentPath, overwrite: true);
        }

        // 2. 重写有效数据到临时文件
        var newOffset = 0L;
        var newIndex = new Dictionary<string, BitcaskEntry>();

        using (var segmentStream = new FileStream(segmentPath, FileMode.Open, FileAccess.Read))
        using (var tempLog = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough))
        {
            foreach (var entry in snapshot)
            {
                segmentStream.Seek(entry.Offset, SeekOrigin.Begin);
                var buffer = new byte[entry.TotalSize];
                segmentStream.ReadExactly(buffer);

                var record = EncodeEntry(entry.Key, entry.Value!);
                tempLog.Write(record);

                newIndex[entry.Key] = new BitcaskEntry(
                    Offset: newOffset,
                    Timestamp: entry.Timestamp,
                    Key: entry.Key,
                    Value: entry.Value,
                    TotalSize: record.Length);

                newOffset += record.Length;
            }

            tempLog.Flush();
        }

        // 3. 原子替换：用临时文件替换活跃日志，再重新打开
        lock (_lock)
        {
            // 关闭旧句柄（此时 _activeLog 已被 move 为 segment，句柄仍有效但文件已改名）
            _activeLog.Dispose();

            // 使用 File.Replace 原子替换（如果 tempPath 不存在则回退到 Move）
            if (File.Exists(tempPath))
            {
                // File.Replace 要求目标文件存在，但 active.log 已被 move 走了，需要先创建
                File.WriteAllBytes(activePath, Array.Empty<byte>());
                File.Replace(tempPath, activePath, null);
            }

            // 重新打开活跃日志
            _activeLog = new FileStream(
                activePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);

            _currentOffset = newOffset;
            _index.Clear();
            foreach (var kv in newIndex)
                _index[kv.Key] = kv.Value;
        }

        // 4. 清理旧段文件
        File.Delete(segmentPath);

        await Task.CompletedTask;
        }
        finally
        {
            Interlocked.Exchange(ref _compacting, 0);
        }
    }

    /// <summary>
    /// 删除旧的段文件（合并后清理）
    /// </summary>
    public void CleanupOldSegments()
    {
        foreach (var file in Directory.GetFiles(_logDir, $"{OldLogPrefix}*.log"))
        {
            File.Delete(file);
        }
    }

    // ── 编解码 ──

    private const int CrcSize = 4;
    private const int HeaderSize = CrcSize + 4 + 8; // CRC(4) + keyLen(4) + timestamp(8) = 16

    /// <summary>
    /// 编码一条记录为字节数组
    /// 格式：[4B CRC32][4B key_len][8B timestamp][key_len B key][4B val_len][val_len B value]
    /// </summary>
    private static byte[] EncodeEntry(string key, string? value)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var valueBytes = value != null ? System.Text.Encoding.UTF8.GetBytes(value) : Array.Empty<byte>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var valLen = value != null ? valueBytes.Length : -1;

        // payload = keyLen(4) + timestamp(8) + key + valLen(4) + value
        var payloadSize = 4 + 8 + keyBytes.Length + 4 + valueBytes.Length;
        var totalSize = CrcSize + payloadSize;
        var buffer = new byte[totalSize];
        var offset = CrcSize; // 跳过 CRC 位置（最后计算）

        // key_len (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), keyBytes.Length);
        offset += 4;

        // timestamp (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), timestamp);
        offset += 8;

        // key
        Buffer.BlockCopy(keyBytes, 0, buffer, offset, keyBytes.Length);
        offset += keyBytes.Length;

        // val_len (4 bytes, -1 表示删除标记)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), valLen);
        offset += 4;

        // value
        if (value != null)
        {
            Buffer.BlockCopy(valueBytes, 0, buffer, offset, valueBytes.Length);
        }

        // CRC32 覆盖 payload 部分
        var crc = Crc32.Compute(buffer.AsSpan(CrcSize, payloadSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), crc);

        return buffer;
    }

    /// <summary>
    /// 从字节数组解码一条记录，校验 CRC32
    /// </summary>
    private static BitcaskEntry DecodeEntry(byte[] buffer)
    {
        var offset = 0;

        // 读取 CRC
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
        offset += 4;

        var keyLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        offset += 4;

        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset));
        offset += 8;

        var key = System.Text.Encoding.UTF8.GetString(buffer, offset, keyLen);
        offset += keyLen;

        var valLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        offset += 4;

        string? value = null;
        if (valLen >= 0)
        {
            value = System.Text.Encoding.UTF8.GetString(buffer, offset, valLen);
        }

        // CRC32 校验：覆盖 payload 部分
        var actualCrc = Crc32.Compute(buffer.AsSpan(CrcSize, buffer.Length - CrcSize));
        if (storedCrc != actualCrc)
        {
            throw new InvalidDataException(
                $"Bitcask CRC 校验失败: key='{key}', 存储={storedCrc:X8}, 计算={actualCrc:X8}");
        }

        return new BitcaskEntry(0, timestamp, key, value, buffer.Length);
    }

    /// <summary>
    /// 重放日志文件重建内存索引，校验每条记录的 CRC32
    /// </summary>
    private void RebuildIndex()
    {
        _activeLog.Seek(0, SeekOrigin.Begin);
        var offset = 0L;
        var corruptedCount = 0;

        while (offset < _activeLog.Length)
        {
            _activeLog.Seek(offset, SeekOrigin.Begin);

            // 读取 header（CRC + key_len + timestamp）
            var headerBuf = new byte[HeaderSize];
            if (_activeLog.Read(headerBuf, 0, HeaderSize) < HeaderSize) break;

            var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(0));
            var keyLen = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(4));
            var timestamp = BinaryPrimitives.ReadInt64LittleEndian(headerBuf.AsSpan(8));

            if (keyLen <= 0 || keyLen > 1024 * 1024) break; // 安全检查

            // 读取 key
            var keyBuf = new byte[keyLen];
            _activeLog.ReadExactly(keyBuf);
            var key = System.Text.Encoding.UTF8.GetString(keyBuf);

            // 读取 val_len
            var valLenBuf = new byte[4];
            _activeLog.ReadExactly(valLenBuf);
            var valLen = BinaryPrimitives.ReadInt32LittleEndian(valLenBuf);

            // 安全检查：valLen 上限（防止损坏日志导致 OOM）
            if (valLen > 1024 * 1024) break;

            // 读取 value
            var valDataLen = valLen >= 0 ? valLen : 0;
            var valueBuf = new byte[valDataLen];
            if (valDataLen > 0) _activeLog.ReadExactly(valueBuf);

            // CRC32 校验：覆盖 payload（keyLen + timestamp + key + valLen + value）
            var totalSize = HeaderSize + keyLen + 4 + valDataLen;
            var fullBuf = new byte[totalSize];
            Buffer.BlockCopy(headerBuf, 0, fullBuf, 0, HeaderSize);
            Buffer.BlockCopy(keyBuf, 0, fullBuf, HeaderSize, keyLen);
            Buffer.BlockCopy(valLenBuf, 0, fullBuf, HeaderSize + keyLen, 4);
            if (valDataLen > 0) Buffer.BlockCopy(valueBuf, 0, fullBuf, HeaderSize + keyLen + 4, valDataLen);

            var actualCrc = Crc32.Compute(fullBuf.AsSpan(CrcSize, totalSize - CrcSize));
            if (storedCrc != actualCrc)
            {
                corruptedCount++;
                offset += totalSize;
                continue; // 跳过损坏记录，继续重建
            }

            if (valLen >= 0)
            {
                var value = System.Text.Encoding.UTF8.GetString(valueBuf);
                _index[key] = new BitcaskEntry(offset, timestamp, key, value, totalSize);
            }
            else
            {
                // 删除标记，从索引中移除
                _index.Remove(key);
            }

            offset += totalSize;
        }

        _currentOffset = offset;
        if (corruptedCount > 0)
        {
            Console.Error.WriteLine($"Bitcask: 重建索引时跳过 {corruptedCount} 条损坏记录（CRC 校验失败）");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_lock)
            {
                _activeLog?.Dispose();
            }
        }
    }
}

// ============================================================================
// CRC32 计算（Castagnoli 多项式 0x1EDC6F41）
// 用于 Bitcask 记录完整性校验
// ============================================================================

internal static class Crc32
{
    private static readonly uint[] CrcTable = GenerateTable();

    private static uint[] GenerateTable()
    {
        const uint polynomial = 0x1EDC6F41; // Castagnoli
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>计算 CRC32（Castagnoli）</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
