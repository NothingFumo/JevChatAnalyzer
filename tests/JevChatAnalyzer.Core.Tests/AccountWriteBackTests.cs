using System.Security.Cryptography;
using System.Xml.Linq;
using JevChatAnalyzer.Core.Db;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.WriteBack;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

/// <summary>写回：引用块 XML、台账状态机、真实加密库的改写与还原。</summary>
public class AccountWriteBackTests : IDisposable
{
    // ── 目标消息本身是「对方引用我的消息回复」：新增气泡，不改写原行 ──

    private const string QuoteReplyContent =
        "<msg><appmsg appid=\"\" sdkver=\"0\"><title>我同意</title><type>57</type>" +
        "<refermsg><content>周末去吗</content><displayname>我</displayname><fromusr>wxid_me</fromusr>" +
        "<svrid>111</svrid><type>1</type><chatusr>wxid_bob</chatusr></refermsg></appmsg></msg>";

    private static readonly ChatMessage SampleFocus = new()
    {
        LocalId = 4537,
        ServerId = 743043483022253863,
        SenderWxId = "wxid_bob",
        SenderDisplayName = "小明",
        LocalType = 1,
        SortSeq = 1791457011000,
        CreateTime = DateTimeOffset.FromUnixTimeSeconds(1791457011),
        Content = "今晚吃饭吗",
        DisplayText = "今晚吃饭吗",
        Location = new MessageLocation(@"C:\x\message_0.db", "Msg_abc", 4537)
    };

    private readonly string _appData = Path.Combine(Path.GetTempPath(), "jca-wb-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _dbDirs = [];
    private readonly LedgerStore _ledger;

    /// <summary>最近一次创建的分片密钥：写回按账号绑定密钥，测试跟着夹具走。</summary>
    private string _shardKey = new('a', 64);

    public AccountWriteBackTests()
    {
        _ledger = new LedgerStore(_appData);
    }

    public void Dispose()
    {
        _ledger.Dispose();
        foreach (var dir in _dbDirs)
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* 临时目录尽力清理 */
            }

        try
        {
            Directory.Delete(_appData, true);
        }
        catch
        {
            /* 同上 */
        }
    }

    private AccountWriteBack Service()
    {
        return new AccountWriteBack(_ledger, _shardKey);
    }

    /// <summary>测试用：按写回的真实形态生成引用块（refermsg 指向判读伪会话）。</summary>
    private static string BuildQuote(string quoteText, string originalText, ChatMessage focus,
        string myWxId, string referFrom, long svrid)
    {
        return QuoteMessage.Build(new QuoteMessage.Parts
        {
            QuoteText = quoteText,
            Title = originalText,
            MyWxId = myWxId,
            ReferFromUsr = referFrom,
            SenderWxId = focus.SenderWxId,
            ReferServerId = svrid
        });
    }

    // ── 引用块 XML：标题仍是对方原正文，引用块是判读结论 ──────────────

    [Fact]
    public void 引用块_标题保留原正文_引用块是判读()
    {
        var xml = BuildQuote("征询意见，急迫度 1.2/3", "今晚吃饭吗",
            SampleFocus, "wxid_me", AccountWriteBack.JudgmentSessionId, 777);

        Assert.Contains("<title>今晚吃饭吗</title>", xml);
        Assert.Contains("<type>57</type>", xml);
        Assert.Contains("<refermsg>", xml);
        Assert.Contains("<content>征询意见，急迫度 1.2/3</content>", xml);
        Assert.NotNull(XDocument.Parse(xml).Root);
    }

    [Fact]
    public void 引用块_引用块作者是我_不是对方()
    {
        var xml = BuildQuote("摘要", "今晚吃饭吗", SampleFocus, "wxid_me", AccountWriteBack.JudgmentSessionId, 777);

        Assert.Contains("<chatusr>wxid_me</chatusr>", xml);
        Assert.Contains("<displayname>我</displayname>", xml);
        Assert.Contains($"<fromusr>{AccountWriteBack.JudgmentSessionId}</fromusr>", xml);
        Assert.Contains("<svrid>777</svrid>", xml);
        Assert.Contains("<fromusername>wxid_bob</fromusername>", xml);
    }

