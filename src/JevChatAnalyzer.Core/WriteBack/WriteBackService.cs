using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using JevChatAnalyzer.Core.Db;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.WriteBack;

/// <summary>写回前置条件不满足（微信在运行 / 文件占用等）。</summary>
public sealed class WriteBackException : Exception
{
    public WriteBackException(string message) : base(message) { }
}

/// <summary>一次写回的台账条目（撤销的唯一事实来源）。</summary>
public sealed record WriteBackEntry
{
    public required string Id { get; init; }

    public required string WxId { get; init; }

    /// <summary>目标会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>被写入的分片库文件名（如 message_0.db）。</summary>
    public required string DbFileName { get; init; }

    /// <summary>写入的表名。</summary>
    public required string TableName { get; init; }

    /// <summary>插入的 local_id 列表（撤销按行删除）。</summary>
    public required IReadOnlyList<long> InsertedLocalIds { get; init; }

    /// <summary>写入前的整库备份文件名（backups 目录内）。</summary>
    public required string BackupFileName { get; init; }

    /// <summary>备份文件 SHA-256（校验还原源完整）。</summary>
    public required string BackupSha256 { get; init; }

    /// <summary>写入时间。</summary>
    public DateTimeOffset WrittenAt { get; init; }

    /// <summary>撤销时间；null = 尚未撤销。</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// 把判读结论以「引用消息」写回微信数据库：写入前自动整库备份；
/// 撤销 = 按台账逐行 DELETE（不影响其后新消息）；恢复原库 = 整库替换备份文件（回到写入前时点）。
/// 微信必须处于退出状态才允许写入。
/// </summary>
public sealed class WriteBackService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _backupDir;
    private readonly string _indexPath;

    public WriteBackService(string? appDataDir = null)
    {
        var root = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JevChatAnalyzer");
        _backupDir = Path.Combine(root, "backups");
        _indexPath = Path.Combine(_backupDir, "index.json");
    }

    /// <summary>写回是否可用（微信未在运行）。</summary>
    public static bool IsWeChatClosed() =>
        Process.GetProcessesByName("Weixin").Length == 0 && Process.GetProcessesByName("WeChat").Length == 0;

    /// <summary>历史台账。</summary>
    public IReadOnlyList<WriteBackEntry> ListEntries()
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<WriteBackEntry>>(File.ReadAllText(_indexPath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void AppendEntry(WriteBackEntry entry)
    {
        Directory.CreateDirectory(_backupDir);
        var entries = ListEntries().ToList();
        entries.Add(entry);
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(entries, JsonOptions));
    }

    private void UpdateEntry(WriteBackEntry updated)
    {
        var entries = ListEntries().ToList();
        var idx = entries.FindIndex(e => e.Id == updated.Id);
        if (idx >= 0)
        {
            entries[idx] = updated;
        }
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(entries, JsonOptions));
    }

    // ── 待写队列：判读（微信运行中，只读）时入队；微信退出后由监视器自动落库 ──

    private string PendingPath => Path.Combine(_backupDir, "pending.json");

    /// <summary>一条待写回的判读结论（落库所需的全部快照）。</summary>
    public sealed record PendingWrite(
        string Id,
        string WxId,
        string SessionId,
        string ShardPath,
        string TableName,
        ChatMessage FocusMessage,
        JudgeVerdict Verdict,
        string QuoteText,
        DateTimeOffset QueuedAt);

    /// <summary>入队一条待写回（幂等：同一条消息只保留最新结论）。</summary>
    public void EnqueuePending(string wxId, string sessionId, ChatMessage focusMessage,
        JudgeVerdict verdict, string quoteText)
    {
        if (focusMessage.IsMine)
        {
            throw new WriteBackException("只能对「对方发送的消息」自动写回。");
        }
        var pending = LoadPending();
        pending.RemoveAll(p => p.SessionId == sessionId && p.FocusMessage.LocalId == focusMessage.LocalId);
        pending.Add(new PendingWrite(
            Id: $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            WxId: wxId,
            SessionId: sessionId,
            ShardPath: focusMessage.Location.ShardPath,
            TableName: focusMessage.Location.TableName,
            FocusMessage: focusMessage,
            Verdict: verdict,
            QuoteText: quoteText,
            QueuedAt: DateTimeOffset.Now));
        SavePending(pending);
    }

    public IReadOnlyList<PendingWrite> ListPending() => LoadPending();

    public void RemovePending(string id)
    {
        var pending = LoadPending();
        pending.RemoveAll(p => p.Id == id);
        SavePending(pending);
    }

