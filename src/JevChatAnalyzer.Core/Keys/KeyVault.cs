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

    /// <summary>账号昵称（非敏感），用于界面显示，不展示 wxid。</summary>
    public string? NickName { get; init; }

    /// <summary>自定义微信号（可能为空）。</summary>
    public string? Alias { get; init; }

    /// <summary>数据目录，非敏感，明文保存。</summary>
    public string? DataPath { get; init; }

    /// <summary>来源：auto（rwk.dll 自动读取）或 manual（手动输入）。</summary>
    public string Source { get; init; } = "auto";

    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>账号摘要（不含密钥），供设置页列表与账号切换使用。</summary>
public sealed record AccountSummary(
    string WxId,
    string? NickName,
    string? Alias,
    string? DataPath,
    string Source,
    DateTimeOffset SavedAt);

/// <summary>微信账号密钥的本地保管库。文件形如 %LOCALAPPDATA%\JevChatAnalyzer\keys.json。</summary>
public sealed class KeyVault
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    private string? _currentWxId;

    public KeyVault(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "keys.json");
    }

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
                NickName = info.NickName,
                Alias = info.Alias,
                Source = source,
                SavedAt = DateTimeOffset.Now
            };
            state.Accounts.RemoveAll(a => a.WxId == info.WxId);
            state.Accounts.Add(entry);
            if (state.CurrentWxId is null || state.CurrentWxId == info.WxId) state.CurrentWxId = info.WxId;
            SaveFile(state);
        }
    }

    /// <summary>保存手动输入的密钥（只填必填项）。</summary>
    public void SaveManual(string wxId, string dbKey, string? dataPath)
    {
        Save(new WeChatAccountInfo { WxId = wxId, DbKey = dbKey, DataPath = dataPath }, "manual");
    }

    /// <summary>取出一个账号，dbKey 等已解密为明文。</summary>
    public StoredAccount? Get(string wxId)
    {
        lock (_lock)
        {
            var state = LoadFile();
            var entry = state.Accounts.FirstOrDefault(a => a.WxId == wxId);
            if (entry is null) return null;
            return entry with
            {
                DbKeyCipher = ProtectedStore.UnprotectString(entry.DbKeyCipher),
                ImageXorKeyCipher = entry.ImageXorKeyCipher is null
                    ? null
                    : ProtectedStore.UnprotectString(entry.ImageXorKeyCipher),
                ImageAesKeyCipher = entry.ImageAesKeyCipher is null
                    ? null
                    : ProtectedStore.UnprotectString(entry.ImageAesKeyCipher)
            };
        }
    }

    /// <summary>当前账号（或指定账号）的明文密钥信息。</summary>
    public StoredAccount? GetCurrent(string? wxId = null)
    {
        var id = wxId ?? CurrentWxId;
        return id is null ? null : Get(id);
    }

    /// <summary>账号列表（不含任何密钥，供设置页展示/切换）。</summary>
    public IReadOnlyList<AccountSummary> ListAccounts()
    {
        lock (_lock)
        {
            return LoadFile().Accounts
                .OrderByDescending(a => a.SavedAt)
                .Select(a => new AccountSummary(a.WxId, a.NickName, a.Alias, a.DataPath, a.Source, a.SavedAt))
                .ToList();
        }
    }

    /// <summary>补写昵称（连接成功后从联系人库解析出来）。</summary>
    public void SetNickName(string wxId, string? nickName)
    {
        lock (_lock)
        {
            var state = LoadFile();
            var idx = state.Accounts.FindIndex(a => a.WxId == wxId);
            if (idx < 0 || string.IsNullOrWhiteSpace(nickName) || state.Accounts[idx].NickName == nickName) return;
            state.Accounts[idx] = state.Accounts[idx] with { NickName = nickName };
            SaveFile(state);
        }
    }

    /// <summary>改写某账号的 dbKey（64 位 hex）；格式不对返回 false。</summary>
    public bool SetDbKey(string wxId, string dbKeyHex)
    {
        var key = dbKeyHex.Trim().ToLowerInvariant();
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) return false;
        lock (_lock)
        {
            var state = LoadFile();
            var idx = state.Accounts.FindIndex(a => a.WxId == wxId);
            if (idx < 0) return false;
            state.Accounts[idx] = state.Accounts[idx] with
            {
                DbKeyCipher = ProtectedStore.ProtectString(key)
            };
            SaveFile(state);
            return true;
        }
    }

    /// <summary>改写某账号的数据目录（用户手动选择后）。</summary>
    public void SetDataPath(string wxId, string? dataPath)
    {
        lock (_lock)
        {
            var state = LoadFile();
            var idx = state.Accounts.FindIndex(a => a.WxId == wxId);
            if (idx < 0) return;
            state.Accounts[idx] = state.Accounts[idx] with { DataPath = dataPath };
            SaveFile(state);
        }
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
            if (state.CurrentWxId == wxId) state.CurrentWxId = null;
            SaveFile(state);
        }
    }

    private VaultFile LoadFile()
    {
        if (!File.Exists(_filePath)) return new VaultFile();
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
