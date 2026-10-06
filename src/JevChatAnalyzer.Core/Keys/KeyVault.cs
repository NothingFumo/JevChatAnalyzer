using System.Text.Json;
using System.Text.Json.Serialization;
using JevChatAnalyzer.Core.Security;

namespace JevChatAnalyzer.Core.Keys;

/// <summary>本地保存的一个微信账号密钥条目。敏感字段以 DPAPI 密文存储。</summary>
public sealed record StoredAccount
{
    public required string WxId { get; init; }

    /// <summary>dbKey（64 hex）的 DPAPI 密文（Base64）。</summary>
    public required string DbKeyCipher { get; init; }

    public string? ImageXorKeyCipher { get; init; }

    public string? ImageAesKeyCipher { get; init; }

    /// <summary>数据目录，非敏感，明文保存。</summary>
    public string? DataPath { get; init; }

    /// <summary>来源：auto（rwk.dll 自动读取）或 manual（手动输入）。</summary>
    public string Source { get; init; } = "auto";

    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>微信账号密钥的本地保管库。文件形如 %LOCALAPPDATA%\JevChatAnalyzer\keys.json。</summary>
public sealed class KeyVault
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public KeyVault(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "keys.json");
    }

    private string? _currentWxId;

    /// <summary>当前选中的账号（未设置时取最近保存的一个）。</summary>
    public string? CurrentWxId
    {
        get
        {
            lock (_lock)
            {
                return _currentWxId ?? LoadFile().Accounts.OrderByDescending(a => a.SavedAt).FirstOrDefault()?.WxId;
            }
        }
        set
        {
            lock (_lock)
            {
                _currentWxId = value;
                var state = LoadFile();
                state.CurrentWxId = value;
                SaveFile(state);
            }
        }
    }

    /// <summary>保存或覆盖一个账号的密钥。</summary>
    public void Save(WeChatAccountInfo info, string source = "auto")
    {
        lock (_lock)
        {
            var state = LoadFile();
            var entry = new StoredAccount
            {
                WxId = info.WxId,
                DbKeyCipher = ProtectedStore.ProtectString(info.DbKey),
                ImageXorKeyCipher = info.ImageXorKey is null ? null : ProtectedStore.ProtectString(info.ImageXorKey),
                ImageAesKeyCipher = info.ImageAesKey is null ? null : ProtectedStore.ProtectString(info.ImageAesKey),
                DataPath = info.DataPath,
                Source = source,
                SavedAt = DateTimeOffset.Now,
            };
            state.Accounts.RemoveAll(a => a.WxId == info.WxId);
            state.Accounts.Add(entry);
            if (state.CurrentWxId is null || state.CurrentWxId == info.WxId)
            {
                state.CurrentWxId = info.WxId;
            }
            SaveFile(state);
        }
    }

    /// <summary>保存手动输入的密钥（只填必填项）。</summary>
    public void SaveManual(string wxId, string dbKey, string? dataPath) =>
        Save(new WeChatAccountInfo { WxId = wxId, DbKey = dbKey, DataPath = dataPath }, "manual");

    /// <summary>取出一个账号，dbKey 等已解密为明文。</summary>
    public StoredAccount? Get(string wxId)
    {
        lock (_lock)
        {
            var state = LoadFile();
            var entry = state.Accounts.FirstOrDefault(a => a.WxId == wxId);
            if (entry is null)
            {
                return null;
            }
            return entry with
            {
                DbKeyCipher = ProtectedStore.UnprotectString(entry.DbKeyCipher),
                ImageXorKeyCipher = entry.ImageXorKeyCipher is null ? null : ProtectedStore.UnprotectString(entry.ImageXorKeyCipher),
                ImageAesKeyCipher = entry.ImageAesKeyCipher is null ? null : ProtectedStore.UnprotectString(entry.ImageAesKeyCipher),
            };
        }
    }

    /// <summary>当前账号（或指定账号）的明文密钥信息。</summary>
    public StoredAccount? GetCurrent(string? wxId = null)
    {
        var id = wxId ?? CurrentWxId;
        return id is null ? null : Get(id);
    }

    public IReadOnlyList<string> ListWxIds()
    {
        lock (_lock)
        {
            return LoadFile().Accounts.Select(a => a.WxId).ToList();
        }
    }

    public void Delete(string wxId)
    {
        lock (_lock)
        {
            var state = LoadFile();
            state.Accounts.RemoveAll(a => a.WxId == wxId);
            if (state.CurrentWxId == wxId)
            {
                state.CurrentWxId = null;
            }
            SaveFile(state);
        }
    }

    private VaultFile LoadFile()
    {
        if (!File.Exists(_filePath))
        {
            return new VaultFile();
        }
        try
        {
            return JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_filePath), JsonOptions) ?? new VaultFile();
        }
        catch (JsonException)
        {
            // 文件损坏时重建，密钥需要重新读取/输入。
            return new VaultFile();
        }
    }

    private void SaveFile(VaultFile state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;

        public string? CurrentWxId { get; set; }

        public List<StoredAccount> Accounts { get; set; } = [];
    }
}
