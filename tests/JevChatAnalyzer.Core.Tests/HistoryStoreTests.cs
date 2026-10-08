using Xunit;
using JevChatAnalyzer.Core.History;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "jca-hist-" + Guid.NewGuid().ToString("N"));

    private static HistoryRecord MakeRecord(string id) => new()
    {
        Id = id,
        WxId = "wxid_t",
        SessionId = "wxid_bob",
        SessionTitle = "小明",
        FocusText = "今晚吃饭吗",
        MessageKey = "s778899",
        Verdict = new JudgeVerdict
        {
            BackendName = "test",
            Model = "stub",
            JudgedAt = DateTimeOffset.Now,
            Answers = new Dictionary<string, JudgeAnswer>
            {
                [JudgeQuestions.IntentId] = new("choice", "invite_meet", 0.88, 0.88),
            },
            Summary = "约见面，稍后回复",
        },
        CreatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void AddAndRecent_往返保真_跨实例读取()
    {
        var store = new HistoryStore(_appData);
        store.Add(MakeRecord("r1"));
        store.Add(MakeRecord("r2"));
        store.Dispose();

        // 新实例用同一 key 文件解密
        var store2 = new HistoryStore(_appData);
        var recent = store2.Recent();

        Assert.Equal(2, recent.Count);
        Assert.Equal("r2", recent[0].Id); // 新到旧
        Assert.Equal("invite_meet", recent[1].Verdict.Answers[JudgeQuestions.IntentId].Label);
        Assert.Equal("约见面，稍后回复", recent[1].Verdict.Summary);
    }

    [Fact]
    public void Add_同Id覆盖()
    {
        var store = new HistoryStore(_appData);
        store.Add(MakeRecord("r1"));
        store.Add(MakeRecord("r1") with { FocusText = "改过的消息" });

        var recent = store.Recent();
        var only = Assert.Single(recent);
        Assert.Equal("改过的消息", only.FocusText);
    }

    [Fact]
    public void Delete_移除记录()
    {
        var store = new HistoryStore(_appData);
        store.Add(MakeRecord("r1"));
        store.Delete("r1");
        Assert.Empty(store.Recent());
    }

    [Fact]
    public void Recent_按WxId过滤()
    {
        var store = new HistoryStore(_appData);
        store.Add(MakeRecord("r1"));
        store.Add(MakeRecord("r2") with { WxId = "wxid_other" });

        Assert.Single(store.Recent(wxId: "wxid_t"));
        Assert.Equal(2, store.Recent().Count);
    }

    [Fact]
    public void 数据文件_不是明文JSON()
    {
        var store = new HistoryStore(_appData);
        store.Add(MakeRecord("r1"));

        var raw = File.ReadAllBytes(Path.Combine(_appData, "history.bin"));
        // AES-GCM 密文不应包含明文内容
        Assert.DoesNotContain("今晚吃饭吗"u8.ToArray(), raw);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }
}
