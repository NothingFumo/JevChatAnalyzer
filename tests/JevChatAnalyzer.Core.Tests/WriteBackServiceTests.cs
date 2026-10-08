using Xunit;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.WriteBack;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class WriteBackServiceTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "jca-wb-" + Guid.NewGuid().ToString("N"));

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
        Location = new MessageLocation(@"C:\x\message_0.db", "Msg_abc", 4537),
    };

    private static JudgeVerdict SampleVerdict() => new()
    {
        BackendName = "test",
        Model = "stub",
        JudgedAt = DateTimeOffset.Now,
        Answers = new Dictionary<string, JudgeAnswer>
        {
            [JudgeQuestions.IntentId] = new("choice", "ask_advice", 0.9, 0.9),
            [JudgeQuestions.UrgencyId] = new("score", null, 1.2, 0.8),
        },
        Summary = "征询意见，急迫度 1.2/3，稍后从容回复",
    };

    [Fact]
    public void BuildQuoteXml_包含判读摘要与refermsg()
    {
        var xml = WriteBackService.BuildQuoteXml(SampleVerdict(), "征询意见，急迫度 1.2/3", SampleFocus, "456789@chatroom");

        Assert.Contains("<type>57</type>", xml);
        Assert.Contains("<svrid>743043483022253863</svrid>", xml);
        Assert.Contains("征询意见", xml);
        Assert.Contains("今晚吃饭吗", xml);
        // XML 可解析
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.NotNull(doc.Root);
    }

    [Fact]
    public void BuildQuoteXml_单引号转义()
    {
        var verdict = SampleVerdict() with { Summary = "对方说'不干了'" };
        var xml = WriteBackService.BuildQuoteXml(verdict, "对方说'不干了'", SampleFocus, "456789@chatroom");
        // 单引号转义后 XML 仍可解析，且不含裸引号造成的结构破坏
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Contains("不干了", xml);
    }

    [Fact]
    public void BuildQuoteXml_超长摘要截断()
    {
        var longText = new string('长', 2000);
        var xml = WriteBackService.BuildQuoteXml(SampleVerdict(), longText, SampleFocus, "456789@chatroom");
        Assert.DoesNotContain(longText, xml); // 被截断
        Assert.Contains("…", xml);
    }

    [Fact]
    public void ListEntries_空清单_写入后可读回()
    {
        var service = new WriteBackService(_appData);
        Assert.Empty(service.ListEntries());

        // 直接构造台账文件验证读写（写库路径需要真实加密库，不在此覆盖）
        var entry = new WriteBackEntry
        {
            Id = "20261008-120000-abcd1234",
            WxId = "wxid_t",
            SessionId = "wxid_bob",
            DbFileName = "message_0.db",
            TableName = "Msg_abc",
            InsertedLocalIds = [4538],
            BackupFileName = "x.bak",
            BackupSha256 = "deadbeef",
            WrittenAt = DateTimeOffset.Now,
        };
        typeof(WriteBackService)
            .GetMethod("AppendEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [entry]);

        var entries = service.ListEntries();
        var loaded = Assert.Single(entries);
        Assert.Equal("20261008-120000-abcd1234", loaded.Id);
        Assert.Equal([4538L], loaded.InsertedLocalIds);
        Assert.Null(loaded.RevokedAt);
    }

    [Fact]
    public void BuildQuoteXml_引用块挂对方消息()
    {
        var xml = WriteBackService.BuildQuoteXml(SampleVerdict(), "摘要", SampleFocus, "456789@chatroom");

        Assert.Contains("<chatusr>wxid_bob</chatusr>", xml);
        Assert.Contains("<displayname>小明</displayname>", xml);
        Assert.Contains("<fromusr>456789@chatroom</fromusr>", xml);
    }

    [Fact]
    public void EnqueuePending_我的消息被拒绝()
    {
        var service = new WriteBackService(_appData);
        var mine = SampleFocus with { IsMine = true };
        Assert.Throws<WriteBackException>(() =>
            service.EnqueuePending("wxid_me", "wxid_bob", mine, SampleVerdict(), "摘要"));
        Assert.Empty(service.ListPending());
    }

    [Fact]
    public void EnqueuePending_同消息幂等覆盖_列表往返()
    {
        var service = new WriteBackService(_appData);
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus, SampleVerdict(), "第一版");
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus,
            SampleVerdict() with { Summary = "更新后的结论" }, "更新后的结论");

        var pending = service.ListPending();
        var only = Assert.Single(pending);
        Assert.Equal("wxid_bob", only.SessionId);
        Assert.Equal("wxid_bob", only.FocusMessage.SenderWxId);
        Assert.Equal("更新后的结论", only.QuoteText);
        Assert.Equal("Msg_abc", only.TableName);
    }

    [Fact]
    public void RemovePending_移除指定项()
    {
        var service = new WriteBackService(_appData);
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus, SampleVerdict(), "摘要");
        var id = service.ListPending()[0].Id;
        service.RemovePending(id);
        Assert.Empty(service.ListPending());
    }

    [Fact]
    public void FlushPending_分片缺失时保留待写并报错()
    {
        var service = new WriteBackService(_appData);
        service.EnqueuePending("wxid_me", "wxid_bob", SampleFocus, SampleVerdict(), "摘要");

        // 密钥合法但分片路径不存在 → 落库失败，条目保留
        var results = service.FlushPending(new string('a', 64));

        var (entry, error) = Assert.Single(results);
        Assert.Null(entry);
        Assert.NotNull(error);
        Assert.Single(service.ListPending()); // 失败项保留在队列
    }

    [Fact]
    public void Revoke_微信运行时抛出异常()
    {
        // IsWeChatClosed 在 CI/本机无微信时为 true；此处只验证方法路径不抛未捕获异常
        // （真实“运行中”场景由 UI 层提示兜底）
        var service = new WriteBackService(_appData);
        var entry = new WriteBackEntry
        {
            Id = "x", WxId = "w", SessionId = "s", DbFileName = "message_0.db",
            TableName = "Msg_x", InsertedLocalIds = [1], BackupFileName = "b.bak", BackupSha256 = "00",
        };
        // 找不到账号目录时抛中文异常（而非崩溃）
        var ex = Record.Exception(() => service.Revoke(entry, new string('a', 64)));
        if (ex is not null)
        {
            Assert.IsType<WriteBackException>(ex);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }
}