    [Fact]
    public void 引用块_引号与尖括号转义后可解析()
    {
        var xml = BuildQuote("对方说'不干了' <急>", "原话 <b>粗体</b> 'q'",
            SampleFocus, "wxid_me", AccountWriteBack.JudgmentSessionId, 777);

        var doc = XDocument.Parse(xml);
        Assert.Contains("不干了", xml);
        Assert.NotNull(doc.Root);
    }

    [Fact]
    public void 引用块_超长内容截断()
    {
        var xml = BuildQuote(new string('摘', 1000), new string('正', 1000),
            SampleFocus, "wxid_me", AccountWriteBack.JudgmentSessionId, 777);

        Assert.Contains("…", xml);
    }

    // ── 待写队列（台账 state=pending） ───────────────────────────────

    [Fact]
    public void EnqueuePending_我的消息被拒绝()
    {
        var mine = SampleFocus with { IsMine = true };
        Assert.Throws<WriteBackException>(() =>
            Service().EnqueuePending("wxid_me", "wxid_bob", mine, "摘要"));
        Assert.Empty(_ledger.ListByState(WriteState.Pending));
    }

    [Fact]
    public void EnqueuePending_同消息幂等覆盖_列表往返()
    {
        var service = Service();
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus, "第一版");
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus, "更新后的结论");

        var only = Assert.Single(_ledger.ListByState(WriteState.Pending));
        Assert.Equal("wxid_bob", only.SessionId);
        Assert.Equal("更新后的结论", only.QuoteText);
        Assert.Equal("Msg_abc", only.TableName);
        Assert.Equal("今晚吃饭吗", only.FocusDisplayText);
        Assert.Equal(WriteState.Pending, only.State);
    }

    [Fact]
    public void FlushPending_分片缺失时保留待写并报错()
    {
        var service = Service();
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus, "摘要");

        var results = service.FlushPending();

        var (entry, error) = Assert.Single(results);
        Assert.Null(entry);
        Assert.NotNull(error);
        Assert.Single(_ledger.ListByState(WriteState.Pending)); // 失败项保留在队列
    }

    [Fact]
    public void Revoke_缺少原文快照时抛中文异常()
    {
        // 分片存在但台账里没有原文快照：拒绝还原（否则会把消息写坏）
        var dir = Path.Combine(Path.GetTempPath(), "jca-wbdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dbDirs.Add(dir);
        var shard = Path.Combine(dir, "message_0.db");
        File.WriteAllBytes(shard, []);
        var entry = new WriteBackEntry
        {
            Id = "x", WxId = "w", SessionId = "s", DbFileName = "message_0.db",
            TableName = "Msg_x", ShardPath = shard,
            AnnotatedLocalId = 1, State = WriteState.Committed
        };
        var ex = Record.Exception(() => Service().Revoke(entry));
        Assert.IsType<WriteBackException>(ex);
        Assert.Contains("原文快照", ex!.Message);
    }

    // ── 真实加密库：改写 → 读取端解析 → 撤销还原 ────────────────────

    [Fact]
    public void AnnotateMessage_把对方文本消息改写成引用_读取端可解析_撤销还原()
    {
        var (dbPath, key) = CreateShardWithTextMessage();

        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };
        var entry = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "征询意见，急迫度 1.2/3");

        // 台账：原文快照齐全，状态 committed
        Assert.Equal(WriteState.Committed, entry.State);
        Assert.Equal(4537, entry.AnnotatedLocalId);
        Assert.Equal(1, entry.OriginalLocalType);
        Assert.True(entry.OriginalContentIsText);
        Assert.NotNull(entry.OriginalContentHex);
        Assert.Null(entry.RevokedAt);

        // 判读行在伪会话表里（微信不会打开它，因此不渲染气泡），refermsg 指向它
        Assert.NotNull(entry.JudgmentTableName);
        Assert.NotNull(entry.JudgmentServerId);
        Assert.Equal(1, CountJudgmentRows(dbPath, key, entry.JudgmentTableName!, entry.JudgmentServerId!.Value));
        Assert.Equal(1, CountTargetRows(dbPath, key)); // 目标表没有新增行

        // 行本身：文本 → 引用；存储类仍是 TEXT
        var row = ReadRow(dbPath, key);
        Assert.Equal((57L << 32) | 49, row.Int("local_type"));
        Assert.Equal("text", row.Text("typeof_content"));

        // 读取端：展示文本仍是原正文，引用块 = 判读结论（作者是我）
        var decoded = ContentDecoder.Decode(row.Int("local_type"), row.Text("message_content")!, s => s);
        Assert.Equal("今晚吃饭吗", decoded.DisplayText);
        Assert.NotNull(decoded.Quote);
        Assert.Equal("征询意见，急迫度 1.2/3", decoded.Quote!.Content);
        Assert.Equal("wxid_me", decoded.Quote.FromWxId);

        // 撤销：local_type 与正文还原
        var changed = service.Revoke(entry);
        Assert.Equal(1, changed);
        var restored = ReadRow(dbPath, key);
        Assert.Equal(1, restored.Int("local_type"));
        Assert.Equal("今晚吃饭吗", restored.Text("message_content"));
        // 伪表空了就删掉：微信库回到没有额外表的状态
        Assert.False(TableExists(dbPath, key, entry.JudgmentTableName!));
        Assert.Equal(WriteState.Revoked, _ledger.GetWriteBack(entry.Id)!.State);
        Assert.NotNull(_ledger.GetWriteBack(entry.Id)!.RevokedAt);
    }

    [Fact]
    public void AnnotateMessage_标题用展示文本_剥掉发送者前缀()
    {
        var (dbPath, key) = CreateShardWithTextMessage(content: "wxid_e67jtnuxhtpj21:\n赚麻了？");
        var focus = SampleFocus with
        {
            Location = new MessageLocation(dbPath, "Msg_test", 4537),
            DisplayText = "赚麻了？"
        };

        var service = Service();
        var entry = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "摘要");

        var xml = ReadRow(dbPath, key).Text("message_content")!;
        Assert.Contains("<title>赚麻了？</title>", xml);
        Assert.DoesNotContain("wxid_e67jtnuxhtpj21:", xml);

        service.Revoke(entry);
        Assert.Equal("wxid_e67jtnuxhtpj21:\n赚麻了？", ReadRow(dbPath, key).Text("message_content"));
    }

    [Fact]
    public void AnnotateMessage_重复写回_旧条目被取代_撤销还原最初原文()
    {
        var (dbPath, key) = CreateShardWithTextMessage();
        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };

        var first = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "第一版判读");
        var second = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "第二版判读");

        // 每次都记一条台账；同一行只保留一条生效写回
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(WriteState.Committed, second.State);
        Assert.Equal(WriteState.Revoked, _ledger.GetWriteBack(first.Id)!.State);
        Assert.Single(_ledger.ListByState(WriteState.Committed));
        Assert.Equal(1, CountTableRows(dbPath, key, second.JudgmentTableName!)); // 判读行被替换而不是堆积
        Assert.Contains("第二版判读", ReadRow(dbPath, key).Text("message_content"));

        // 撤销 → 还原最初原文（快照来自第一次写回）
        service.Revoke(second);
        Assert.Equal("今晚吃饭吗", ReadRow(dbPath, key).Text("message_content"));
        Assert.False(TableExists(dbPath, key, second.JudgmentTableName!));

        // 撤销后可以再次写回
        var third = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "第三版判读");
        Assert.NotEqual(second.Id, third.Id);
        Assert.Contains("第三版判读", ReadRow(dbPath, key).Text("message_content"));
        Assert.Equal(1, CountTableRows(dbPath, key, third.JudgmentTableName!));
    }

    [Fact]
    public void AnnotateMessage_非文本消息被拒绝_不留台账()
    {
        var (dbPath, key) = CreateShardWithTextMessage(3, "");

        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };
        var ex = Assert.Throws<WriteBackRejected>(() =>
            service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
                focus, "摘要"));

        Assert.Contains("文本", ex.Message);
        Assert.Empty(_ledger.RecentWriteBacks()); // 结构性失败不入账
        Assert.Empty(_ledger.ListByState(WriteState.Pending));
    }

    [Fact]
    public void RestoreAll_把生效中的写回逐条还原()
    {
        var (dbPath, key) = CreateShardWithTextMessage();
        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };
        service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom", focus, "判读");

        var results = service.RestoreAll();

        Assert.Single(results);
        Assert.Null(results[0].Error);
        Assert.Equal(1, ReadRow(dbPath, key).Int("local_type"));
        Assert.Empty(_ledger.ListByState(WriteState.Committed));
    }

    [Fact]
    public void Revoke_目标行已删除_标记待确认且不谎报成功()
    {
        var (dbPath, key) = CreateShardWithTextMessage();
        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };
        var entry = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "判读");

        // 用户在微信里把这条消息删了
        using (var db = SqlCipherNative.OpenReadWrite(dbPath, key))
        {
            db.Execute("DELETE FROM Msg_test WHERE local_id = 4537");
        }

        var ex = Assert.Throws<WriteBackException>(() => service.Revoke(entry));
        Assert.Contains("无法还原", ex.Message);
        Assert.Equal(WriteState.Orphaned, _ledger.GetWriteBack(entry.Id)!.State);
    }

    [Fact]
    public void Revoke_行被微信改写后拒绝覆盖()
    {
        var (dbPath, key) = CreateShardWithTextMessage();
        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };
        var entry = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "判读");

        // 模拟微信把这一行改回普通文本（同步/修复）
        using (var db = SqlCipherNative.OpenReadWrite(dbPath, key))
        {
            db.Execute("UPDATE Msg_test SET local_type = 1, message_content = '对方后来改过' WHERE local_id = 4537");
        }

        Assert.Throws<WriteBackException>(() => service.Revoke(entry));
        Assert.Equal(WriteState.Orphaned, _ledger.GetWriteBack(entry.Id)!.State);
        // 拒绝覆盖：微信写的内容保持原样
        Assert.Equal("对方后来改过", ReadRow(dbPath, key).Text("message_content"));
    }

    [Fact]
    public void RestoreAll_单条失败不中断其余条目()
    {
        var (dbPath, key) = CreateShardWithTextMessage();
        var (dbPath2, _) = CreateShardWithTextMessage(keyHex: key);
        var service = Service();
        var first = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) }, "判读 A");
        // 同一 session + 同一 local_id 会被视作「重新写回」，这里换一个会话以示两条独立台账
        service.AnnotateMessage(dbPath2, "Msg_test", "wxid_me", "456790@chatroom",
            SampleFocus with { Location = new MessageLocation(dbPath2, "Msg_test", 4537) }, "判读 B");

        // 第二条的分片整个消失（账号目录被移走）
        Directory.Delete(Path.GetDirectoryName(dbPath2)!, true);

        var results = service.RestoreAll();

        Assert.Equal(2, results.Count);
        Assert.Null(results.Single(r => r.Entry.Id == first.Id).Error);
        Assert.NotNull(results.Single(r => r.Entry.Id != first.Id).Error);
        Assert.Equal(1, ReadRow(dbPath, key).Int("local_type")); // 成功的那条已还原
    }

    [Fact]
    public void Reconcile_行已是引用块则补成committed()
    {
        var (dbPath, key) = CreateShardWithTextMessage();
        var service = Service();
        var focus = SampleFocus with { Location = new MessageLocation(dbPath, "Msg_test", 4537) };
        var committed = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "判读");

        // 模拟「改库已提交、台账没来得及落 committed」：手工补一条 pending
        var orphan = _ledger.BeginWriteBack(new WriteBackIntent
        {
            WxId = "wxid_me", SessionId = "456789@chatroom", ShardPath = dbPath,
            TableName = "Msg_test", Focus = focus, QuoteText = "判读"
        });
        Assert.Equal(WriteState.Pending, orphan.State);

        var results = service.Reconcile();

        var (entry, outcome) = Assert.Single(results);
        Assert.Equal(orphan.Id, entry.Id);
        Assert.Equal("committed", outcome);
        Assert.Equal(WriteState.Committed, _ledger.GetWriteBack(orphan.Id)!.State);
        // 对账不破坏已有台账
        Assert.Equal(WriteState.Committed, _ledger.GetWriteBack(committed.Id)!.State);
    }

    // ── 夹具 ────────────────────────────────────────────────────────

    private (string DbPath, string Key) CreateShardWithTextMessage(long localType = 1, string content = "今晚吃饭吗",
        string? keyHex = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-wbdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dbDirs.Add(dir);
        var dbPath = Path.Combine(dir, "message_0.db");
        var key = keyHex ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _shardKey = key;
        File.WriteAllBytes(dbPath, []); // 先落地空库文件（OpenReadWrite 不带 CREATE 标志）
        using var db = SqlCipherNative.OpenReadWrite(dbPath, key);
        db.Execute("CREATE TABLE Name2Id (user_name TEXT)");
        db.Execute("INSERT INTO Name2Id (user_name) VALUES ('wxid_me')");
        db.Execute("""
                   CREATE TABLE "Msg_test" (
                       local_id INTEGER, server_id INTEGER, local_type INTEGER, sort_seq INTEGER,
                       real_sender_id INTEGER, create_time INTEGER, status INTEGER, upload_status INTEGER,
                       download_status INTEGER, server_seq INTEGER, origin_source INTEGER, source TEXT,
                       message_content TEXT, compress_content BLOB, packed_info_data BLOB,
                       WCDB_CT_message_content INTEGER)
                   """);
        db.Execute($"""
                    INSERT INTO "Msg_test"
                    (local_id, server_id, local_type, sort_seq, real_sender_id, create_time, status,
                     upload_status, download_status, server_seq, origin_source, source, message_content)
                    VALUES (4537, 743043483022253863, {localType}, 1791457011000, 2, 1791457011, 3,
                            0, 0, 0, 2, '', '{content.Replace("'", "''")}')
                    """);
        return (dbPath, key);
    }

    private static long InsertAnnotationBubble(string dbPath, string key, string table,
        ChatMessage focus, string quoteText)
    {
        using var db = SqlCipherNative.OpenReadWrite(dbPath, key);
        var id = db.Query($"SELECT COALESCE(MAX(local_id),0)+1 AS next FROM \"{table}\"")[0].Int("next");
        var xml = QuoteMessage.Build(new QuoteMessage.Parts
        {
            QuoteText = quoteText,
            Title = focus.DisplayText,
            MyWxId = "wxid_me",
            ReferFromUsr = AccountWriteBack.JudgmentSessionId,
            SenderWxId = focus.SenderWxId,
            ReferServerId = 1
        });
        db.Execute($"""
                    INSERT INTO "{table}"
                    (local_id, server_id, local_type, sort_seq, real_sender_id, create_time, status,
                     upload_status, download_status, server_seq, origin_source, source,
                     message_content, compress_content, packed_info_data)
                    VALUES ({id}, 1, {(57L << 32) | 49}, 1791457011000, 2, 1791457011, 4, 0, 0, 0, 5,
                            '{ChatStore.AnnotationBubbleSource}', '{xml.Replace("'", "''")}', NULL, NULL)
                    """);
        return id;
    }

    private static bool TableExists(string dbPath, string key, string table)
    {
        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        return db.Query($"SELECT COUNT(*) AS n FROM sqlite_master WHERE name = '{table}'")[0].Int("n") > 0;
    }

    private static long CountTableRows(string dbPath, string key, string table)
    {
        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        return db.Query($"SELECT COUNT(*) AS n FROM \"{table}\"")[0].Int("n");
    }

    private static long CountJudgmentRows(string dbPath, string key, string table, long serverId)
    {
        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        return db.Query($"SELECT COUNT(*) AS n FROM \"{table}\" WHERE server_id = {serverId}")[0].Int("n");
    }

    private static long CountTargetRows(string dbPath, string key)
    {
        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        return db.Query("SELECT COUNT(*) AS n FROM Msg_test")[0].Int("n");
    }

    private static SqlCipherNative.Row ReadRow(string dbPath, string key)
    {
        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        return db.Query(
            "SELECT local_type, typeof(message_content) AS typeof_content, message_content FROM Msg_test")[0];
    }

    [Fact]
    public void 目标消息是对方引用回复_新增气泡且不改写原行()
    {
        var (dbPath, key) = CreateShardWithTextMessage(49, QuoteReplyContent);
        var focus = SampleFocus with { DisplayText = "我同意" };

        var entry = Service().AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom",
            focus, "征询意见，急迫度 1.2/3");

        Assert.NotNull(entry.InsertedLocalId);
        Assert.Equal(2, CountTargetRows(dbPath, key));

        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        var original = db.Query("SELECT local_type, message_content FROM Msg_test WHERE local_id = 4537")[0];
        Assert.Equal(49, original.Int("local_type") & 0xFFFFFFFF);
        Assert.Equal(QuoteReplyContent, original.Text("message_content"));

        var bubble = db.Query(
            $"SELECT source, local_type, message_content FROM Msg_test WHERE local_id = {entry.InsertedLocalId}")[0];
        Assert.Equal(ChatStore.AnnotationBubbleSource, bubble.Text("source"));
        Assert.Equal((57L << 32) | 49, bubble.Int("local_type"));
        var bubbleXml = bubble.Text("message_content")!;
        Assert.Contains("<title>我同意</title>", bubbleXml); // 气泡正文仍是对方原话
        Assert.Contains("<refermsg>", bubbleXml);
        Assert.Contains("征询意见，急迫度 1.2/3", bubbleXml);

        // 读取端把气泡解析成「对方原话 + 引用判读」，而不是空正文
        var decoded = ContentDecoder.Decode(bubble.Int("local_type"), bubbleXml, s => s);
        Assert.Equal("我同意", decoded.DisplayText);
        Assert.Equal("征询意见，急迫度 1.2/3", decoded.Quote!.Content);

        Assert.Equal(WriteState.Committed, _ledger.GetWriteBack(entry.Id)!.State);
    }

    [Fact]
    public void 撤销新增气泡_气泡与判读行消失_目标行原样()
    {
        var (dbPath, key) = CreateShardWithTextMessage(49, QuoteReplyContent);
        var service = Service();
        var focus = SampleFocus with { DisplayText = "我同意" };
        var entry = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom", focus, "判读");

        var changed = service.Revoke(entry);

        Assert.Equal(1, changed);
        Assert.Equal(1, CountTargetRows(dbPath, key));
        using (var db = SqlCipherNative.OpenReadOnly(dbPath, key))
        {
            var original = db.Query("SELECT message_content FROM Msg_test WHERE local_id = 4537")[0];
            Assert.Equal(QuoteReplyContent, original.Text("message_content"));
        }

        Assert.False(TableExists(dbPath, key, MessageTable.For(AccountWriteBack.JudgmentSessionId)));
        Assert.Equal(WriteState.Revoked, _ledger.GetWriteBack(entry.Id)!.State);
    }

    [Fact]
    public void 目标消息是对方引用回复_重复写回_不堆积气泡()
    {
        var (dbPath, key) = CreateShardWithTextMessage(49, QuoteReplyContent);
        var service = Service();
        var focus = SampleFocus with { DisplayText = "我同意" };

        var first = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom", focus, "第一版判读");
        var second = service.AnnotateMessage(dbPath, "Msg_test", "wxid_me", "456789@chatroom", focus, "第二版判读");

        // 目标行 + 唯一一条气泡：旧气泡必须被新写回取代，而不是堆积
        Assert.Equal(2, CountTargetRows(dbPath, key));
        Assert.Equal(WriteState.Revoked, _ledger.GetWriteBack(first.Id)!.State);

        using var db = SqlCipherNative.OpenReadOnly(dbPath, key);
        var bubble = Assert.Single(db.Query("SELECT local_id, message_content FROM Msg_test WHERE local_id <> 4537"));
        Assert.Equal(second.InsertedLocalId, bubble.Int("local_id"));
        Assert.Contains("第二版判读", bubble.Text("message_content"));

        // 判读伪表同样只留最新一条
        Assert.Equal(1, CountTableRows(dbPath, key, MessageTable.For(AccountWriteBack.JudgmentSessionId)));
    }

    [Fact]
    public void Reconcile_气泡已插入但台账未提交_补成committed()
    {
        var (dbPath, key) = CreateShardWithTextMessage(49, QuoteReplyContent);
        var service = Service();
        var focus = SampleFocus with
        {
            DisplayText = "我同意",
            Location = new MessageLocation(dbPath, "Msg_test", 4537)
        };
        var pending = service.EnqueuePending("wxid_me", "456789@chatroom", focus, "判读");
        Assert.Equal(WriteState.Pending, pending.State);

        // 模拟崩溃窗口：气泡行已写入，台账没来得及落 committed（行号先记了）
        var bubbleId = InsertAnnotationBubble(dbPath, key, pending.TableName, focus, "判读");
        _ledger.SetInsertedRow(pending.Id, bubbleId);

        var (entry, outcome) = Assert.Single(service.Reconcile());

        Assert.Equal(pending.Id, entry.Id);
        Assert.Equal("committed", outcome);
        Assert.Equal(WriteState.Committed, _ledger.GetWriteBack(pending.Id)!.State);
    }
}