    /// <summary>落库全部待写项（微信退出后调用）。返回 (成功条目, 失败原因列表)。</summary>
    public IReadOnlyList<(WriteBackEntry? Entry, string? Error)> FlushPending(string dbKeyHex)
    {
        var results = new List<(WriteBackEntry? Entry, string? Error)>();
        foreach (var p in LoadPending())
        {
            try
            {
                var entry = WriteQuoteMessage(p.ShardPath, p.TableName, dbKeyHex,
                    p.WxId, p.SessionId, p.FocusMessage, p.Verdict, p.QuoteText);
                results.Add((entry, null));
            }
            catch (Exception e)
            {
                // 自动落库在后台执行，任何失败都转为结果项而不是中断整批
                results.Add((null, $"{p.SessionId}#{p.FocusMessage.LocalId}: {e.Message}"));
            }
        }
        if (results.All(r => r.Error is null))
        {
            SavePending([]);
        }
        else
        {
            // 只清掉成功的，失败的保留待下次
            var failedKeys = results.Where(r => r.Error is not null)
                .Select(r => r.Error!).ToHashSet();
            SavePending(LoadPending().Where(p => failedKeys.Any(f => f.Contains($"{p.SessionId}#{p.FocusMessage.LocalId}"))).ToList());
        }
        return results;
    }

