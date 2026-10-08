using System.Security.Cryptography;
using System.Text;
using JevChatAnalyzer.Core.Db;
using JevChatAnalyzer.Core.Keys;

namespace JevChatAnalyzer.Core.Messages;

/// <summary>
/// 微信 4.x 消息读取器：按账号打开消息分片库，聚合会话列表，
/// 按「表名 md5 反查 + sort_seq 排序 + Name2Id 发送者还原」读取会话消息。
/// 原始库一律只读打开。
/// </summary>
public sealed class ChatStore : IDisposable
{
    /// <summary>4.x 一级类型：文本/图片/语音/视频/动画表情/位置/appmsg 容器/通话/系统。</summary>
    public const long TypeText = 1;
    public const long TypeImage = 3;
    public const long TypeVoice = 34;
    public const long TypeVideo = 43;
    public const long TypeSticker = 47;
    public const long TypeLocation = 48;
    public const long TypeAppMsg = 49;
    public const long TypeVoip = 50;
    public const long TypeSystem = 10000;
    /// <summary>appmsg 子类型：引用回复。</summary>
    public const int AppMsgQuote = 57;

    private readonly List<(string ShardPath, SqlCipherNative Db)> _shards = [];
    private SqlCipherNative? _contactDb;
    private SqlCipherNative? _sessionDb;
    private readonly string _dbKeyHex;

    /// <summary>我自己的 wxid。</summary>
    public string MyWxId { get; }

    /// <summary>会话名 → Msg 表名（md5）；装载分片时构建。</summary>
    private readonly Dictionary<string, string> _sessionTableNames = new(StringComparer.Ordinal);

    /// <summary>各分片内 Name2Id：rowid → user_name。</summary>
    private readonly List<Dictionary<long, string>> _name2IdByShard = [];

