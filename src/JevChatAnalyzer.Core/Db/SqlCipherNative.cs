using System.Runtime.InteropServices;
using System.Text;

namespace JevChatAnalyzer.Core.Db;

/// <summary>原生 SQLCipher 调用失败。</summary>
public sealed class SqliteNativeException : Exception
{
    public SqliteNativeException(string message, int rc = -1) : base(message)
    {
        ResultCode = rc;
    }

    public int ResultCode { get; }
}

/// <summary>
///     微信同源 sqlcipher.dll（SQLite 3.27.2 定制版）的最小 P/Invoke 包装。
///     以原始 32 字节 dbKey（64 hex）经 sqlite3_key 打开加密库。
///     本类是唯一的数据访问路径：纯 C# 页解密实现已删除，不保留回退。
/// </summary>
public sealed class SqlCipherNative : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteOpenReadWrite = 0x00000002;
    private const int SqliteOpenCreate = 0x00000004;
    private const int SqliteTypeInteger = 1;
    private const int SqliteTypeFloat = 2;
    private const int SqliteTypeText = 3;
    private const int SqliteTypeBlob = 4;

    private static volatile nint _lib;
    private static readonly object LoadLock = new();

    private readonly nint _db;

    /// <summary>句柄读写闸门：Dispose 与查询并发时，必须保证 close 不会发生在 prepare 之前。</summary>
    private readonly object _gate = new();

    private bool _disposed;

    private SqlCipherNative(string dbPath, string keyHex, bool readOnly, bool create = false)
    {
        var keyBytes = ParseKey(keyHex);
        var flags = readOnly ? SqliteOpenReadOnly : SqliteOpenReadWrite | (create ? SqliteOpenCreate : 0);
        var rc = open_v2(dbPath, out _db, flags, nint.Zero);
        if (rc != SqliteOk || _db == 0)
        {
            // sqlite3_open_v2 失败时仍可能返回一个句柄，必须显式关闭
            if (_db != 0)
            {
                _ = close(_db);
                _db = 0;
            }

            throw new SqliteNativeException($"打开数据库失败：{dbPath}（rc={rc}）", rc);
        }

        rc = sqlite3_key(_db, keyBytes, keyBytes.Length);
        if (rc != SqliteOk)
        {
            var msg = ErrMsg();
            close(_db);
            _db = 0;
            throw new SqliteNativeException($"注入密钥失败（rc={rc}）：{msg}", rc);
        }

        // 密钥校验：错误密钥在 sqlite_master 访问时报 SQLITE_NOTADB
        try
        {
            _ = Query("SELECT count(*) FROM sqlite_master");
        }
        catch
        {
            close(_db);
            _db = 0;
            throw new SqliteNativeException(
                "密钥校验失败：dbKey 与该数据库不符。请在设置中重新读取或核对密钥。");
        }
    }

    /// <summary>当前进程加载的 dll 路径（诊断用）。</summary>
    public static string? LoadedDllPath { get; private set; }

    private static nint Lib
    {
        get
        {
            if (_lib != 0) return _lib;
            lock (LoadLock)
            {
                if (_lib != 0) return _lib;
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "sqlcipher.dll"),
                    "sqlcipher.dll" // PATH / 应用目录
                };
                foreach (var candidate in candidates)
                {
                    if (!NativeLibrary.TryLoad(candidate, out var lib)) continue;
                    LoadedDllPath = candidate;
                    _lib = lib;
                    return lib;
                }

                throw new SqliteNativeException(
                    "未找到 sqlcipher.dll（微信同源 SQLCipher 3.27.2）。请确认应用完整性，" +
                    "或将其放置在程序目录下。");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var handle = _db;
            if (handle != 0) _ = close(handle);
        }
    }

    /// <summary>只读打开加密库并注入密钥，执行一次 sqlite_master 查询验证密钥正确性。</summary>
    public static SqlCipherNative OpenReadOnly(string dbPath, string keyHex)
    {
        return new SqlCipherNative(dbPath, keyHex, true);
    }

    /// <summary>读写打开加密库（仅用于写回：调用方必须先把原文快照写入台账）。</summary>
    public static SqlCipherNative OpenReadWrite(string dbPath, string keyHex)
    {
        return new SqlCipherNative(dbPath, keyHex, false);
    }

    /// <summary>读写打开本应用自己的加密库（不存在则创建），用于判读台账。</summary>
    public static SqlCipherNative OpenReadWriteCreate(string dbPath, string keyHex)
    {
        return new SqlCipherNative(dbPath, keyHex, false, true);
    }

    private static byte[] ParseKey(string keyHex)
    {
        var hex = keyHex.Trim();
        if (hex.Length != 64 || hex.Any(c => !Uri.IsHexDigit(c)))
            throw new SqliteNativeException($"dbKey 格式错误：需要 64 位 hex，收到 {hex.Length} 字符。");
        return Convert.FromHexString(hex);
    }

    /// <summary>执行查询并物化全部行。文本按 UTF-8 解码，BLOB 复制为 byte[]。</summary>
    public List<Row> Query(string sql)
    {
        lock (_gate)
        {
            return QueryCore(sql);
        }
    }

    private List<Row> QueryCore(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rows = new List<Row>();
        var zSql = Utf8(sql);
        var rc = prepare_v2(_db, zSql, zSql.Length, out var stmt, nint.Zero);
        if (rc != SqliteOk) throw new SqliteNativeException($"SQL 准备失败（rc={rc}）：{ErrMsg()} {sql}", rc);
        try
        {
            var cols = column_count(stmt);
            var names = new string[cols];
            for (var i = 0; i < cols; i++) names[i] = PtrToStr(column_name(stmt, i)) ?? $"c{i}";
            while ((rc = step(stmt)) == SqliteRow)
            {
                var dict = new Dictionary<string, object?>(cols, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < cols; i++)
                    dict[names[i]] = column_type(stmt, i) switch
                    {
                        SqliteTypeInteger => column_int64(stmt, i),
                        SqliteTypeFloat => column_double(stmt, i),
                        SqliteTypeText => PtrToStr(column_text(stmt, i)),
                        SqliteTypeBlob => ReadBlob(stmt, i),
                        _ => null
                    };
                rows.Add(new Row(dict));
            }

            if (rc != SqliteDone) throw new SqliteNativeException($"查询失败（rc={rc}）：{ErrMsg()}", rc);
        }
        finally
        {
            finalize(stmt);
        }

        return rows;
    }

    /// <summary>执行无结果语句（DDL/DML）。仅供写回功能使用。</summary>
    public void Execute(string sql)
    {
        lock (_gate)
        {
            ExecuteCore(sql);
        }
    }

    private void ExecuteCore(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var zSql = Utf8(sql);
        var rc = prepare_v2(_db, zSql, zSql.Length, out var stmt, nint.Zero);
        if (rc != SqliteOk) throw new SqliteNativeException($"SQL 准备失败（rc={rc}）：{ErrMsg()} {sql}", rc);
        try
        {
            rc = step(stmt);
            if (rc is not (SqliteDone or SqliteRow)) throw new SqliteNativeException($"执行失败（rc={rc}）：{ErrMsg()}", rc);
        }
        finally
        {
            finalize(stmt);
        }
    }

    /// <summary>最近一条 DML 影响的行数（sqlite3_changes）。</summary>
    public int LastChanges()
    {
        lock (_gate)
        {
            return _disposed ? 0 : changes(_db);
        }
    }

    private string ErrMsg()
    {
        return PtrToStr(errmsg(_db)) ?? "未知错误";
    }

    private static byte[]? ReadBlob(nint stmt, int i)
    {
        var ptr = column_blob(stmt, i);
        var n = (int)column_bytes(stmt, i);
        if (ptr == nint.Zero || n == 0) return n == 0 ? [] : null;
        var bytes = new byte[n];
        Marshal.Copy(ptr, bytes, 0, n);
        return bytes;
    }

    private static byte[] Utf8(string s)
    {
        return Encoding.UTF8.GetBytes(s);
    }

    private static string? PtrToStr(nint ptr)
    {
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    [DllImport("sqlcipher", EntryPoint = "sqlite3_open_v2")]
    private static extern int open_v2(string filename, out nint ppDb, int flags, nint zVfs);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_key")]
    private static extern int sqlite3_key(nint db, byte[] key, int nKey);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_prepare_v2")]
    private static extern int prepare_v2(nint db, byte[] zSql, int nByte, out nint ppStmt, nint pzTail);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_step")]
    private static extern int step(nint stmt);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_count")]
    private static extern int column_count(nint stmt);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_name")]
    private static extern nint column_name(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_type")]
    private static extern int column_type(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_int64")]
    private static extern long column_int64(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_double")]
    private static extern double column_double(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_text")]
    private static extern nint column_text(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_blob")]
    private static extern nint column_blob(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_column_bytes")]
    private static extern long column_bytes(nint stmt, int iCol);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_finalize")]
    private static extern int finalize(nint stmt);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_close")]
    private static extern int close(nint db);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_errmsg")]
    private static extern nint errmsg(nint db);

    [DllImport("sqlcipher", EntryPoint = "sqlite3_changes")]
    private static extern int changes(nint db);

    /// <summary>一行数据的宽松视图（列名 → 值；long/double/string/byte[]/null）。</summary>
    public sealed class Row
    {
        private readonly Dictionary<string, object?> _values;

        public Row(Dictionary<string, object?> values)
        {
            _values = values;
        }

        public int ColumnCount => _values.Count;
        public IReadOnlyCollection<string> Columns => _values.Keys;

        public object? this[string column] => _values.GetValueOrDefault(column);

        public long Int(string column)
        {
            return AsLong(_values[column]);
        }

        public long? IntOrNull(string column)
        {
            return _values.TryGetValue(column, out var v) && v is not null ? AsLong(v) : null;
        }

        public string? Text(string column)
        {
            return _values.TryGetValue(column, out var v) ? v as string : null;
        }

        public byte[]? Blob(string column)
        {
            return _values.TryGetValue(column, out var v) ? v as byte[] : null;
        }

        private static long AsLong(object? v)
        {
            return v switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                string s when long.TryParse(s, out var l) => l,
                _ => 0
            };
        }
    }
}