    private List<PendingWrite> LoadPending()
    {
        if (!File.Exists(PendingPath))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<PendingWrite>>(File.ReadAllText(PendingPath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SavePending(List<PendingWrite> pending)
    {
        Directory.CreateDirectory(_backupDir);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending, JsonOptions));
    }

    /// <summary>
    /// 把判读结论写回为一条引用消息（引用「对方发送的被分析消息」）。
    /// 返回台账条目。安全链：微信退出检查 → 整库备份 → 显式 17 列 INSERT。
 /// 仅接受对方发送的消息（IsMine=false）
    /// </summary>
    public WriteBackEntry WriteQuoteMessage(
        string dbShardPath, string tableName, string dbKeyHex,
        string wxId, string sessionId, ChatMessage focusMessage, JudgeVerdict verdict,
        string quoteText)
    {
        if (focusMessage.IsMine)
        {
            throw new WriteBackException("只能对「对方发送的消息」写回引用（本软件的分析对象）。");
        }
        if (!IsWeChatClosed())
        {
            throw new WriteBackException("微信正在运行。为避免写入冲突与数据不一致，请先完全退出微信再执行写回。");
        }
        if (File.Exists(dbShardPath + "-wal") && new FileInfo(dbShardPath + "-wal").Length > 0)
        {
            throw new WriteBackException(
                "目标数据库存在未合并的 WAL 日志。请先启动微信并正常退出一次（让微信自行合并日志），再执行写回。");
        }

        Directory.CreateDirectory(_backupDir);
        var id = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var backupName = $"{id}-{Path.GetFileName(dbShardPath)}.bak";
        var backupPath = Path.Combine(_backupDir, backupName);
        File.Copy(dbShardPath, backupPath, overwrite: true);
        var sha = Sha256Hex(File.ReadAllBytes(backupPath));

        var content = BuildQuoteXml(verdict, quoteText, focusMessage, sessionId);
        var inserted = new List<long>();
        using (var db = SqlCipherNative.OpenReadWrite(dbShardPath, dbKeyHex))
        {
            var myRowId = db.Query($"SELECT rowid FROM Name2Id WHERE user_name = '{Esc(wxId)}'");
            if (myRowId.Count == 0)
            {
                throw new WriteBackException($"在 {Path.GetFileName(dbShardPath)} 的 Name2Id 中找不到当前账号（{wxId}）。");
            }
            var rowId = myRowId[0].Int("rowid");
            var nextLocalId = db.Query($"SELECT COALESCE(MAX(local_id),0)+1 AS next FROM \"{Esc(tableName)}\"")[0].Int("next");
            var now = DateTimeOffset.Now;
            var sortSeq = now.ToUnixTimeMilliseconds();
            var serverId = GenerateServerId();

            db.Execute("BEGIN");
            try
            {
                db.Execute($"""
                    INSERT INTO "{Esc(tableName)}"
                    (local_id, server_id, local_type, sort_seq, real_sender_id, create_time,
                     status, upload_status, download_status, server_seq, origin_source, source,
                     message_content, compress_content, packed_info_data)
                    VALUES
                    ({nextLocalId}, {serverId}, {(57L << 32) | 49}, {sortSeq}, {rowId}, {now.ToUnixTimeSeconds()},
                     4, 0, 0, 0, 5, '', '{Esc(content)}', NULL, NULL)
                    """);
                db.Execute("COMMIT");
                inserted.Add(nextLocalId);
            }
            catch
            {
                try { db.Execute("ROLLBACK"); } catch { /* 尽力回滚 */ }
                throw;
            }
        }

        var entry = new WriteBackEntry
        {
            Id = id,
            WxId = wxId,
            SessionId = sessionId,
            DbFileName = Path.GetFileName(dbShardPath),
            TableName = tableName,
            InsertedLocalIds = inserted,
            BackupFileName = backupName,
            BackupSha256 = sha,
            WrittenAt = DateTimeOffset.Now,
        };
        AppendEntry(entry);
        return entry;
    }

    /// <summary>撤销一次写回：按台账删除插入的行（不影响其后新消息）。返回删除的行数。</summary>
    public int Revoke(WriteBackEntry entry, string dbKeyHex)
    {
        if (!IsWeChatClosed())
        {
            throw new WriteBackException("微信正在运行，请先完全退出微信再撤销。");
        }
        var shardPath = FindShardPath(entry);
        if (shardPath is null)
        {
            throw new WriteBackException($"找不到目标分片库 {entry.DbFileName}。账号目录可能已移动。");
        }
        var deleted = 0;
        using (var db = SqlCipherNative.OpenReadWrite(shardPath, dbKeyHex))
        {
            db.Execute("BEGIN");
            try
            {
                foreach (var localId in entry.InsertedLocalIds)
                {
                    db.Execute($"DELETE FROM \"{Esc(entry.TableName)}\" WHERE local_id = {localId} AND local_type = {(57L << 32) | 49}");
                    deleted += 1;
                }
                db.Execute("COMMIT");
            }
            catch
            {
                try { db.Execute("ROLLBACK"); } catch { /* 尽力回滚 */ }
                throw;
            }
        }
        entry.RevokedAt = DateTimeOffset.Now;
        UpdateEntry(entry);
        return deleted;
    }

    /// <summary>恢复原库：把目标分片库整体替换为写入前的备份（会丢弃备份之后的全部新消息）。</summary>
    public void RestoreOriginal(WriteBackEntry entry)
    {
        if (!IsWeChatClosed())
        {
            throw new WriteBackException("微信正在运行，请先完全退出微信再恢复。");
        }
        var shardPath = FindShardPath(entry);
        if (shardPath is null)
        {
            throw new WriteBackException($"找不到目标分片库 {entry.DbFileName}。");
        }
        var backupPath = Path.Combine(_backupDir, entry.BackupFileName);
        if (!File.Exists(backupPath))
        {
            throw new WriteBackException($"备份文件缺失：{entry.BackupFileName}");
        }
        var sha = Sha256Hex(File.ReadAllBytes(backupPath));
        if (!string.Equals(sha, entry.BackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new WriteBackException("备份文件校验失败（SHA-256 不匹配），拒绝还原以免写入损坏数据。");
        }
        File.Copy(backupPath, shardPath, overwrite: true);
        // 整库还原已包含按行撤销的效果
        entry.RevokedAt = DateTimeOffset.Now;
        UpdateEntry(entry);
    }

    private string? FindShardPath(WriteBackEntry entry)
    {
        // 通过 KeyVault 里的 dataPath 定位；再回退 xwechat_files 扫描
        var locator = new Keys.WeChatDataLocator();
        foreach (var (dir, wxId) in locator.ListAccounts())
        {
            if (wxId != entry.WxId)
            {
                continue;
            }
            var candidate = Path.Combine(Keys.WeChatDataLocator.MessageDbDir(dir), entry.DbFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 构造 appmsg type=57 引用消息 XML：正文为判读摘要，refermsg 指向「对方发送的被分析消息」
    /// （chatusr/displayname 为对方，微信里呈现为「我引用了对方的话 + 判读结论」）。
    /// </summary>
    internal static string BuildQuoteXml(JudgeVerdict verdict, string quoteText, ChatMessage focus, string sessionId)
    {
        var summary = Esc(quoteText.Length > 800 ? quoteText[..800] + "…" : quoteText);
        var referContent = Esc(focus.DisplayText.Length > 500 ? focus.DisplayText[..500] + "…" : focus.DisplayText);
        var svrid = focus.ServerId ?? 0;
        var referTime = focus.CreateTime.ToUnixTimeSeconds();
        return $"""
            <?xml version="1.0"?>
            <msg>
                <appmsg appid="" sdkver="0">
                    <title>{summary}</title>
                    <des />
                    <type>57</type>
                    <url />
                    <appattach><totlen>0</totlen><emoticonmd5></emoticonmd5><fileext></fileext></appattach>
                    <refermsg>
                        <type>1</type>
                        <svrid>{svrid}</svrid>
                        <fromusr>{Esc(sessionId)}</fromusr>
                        <chatusr>{Esc(focus.SenderWxId)}</chatusr>
                        <displayname>{Esc(focus.SenderDisplayName)}</displayname>
                        <content>{referContent}</content>
                        <createtime>{referTime}</createtime>
                    </refermsg>
                </appmsg>
            </msg>
            """;
    }

    private static long GenerateServerId()
    {
        // 确保为正数且基本唯一：时间戳高位 + 随机低位
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var v = BitConverter.ToInt64(bytes);
        return Math.Abs(v % 0x0000_FFFF_FFFF_FFFF);
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
