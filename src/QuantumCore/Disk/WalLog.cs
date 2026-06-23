// ============================================================================
// 量子核（QuantumCore）— WAL 预写日志（Write-Ahead Log）
//
// 设计原则：
//   1. 每次写操作先写 WAL，再写内存（崩溃恢复保证）
//   2. 后台刷盘成功后，截断已提交的 WAL 条目
//   3. 启动时重放未截断的 WAL 条目恢复内存状态
//
// 条目格式：
//   [4B CRC32][1B op][4B key_len][key][4B field_len][field?][4B val_len][val?][8B score?]
//   CRC32 覆盖范围：op + key_len + key + field_len + field + val_len + val + score
// ============================================================================

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuantumCore.Disk;

/// <summary>
/// WAL 操作类型
/// </summary>
internal enum WalOp : byte
{
    StringSet = 0x01,
    StringDelete = 0x02,
    HashSet = 0x03,
    HashDelete = 0x04,
    ZSetAdd = 0x05,
    ZSetRemove = 0x06
}

/// <summary>
/// WAL 条目（内存中的表示）
/// </summary>
internal readonly record struct WalEntry(
    WalOp Op,
    string Key,
    string? Field,
    string? Value,
    double Score
);

/// <summary>
/// WAL 预写日志
/// 所有写操作在应用到内存之前，先追加到 WAL 文件
/// 崩溃后通过重放 WAL 恢复数据一致性
/// </summary>
internal sealed class WalLog : IDisposable
{
    private readonly string _walPath;
    private FileStream _walFile;
    private readonly object _lock = new();
    private bool _disposed;

    private const string WalFileName = "wal.log";

    public WalLog(string dataDirectory)
    {
        var walDir = Path.Combine(dataDirectory, "wal");
        Directory.CreateDirectory(walDir);
        _walPath = Path.Combine(walDir, WalFileName);

        _walFile = new FileStream(
            _walPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
    }

    /// <summary>
    /// WAL 当前大小（字节）
    /// </summary>
    public long Length
    {
        get
        {
            lock (_lock) return _walFile.Length;
        }
    }

    /// <summary>
    /// WAL 是否有未提交的数据
    /// </summary>
    public bool HasUncommittedData
    {
        get
        {
            lock (_lock) return _walFile.Length > 0;
        }
    }

    // ── 写入操作 ──

    /// <summary>
    /// 追加一条 WAL 条目（在应用到内存之前调用）
    /// </summary>
    public void Append(WalEntry entry)
    {
        var bytes = EncodeEntry(entry);
        lock (_lock)
        {
            _walFile.Seek(0, SeekOrigin.End);
            _walFile.Write(bytes);
            _walFile.Flush();
        }
    }

    /// <summary>
    /// 追加多条 WAL 条目（批量操作时使用）
    /// </summary>
    public void AppendBatch(IReadOnlyList<WalEntry> entries)
    {
        if (entries.Count == 0) return;

        var buffer = new List<byte>(entries.Count * 64);
        foreach (var entry in entries)
        {
            buffer.AddRange(EncodeEntry(entry));
        }

        lock (_lock)
        {
            _walFile.Seek(0, SeekOrigin.End);
            _walFile.Write(buffer.ToArray());
            _walFile.Flush();
        }
    }

    /// <summary>
    /// 截断 WAL（刷盘成功后调用，标记数据已持久化）
    /// </summary>
    public void Truncate()
    {
        lock (_lock)
        {
            _walFile.SetLength(0);
            _walFile.Flush();
        }
    }

    /// <summary>
    /// 重放 WAL 恢复数据（启动时调用）
    /// 返回所有需要恢复的条目
    /// </summary>
    public List<WalEntry> Replay()
    {
        var entries = new List<WalEntry>();

        lock (_lock)
        {
            if (_walFile.Length == 0) return entries;

            _walFile.Seek(0, SeekOrigin.Begin);
            var offset = 0L;

            while (offset < _walFile.Length)
            {
                _walFile.Seek(offset, SeekOrigin.Begin);

                // 读取 CRC + op（5 字节最小 header）
                var headerBuf = new byte[5];
                if (_walFile.Read(headerBuf, 0, 5) < 5) break;

                var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(0));
                var op = (WalOp)headerBuf[4];

                // 读取 key_len
                var keyLenBuf = new byte[4];
                if (_walFile.Read(keyLenBuf, 0, 4) < 4) break;
                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(keyLenBuf);

                if (keyLen < 0 || keyLen > 1024 * 1024) // 1MB key 上限
                {
                    // CRC 不匹配或数据损坏，停止重放
                    break;
                }

                // 读取 key
                var keyBuf = new byte[keyLen];
                if (_walFile.Read(keyBuf, 0, keyLen) < keyLen) break;
                var key = Encoding.UTF8.GetString(keyBuf);

                // 读取 field_len
                var fieldLenBuf = new byte[4];
                if (_walFile.Read(fieldLenBuf, 0, 4) < 4) break;
                var fieldLen = BinaryPrimitives.ReadInt32LittleEndian(fieldLenBuf);

                string? field = null;
                if (fieldLen > 0)
                {
                    var fieldBuf = new byte[fieldLen];
                    if (_walFile.Read(fieldBuf, 0, fieldLen) < fieldLen) break;
                    field = Encoding.UTF8.GetString(fieldBuf);
                }

                // 读取 val_len
                var valLenBuf = new byte[4];
                if (_walFile.Read(valLenBuf, 0, 4) < 4) break;
                var valLen = BinaryPrimitives.ReadInt32LittleEndian(valLenBuf);

                string? value = null;
                if (valLen > 0)
                {
                    var valBuf = new byte[valLen];
                    if (_walFile.Read(valBuf, 0, valLen) < valLen) break;
                    value = Encoding.UTF8.GetString(valBuf);
                }

                // 读取 score（仅 ZSet 操作）
                double score = 0;
                if (op == WalOp.ZSetAdd)
                {
                    var scoreBuf = new byte[8];
                    if (_walFile.Read(scoreBuf, 0, 8) < 8) break;
                    score = BitConverter.Int64BitsToDouble(
                        BinaryPrimitives.ReadInt64LittleEndian(scoreBuf));
                }

                // 计算并校验 CRC32
                // payload = op(1) + keyLen(4) + key + fieldLen(4) + field + valLen(4) + val + score(8)
                var payloadLen = 1 + 4 + keyLen + 4 + (fieldLen > 0 ? fieldLen : 0) + 4 + (valLen > 0 ? valLen : 0);
                if (op == WalOp.ZSetAdd) payloadLen += 8;

                var payloadBuf = new byte[payloadLen];
                var p = 0;
                payloadBuf[p++] = (byte)op;
                BinaryPrimitives.WriteInt32LittleEndian(payloadBuf.AsSpan(p), keyLen); p += 4;
                Buffer.BlockCopy(keyBuf, 0, payloadBuf, p, keyLen); p += keyLen;
                BinaryPrimitives.WriteInt32LittleEndian(payloadBuf.AsSpan(p), fieldLen); p += 4;
                if (fieldLen > 0 && field != null)
                {
                    Buffer.BlockCopy(Encoding.UTF8.GetBytes(field), 0, payloadBuf, p, fieldLen);
                    p += fieldLen;
                }
                BinaryPrimitives.WriteInt32LittleEndian(payloadBuf.AsSpan(p), valLen); p += 4;
                if (valLen > 0 && value != null)
                {
                    Buffer.BlockCopy(Encoding.UTF8.GetBytes(value), 0, payloadBuf, p, valLen);
                    p += valLen;
                }
                if (op == WalOp.ZSetAdd)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(payloadBuf.AsSpan(p),
                        BitConverter.DoubleToInt64Bits(score));
                    p += 8;
                }

                var actualCrc = Crc32.Compute(payloadBuf.AsSpan(0, payloadLen));
                if (storedCrc != actualCrc)
                {
                    // CRC 校验失败，后续数据可能损坏，停止重放
                    break;
                }

                entries.Add(new WalEntry(op, key, field, value ?? "", score));

                // 移动到下一条记录
                offset += 4 /*crc*/ + payloadLen;
            }
        }