    /// <summary>wxid → 展示名（联系人库，备注 > 昵称）。</summary>
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);

    /// <summary>群聊 wxid → 群内昵称（contact.db chatroom_member / 群昵称表）。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _groupNicknames = new(StringComparer.Ordinal);

    private ChatStore(string myWxId, string dbKeyHex)
    {
        MyWxId = myWxId;
        _dbKeyHex = dbKeyHex;
    }

    /// <summary>打开账号的全部基础库。shards 为 message_*.db 路径。</summary>
    public static ChatStore Open(string accountDir, string wxId, string dbKeyHex, Action<string>? log = null)
    {
        var store = new ChatStore(wxId, dbKeyHex);
        try
        {
            foreach (var shard in WeChatDataLocator.ListMessageDbs(accountDir))
            {
                log?.Invoke($"打开 {Path.GetFileName(shard)} …");
                var db = SqlCipherNative.OpenReadOnly(shard, dbKeyHex);
                store._shards.Add((shard, db));
                store.LoadShardIndex(shard, db);
            }
            if (store._shards.Count == 0)
            {
                throw new InvalidOperationException($"在 {accountDir} 下未找到消息分片库。");
            }

            var contactPath = WeChatDataLocator.ContactDbPath(accountDir);
            if (File.Exists(contactPath))
            {
                store._contactDb = SqlCipherNative.OpenReadOnly(contactPath, dbKeyHex);
                store.LoadContacts();
            }
            var sessionPath = WeChatDataLocator.SessionDbPath(accountDir);
            if (File.Exists(sessionPath))
            {
                store._sessionDb = SqlCipherNative.OpenReadOnly(sessionPath, dbKeyHex);
            }
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>索引分片：枚举 Msg_&lt;md5&gt; 表与 Name2Id 映射。</summary>
    private void LoadShardIndex(string shardPath, SqlCipherNative db)
    {
        var name2Id = new Dictionary<long, string>();
        try
        {
            foreach (var row in db.Query("SELECT rowid, user_name FROM Name2Id"))
            {
                name2Id[row.Int("rowid")] = row.Text("user_name") ?? "";
            }
        }
        catch (SqliteNativeException)
        {
            // 个别分片可能没有 Name2Id，留空
        }
        _name2IdByShard.Add(name2Id);

        foreach (var row in db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg\\_%' ESCAPE '\\'"))
        {
            var table = row.Text("name")!;
            var md5 = table["Msg_".Length..];
            // 表名 md5 反查会话名：对照已知会话名做匹配（ListSessions 时补全）
            _sessionTableNames.TryAdd(table, table);
            if (!_tableToMd5.TryGetValue(table, out _))
            {
                _tableToMd5[table] = md5;
            }
            if (!_tableShardCount.TryGetValue(table, out var n))
            {
                _tableShardCount[table] = 0;
            }
            _tableShardCount[table]++;
        }
    }

    private readonly Dictionary<string, string> _tableToMd5 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tableShardCount = new(StringComparer.Ordinal);

    private void LoadContacts()
    {
        if (_contactDb is null)
        {
            return;
        }
        try
        {
            foreach (var row in _contactDb.Query("SELECT username, nick_name, remark, alias FROM contact"))
            {
                var username = row.Text("username") ?? "";
                if (username.Length == 0)
                {
                    continue;
                }
                var remark = row.Text("remark");
                var nick = row.Text("nick_name");
                var alias = row.Text("alias");
                _displayNames[username] = FirstNonEmpty(remark, nick, alias, username)!;
            }
            // 群昵称：chatroom_member(member_id → name2id.rowid, room_id → 群 rowid)
            foreach (var row in _contactDb.Query("""
                    SELECT r.user_name AS room, m2.user_name AS member, cm.display_name AS nick
                    FROM chatroom_member cm
                    JOIN name2id r ON r.rowid = cm.room_id
                    JOIN name2id m2 ON m2.rowid = cm.member_id
                    """))
            {
                var room = row.Text("room");
                var member = row.Text("member");
                var nick = row.Text("nick");
                if (room is { Length: > 0 } && member is { Length: > 0 } && !string.IsNullOrWhiteSpace(nick))
                {
                    if (!_groupNicknames.TryGetValue(room!, out var members))
                    {
                        members = _groupNicknames[room!] = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                    members[member!] = nick!;
                }
            }
        }
        catch (SqliteNativeException)
        {
            // 联系人库结构差异时静默降级：展示名回退 wxid
        }
    }

    /// <summary>会话列表：Msg 表消息数 + session.db 摘要合并。</summary>
    public IReadOnlyList<ChatSession> ListSessions(int top = 200)
    {
        var sessions = new Dictionary<string, (long Count, long LastSeq)>(StringComparer.Ordinal);
        foreach (var (shard, db) in _shards)
        {
            var tables = string.Join(", ", _tableToMd5.Keys.Select(t => $"'{t}'"));
            // 每张分表取 count 与 max(sort_seq)
            foreach (var table in _tableToMd5.Keys)
            {
                try
                {
                    var rows = db.Query($"SELECT count(*) AS c, COALESCE(max(sort_seq),0) AS m FROM \"{table}\"");
                    var c = rows[0].Int("c");
                    if (c > 0)
                    {
                        var cur = sessions.GetValueOrDefault(table, (0, 0));
                        sessions[table] = (cur.Count + c, Math.Max(cur.LastSeq, rows[0].Int("m")));
                    }
                }
                catch (SqliteNativeException)
                {
                    // 空表/异常表跳过
                }
            }
        }

        // md5 → 会话 id：优先用 session.db 的 username 与 md5 匹配
        var md5ToSession = BuildSessionMd5Lookup();
        var result = new List<ChatSession>();
        foreach (var (table, (count, lastSeq)) in sessions)
        {
            var md5 = _tableToMd5[table];
            var sessionId = md5ToSession.GetValueOrDefault(md5) ?? $"md5:{md5}";
            var kind = SessionKindExtensions.Classify(sessionId);
            var lastTime = lastSeq > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastSeq)
                : default;
            result.Add(new ChatSession
            {
                SessionId = sessionId,
                DisplayName = ResolveDisplayName(sessionId),
                Kind = kind,
                Summary = "",
                MessageCount = count,
                LastTime = lastTime,
            });
        }
        return result.OrderByDescending(s => s.LastTime).ThenByDescending(s => s.MessageCount).Take(top).ToList();
    }

    /// <summary>用 session.db / contact.db 的已知 username 计算 md5 反查表名。</summary>
    private Dictionary<string, string> BuildSessionMd5Lookup()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (_sessionDb is not null)
        {
            try
            {
                foreach (var row in _sessionDb.Query("SELECT username FROM SessionTable"))
                {
                    var u = row.Text("username");
                    if (!string.IsNullOrEmpty(u))
                    {
                        candidates.Add(u!);
                    }
                }
            }
            catch (SqliteNativeException) { }
        }
        foreach (var u in _displayNames.Keys)
        {
            candidates.Add(u);
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in candidates)
        {
            map[ComputeMd5Hex(u)] = u;
        }
        return map;
    }

    internal static string ComputeMd5Hex(string s) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>读取一个会话的全部消息（跨分片聚合、按 sort_seq 升序）。limit 为倒序截取条数。</summary>
    public IReadOnlyList<ChatMessage> ReadSessionMessages(string sessionId, int limit = 500, long? beforeSeq = null)
    {
        var table = $"Msg_{ComputeMd5Hex(sessionId)}";
        if (!_tableToMd5.ContainsKey(table))
        {
            return [];
        }

        var all = new List<(ChatMessage Msg, string Shard)>();
        var where = beforeSeq is null ? "" : $" WHERE sort_seq < {beforeSeq}";
        foreach (var (shardPath, db) in _shards)
        {
            List<SqlCipherNative.Row> rows;
            try
            {
                rows = db.Query($"""
                        SELECT local_id, server_id, local_type, sort_seq, real_sender_id,
                               create_time, message_content, compress_content
                        FROM "{table}"{where} ORDER BY sort_seq DESC LIMIT {limit}
                        """);
            }
            catch (SqliteNativeException)
            {
                continue; // 该分片没有此表
            }
            var name2Id = _name2IdByShard[_shards.FindIndex(s => s.ShardPath == shardPath)];
            foreach (var row in rows)
            {
                var localType = row.Int("local_type");
                var senderId = row.Int("real_sender_id");
                var senderWxId = NormalizeSender(name2Id.GetValueOrDefault(senderId), senderId);
                var content = row.Text("message_content") ?? "";
                var compressed = row.Blob("compress_content");
                if (content.Length == 0 && compressed is { Length: > 0 })
                {
                    content = ContentDecoder.TryDecompress(compressed) ?? "";
                }
                // 4.x 文本正文常带 "wxid_xxx:\n" 发送者前缀（单聊也有），剥除并用作发送者回退
                var (strippedContent, prefixSender) = StripSenderPrefix(content);
                if (prefixSender is not null && IsRowidFallback(senderWxId))
                {
                    senderWxId = prefixSender;
                }
                content = strippedContent;
                var sortSeq = row.Int("sort_seq");
                var createTime = row.IntOrNull("create_time") is { } t && t > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(t)
                    : DateTimeOffset.FromUnixTimeMilliseconds(sortSeq);
                var isMine = senderWxId == MyWxId;
                var decoded = ContentDecoder.Decode(localType, content, ResolveDisplayName);
                // 引用块的发送者展示名按联系人库补齐
                var quote = decoded.Quote;
                if (quote is not null && string.IsNullOrWhiteSpace(quote.FromDisplayName) && quote.FromWxId is not null)
                {
                    quote = quote with { FromDisplayName = ResolveDisplayName(quote.FromWxId) };
                }
                all.Add((new ChatMessage
                {
                    LocalId = row.Int("local_id"),
                    ServerId = row.IntOrNull("server_id"),
                    SenderWxId = senderWxId,
                    SenderDisplayName = ResolveSenderName(sessionId, senderWxId, isMine),
                    IsMine = isMine,
                    LocalType = localType,
                    AppMsgSubType = decoded.AppMsgSubType,
                    SortSeq = sortSeq,
                    CreateTime = createTime,
                    Content = content,
                    DisplayText = decoded.DisplayText,
                    Quote = quote,
                    Location = new MessageLocation(shardPath, table, row.Int("local_id")),
                }, shardPath));
            }
        }
        return all
            .OrderByDescending(m => m.Msg.SortSeq)
            .Take(limit)
            .OrderBy(m => m.Msg.SortSeq)
            .Select(m => m.Msg)
            .ToList();
    }

    private string ResolveSenderName(string sessionId, string senderWxId, bool isMine)
    {
        if (isMine)
        {
            return "我";
        }
        if (sessionId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
            _groupNicknames.TryGetValue(sessionId, out var members) &&
            members.TryGetValue(senderWxId, out var nick) && !string.IsNullOrWhiteSpace(nick))
        {
            return nick;
        }
        return ResolveDisplayName(senderWxId);
    }

    /// <summary>Name2Id 空值/缺失时回退 rowid 标记。</summary>
    private static string NormalizeSender(string? userName, long rowid) =>
        string.IsNullOrWhiteSpace(userName) ? $"rowid:{rowid}" : userName!;

    private static bool IsRowidFallback(string senderWxId) =>
        senderWxId.StartsWith("rowid:", StringComparison.Ordinal);

    /// <summary>
    /// 剥除 4.x 正文前缀 "wxid-ish:\n"（wxid / 自定义微信号 / @chatroom 成员 id）。
    /// 返回剥除后的正文与识别出的发送者（不匹配时返回原文与 null）。
    /// </summary>
    internal static (string Content, string? PrefixSender) StripSenderPrefix(string content)
    {
        if (content.Length == 0)
        {
            return (content, null);
        }
        var nl = content.IndexOf(":\n", StringComparison.Ordinal);
        if (nl <= 0 || nl > 64 || content.AsSpan(0, nl).Contains('\n'))
        {
            return (content, null);
        }
        var candidate = content[..nl];
        // wxid/gh_/xxx@chatroom 或自定义微信号（字母数字下划线短横线）
        var looksLikeId = (candidate.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) ||
                           candidate.StartsWith("gh_", StringComparison.OrdinalIgnoreCase) ||
                           candidate.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)) ||
                          candidate.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
        return looksLikeId && candidate.Length >= 4 ? (content[(nl + 2)..], candidate) : (content, null);
    }

    private string ResolveDisplayName(string id) =>
        _displayNames.GetValueOrDefault(id) ?? id;

    public void Dispose()
    {
        foreach (var (_, db) in _shards)
        {
            db.Dispose();
        }
        _shards.Clear();
        _contactDb?.Dispose();
        _sessionDb?.Dispose();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
