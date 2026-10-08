using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Security;

namespace JevChatAnalyzer.Core.History;

/// <summary>一条判读历史记录。</summary>
public sealed record HistoryRecord
{
    public required string Id { get; init; }

    public required string WxId { get; init; }

    public required string SessionId { get; init; }

    public string SessionTitle { get; init; } = "";

    /// <summary>待分析消息展示文本。</summary>
    public required string FocusText { get; init; }

    /// <summary>待分析消息摘要（server_id 或时间+local_id）。</summary>
    public required string MessageKey { get; init; }

    public required JudgeVerdict Verdict { get; init; }

    /// <summary>写回条目 id（已写回时非空）。</summary>
    public string? WriteBackId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 判读历史本地存储：单 JSON 文件、AES-256-GCM 加密落盘，
/// 文件密钥由 DPAPI 保护（绑定当前 Windows 用户），见 NOTICE 的隐私设计。
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly string _dataPath;
    private readonly string _keyPath;
    private readonly object _lock = new();
    private List<HistoryRecord> _records = [];
    private byte[]? _key;

    public HistoryStore(string? appDataDir = null)
    {
        _dir = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JevChatAnalyzer");
        _dataPath = Path.Combine(_dir, "history.bin");
        _keyPath = Path.Combine(_dir, "history.key");
    }

    private byte[] GetKey()
    {
        if (_key is not null)
        {
            return _key;
        }
        Directory.CreateDirectory(_dir);
        if (File.Exists(_keyPath))
        {
            try
            {
                _key = ProtectedStore.Unprotect(File.ReadAllText(_keyPath));
                if (_key.Length == 32)
                {
                    return _key;
                }
            }
            catch (CryptographicException)
            {
                // 换用户/损坏则重置
            }
        }
        _key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(_keyPath, ProtectedStore.ProtectBytes(_key));
        return _key;
    }

    private void Load()
    {
        if (!File.Exists(_dataPath))
        {
            _records = [];
            return;
        }
        try
        {
            var blob = File.ReadAllBytes(_dataPath);
            if (blob.Length < 12 + 16)
            {
                _records = [];
                return;
            }
            var nonce = blob[..12];
            var cipher = blob[12..^16];
            var tag = blob[^16..];
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(GetKey(), AesGcm.TagByteSizes.MaxSize))
            {
                gcm.Decrypt(nonce, cipher, tag, plain);
            }
            _records = JsonSerializer.Deserialize<List<HistoryRecord>>(Encoding.UTF8.GetString(plain), JsonOptions) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            // 文件损坏或密钥不匹配：不抛出，历史视为空（数据仍在磁盘，不覆盖，直到下次保存）
            _records = [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_dir);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_records, JsonOptions));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(GetKey(), AesGcm.TagByteSizes.MaxSize))
        {
            gcm.Encrypt(nonce, plain, cipher, tag);
        }
        using var fs = File.Create(_dataPath);
        fs.Write(nonce);
        fs.Write(cipher);
        fs.Write(tag);
    }

    /// <summary>追加一条历史（含内存缓存刷新）。</summary>
    public void Add(HistoryRecord record)
    {
        lock (_lock)
        {
            Load();
            _records.RemoveAll(r => r.Id == record.Id);
            _records.Add(record);
            if (_records.Count > 2000)
            {
                _records = _records.OrderByDescending(r => r.CreatedAt).Take(2000).ToList();
            }
            Save();
        }
    }

    /// <summary>最近 N 条（新到旧）。</summary>
    public IReadOnlyList<HistoryRecord> Recent(int count = 100, string? wxId = null)
    {
        lock (_lock)
        {
            Load();
            return _records
                .Where(r => wxId is null || r.WxId == wxId)
                .OrderByDescending(r => r.CreatedAt)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>删除一条。</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            Load();
            _records.RemoveAll(r => r.Id == id);
            Save();
        }
    }

    public void Dispose() { }
}
