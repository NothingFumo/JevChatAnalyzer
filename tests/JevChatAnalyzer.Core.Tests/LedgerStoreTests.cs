using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

/// <summary>判读台账：判读记录 + 写回状态机的唯一本地存储（SQLCipher）。</summary>
public class LedgerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jca-ledger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            /* 临时目录尽力清理 */
        }
    }

    private static JudgeVerdict Verdict(string summary)
    {
        return new JudgeVerdict
        {
            BackendName = "test",
            Model = "stub",
            JudgedAt = DateTimeOffset.Now,
            Answers = new Dictionary<string, JudgeAnswer>
            {
                [JudgeProtocol.IntentId] = new("choice", "ask_advice", 0.9, 0.9)
            },
            Summary = summary
        };
    }

    private static JudgmentRecord Judgment(string id, string summary = "摘要")
    {
        return new JudgmentRecord
        {
            Id = id,
            WxId = "wxid_me",
            SessionId = "wxid_bob",
            SessionTitle = "小明",
            FocusText = "今晚吃饭吗",
            MessageKey = "key-1",
            Verdict = Verdict(summary),
            CreatedAt = DateTimeOffset.Now
        };
    }

    private static ChatMessage Focus(long localId = 4537)
    {
        return new ChatMessage
        {
            LocalId = localId,
            ServerId = 743043483022253863,
            SenderWxId = "wxid_bob",
            LocalType = 1,
            SortSeq = 1791457011000,
            CreateTime = DateTimeOffset.FromUnixTimeSeconds(1791457011),
            DisplayText = "今晚吃饭吗",
            Location = new MessageLocation(@"C:\x\message_0.db", "Msg_abc", localId)
        };
    }

    private static WriteBackIntent Intent(string? judgmentId = null, long localId = 4537)
    {
        return new WriteBackIntent
        {
            JudgmentId = judgmentId,
            WxId = "wxid_me",
            SessionId = "wxid_bob",
            ShardPath = @"C:\x\message_0.db",
            TableName = "Msg_abc",
            Focus = Focus(localId),
            QuoteText = "征询意见"
        };
    }

    [Fact]
    public void Judgment_写入后在历史视图里可见_且带有写回关联()
    {
        using var ledger = new LedgerStore(_dir);
        ledger.AddJudgment(Judgment("j1"));

        var view = Assert.Single(ledger.Recent(10));
        Assert.Equal("j1", view.Record.Id);
        Assert.Equal("摘要", view.Record.Verdict.Summary);
        Assert.Null(view.WriteBack);

        var entry = ledger.BeginWriteBack(Intent("j1"));
        ledger.CompleteWriteBack(entry.Id, 1, 777, "Msg_pseudo");

        var joined = Assert.Single(ledger.Recent(10));
        Assert.Equal(WriteState.Committed, joined.WriteBack!.State);
        Assert.Equal(777, joined.WriteBack.JudgmentServerId);
        Assert.Equal("j1", joined.WriteBack.JudgmentId);
    }

    [Fact]
    public void Judgment_同id写入覆盖_不产生重复()
    {
        using var ledger = new LedgerStore(_dir);
        ledger.AddJudgment(Judgment("j1", "第一版"));
        ledger.AddJudgment(Judgment("j1", "第二版"));

        var view = Assert.Single(ledger.Recent(10));
        Assert.Equal("第二版", view.Record.Verdict.Summary);
    }

    [Fact]
    public void WriteBack_确定性拒绝标记后移出待写队列()
    {
        using var ledger = new LedgerStore(_dir);
        var entry = ledger.BeginWriteBack(Intent());
        Assert.Equal(1, ledger.PendingCount());

        ledger.MarkRejected(entry.Id, "只能给对方的文本消息或引用回复加引用块。");

        Assert.Equal(0, ledger.PendingCount());
        var rejected = Assert.Single(ledger.ListByState(WriteState.Rejected));
        Assert.Equal("只能给对方的文本消息或引用回复加引用块。", rejected.LastError);
    }

    [Fact]
    public void WriteBack_从pending到committed_同一行只保留一条生效()
    {
        using var ledger = new LedgerStore(_dir);
        var first = ledger.BeginWriteBack(Intent());
        Assert.Equal(WriteState.Pending, first.State);
        Assert.Equal(1, ledger.PendingCount());
        Assert.Equal("Msg_abc", first.TableName);
        Assert.Equal("今晚吃饭吗", first.FocusDisplayText);

        ledger.CompleteWriteBack(first.Id, 1, 111, "Msg_pseudo");
        Assert.Equal(0, ledger.PendingCount());

        var second = ledger.BeginWriteBack(Intent());
        ledger.CompleteWriteBack(second.Id, 2, 222, "Msg_pseudo");
        ledger.SupersedeOtherCommitted(second.SessionId, 4537, second.Id);

        var committed = Assert.Single(ledger.ListByState(WriteState.Committed));
        Assert.Equal(second.Id, committed.Id);
        Assert.Equal(WriteState.Revoked, ledger.GetWriteBack(first.Id)!.State);
    }

    [Fact]
    public void WriteBack_撤销记录撤销时间()
    {
        using var ledger = new LedgerStore(_dir);
        var entry = ledger.BeginWriteBack(Intent());
        ledger.CompleteWriteBack(entry.Id, 1, 111, "Msg_pseudo");

        ledger.MarkRevoked(entry.Id);

        var revoked = ledger.GetWriteBack(entry.Id)!;
        Assert.Equal(WriteState.Revoked, revoked.State);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Empty(ledger.ListByState(WriteState.Committed));
    }

    [Fact]
    public void 待写条目按消息幂等_并可按状态清理()
    {
        using var ledger = new LedgerStore(_dir);
        ledger.BeginWriteBack(Intent());
        ledger.DeletePendingFor("wxid_bob", 4537);

        Assert.Equal(0, ledger.PendingCount());
        Assert.Empty(ledger.ListByState(WriteState.Pending));
    }

    [Fact]
    public void DeleteJudgment_写回生效中时被拒绝()
    {
        using var ledger = new LedgerStore(_dir);
        ledger.AddJudgment(Judgment("j1"));
        var entry = ledger.BeginWriteBack(Intent("j1"));
        ledger.CompleteWriteBack(entry.Id, 1, 111, "Msg_pseudo");

        var ex = Assert.Throws<InvalidOperationException>(() => ledger.DeleteJudgment("j1"));
        Assert.Contains("撤销", ex.Message);
        Assert.Single(ledger.Recent(10));

        ledger.MarkRevoked(entry.Id);
        ledger.DeleteJudgment("j1");
        Assert.Empty(ledger.Recent(10));
    }

    [Fact]
    public void 库损坏时保留现场并重建_不静默丢弃()
    {
        using (var ledger = new LedgerStore(_dir))
        {
            ledger.AddJudgment(Judgment("j1"));
        }

        // 破坏文件头：密钥校验会失败
        var dbPath = Path.Combine(_dir, "ledger.db");
        File.WriteAllBytes(dbPath, new byte[512]);

        using (var reopened = new LedgerStore(_dir))
        {
            Assert.Empty(reopened.Recent(10));
            reopened.AddJudgment(Judgment("j2"));
            Assert.Single(reopened.Recent(10));
        }

        Assert.NotEmpty(Directory.GetFiles(_dir, "ledger.corrupt-*.db"));
    }
}