        return entries;
    }

    // ── 编解码 ──

    private static byte[] EncodeEntry(WalEntry entry)
    {
        var keyBytes = Encoding.UTF8.GetBytes(entry.Key);
        var fieldBytes = entry.Field != null ? Encoding.UTF8.GetBytes(entry.Field) : null;
        var valBytes = !string.IsNullOrEmpty(entry.Value) ? Encoding.UTF8.GetBytes(entry.Value) : null;

        var fieldLen = fieldBytes?.Length ?? 0;
        var valLen = valBytes?.Length ?? 0;

        // payload = op(1) + keyLen(4) + key + fieldLen(4) + field + valLen(4) + val + score(8 if ZSet)
        var payloadLen = 1 + 4 + keyBytes.Length + 4 + fieldLen + 4 + valLen;
        if (entry.Op == WalOp.ZSetAdd) payloadLen += 8;

        var totalLen = 4 + payloadLen; // CRC(4) + payload
        var buffer = new byte[totalLen];
        var p = 4; // 跳过 CRC 位置

        // op
        buffer[p++] = (byte)entry.Op;

        // key
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(p), keyBytes.Length); p += 4;
        Buffer.BlockCopy(keyBytes, 0, buffer, p, keyBytes.Length); p += keyBytes.Length;

        // field
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(p), fieldLen); p += 4;
        if (fieldLen > 0 && fieldBytes != null)
        {
            Buffer.BlockCopy(fieldBytes, 0, buffer, p, fieldLen); p += fieldLen;
        }

        // value
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(p), valLen); p += 4;
        if (valLen > 0 && valBytes != null)
        {
            Buffer.BlockCopy(valBytes, 0, buffer, p, valLen); p += valLen;
        }

        // score (ZSet only)
        if (entry.Op == WalOp.ZSetAdd)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(p),
                BitConverter.DoubleToInt64Bits(entry.Score));
            p += 8;
        }

        // CRC32 覆盖 payload
        var crc = Crc32.Compute(buffer.AsSpan(4, payloadLen));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), crc);

        return buffer;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_lock)
            {
                _walFile?.Dispose();
            }
        }
    }
}
