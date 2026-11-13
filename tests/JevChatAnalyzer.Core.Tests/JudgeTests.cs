using System.Text.Json.Nodes;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Judge.Laya;
using JevChatAnalyzer.Core.Messages;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class JudgeTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
    }

    [Fact]
    public void BuildRequest_只使用传入会话的消息_不跨会话泄漏上下文()
    {
        static ChatMessage Msg(long id, string sender, string text, string shard)
        {
            return new ChatMessage
            {
                LocalId = id,
                SenderWxId = sender,
                LocalType = 1,
                SortSeq = id,
                CreateTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + id),
                DisplayText = text,
                Location = new MessageLocation($@"C:\x\{shard}.db", "Msg_x", id)
            };
        }

        var sessionA = new[] { Msg(1, "wxid_a", "A1", "a"), Msg(2, "wxid_a", "A2", "a") };
        var sessionB = new[] { Msg(1, "wxid_b", "B1", "b"), Msg(2, "wxid_b", "B2", "b") };

        var requestA = JudgeProtocol.BuildRequest(sessionA, sessionA[1], "会话A", false);
        var requestB = JudgeProtocol.BuildRequest(sessionB, sessionB[1], "会话B", false);

        // 每个会话的 state 只由该会话的消息构成（laya 判读无跨会话共享状态）
        Assert.Equal(["A1"], requestA.Turns.Select(t => t.Text));
        Assert.Equal("A2", requestA.Focus.Text);
        Assert.Equal(["B1"], requestB.Turns.Select(t => t.Text));
        Assert.Equal("B2", requestB.Focus.Text);
        Assert.Equal("会话A", requestA.SessionTitle);
    }

    [Fact]
    public void BuildState_包含会话描述与待分析消息_引用并入()
    {
        var request = new JudgeRequest
        {
            SessionTitle = "小明",
            IsGroup = false,
            Turns = [new JudgeTurn("小明", "在吗", false)],
            Focus = new JudgeTurn("小明", "这个方案我看行", false),
            FocusQuote = "小明：明天开会怎么样"
        };

        var state = JudgeProtocol.BuildState(request);

        Assert.Contains("单聊", state["会话"]!.GetValue<string>());
        var focus = (JsonObject)state["待分析消息"]!;
        Assert.Equal("这个方案我看行", focus["text"]!.GetValue<string>());
        Assert.NotNull(state["待分析消息"]!["引用的原文"]);
        var recent = (JsonArray)state["最近对话"]!;
        Assert.Single(recent);
    }

    [Fact]
    public void BuildRequest_不把自己的写回判读当引用原文()
    {
        var focus = new ChatMessage
        {
            LocalId = 2,
            SenderWxId = "wxid_bob",
            LocalType = (57L << 32) | 49,
            SortSeq = 2,
            CreateTime = DateTimeOffset.Now,
            DisplayText = "赚麻了？",
            Location = new MessageLocation("m.db", "Msg_x", 2),
            Quote = new QuoteInfo { FromWxId = "wxid_me", FromDisplayName = "我", Content = "判读结论" }
        };

        Assert.Null(JudgeProtocol.BuildRequest([focus], focus, "群", true, "wxid_me").FocusQuote);
        Assert.NotNull(JudgeProtocol.BuildRequest([focus], focus, "群", true, "wxid_bob").FocusQuote);
    }

    [Fact]
    public void Specs_七题口径_类型与选项完备()
    {
        Assert.Equal(7, JudgeProtocol.Specs.Count);
        Assert.All(JudgeProtocol.Specs, s =>
        {
            if (s.Kind == "choice")
            {
                Assert.NotNull(s.Options);
                Assert.True(s.Options!.Length >= 5);
            }
            else if (s.Kind == "score")
            {
                Assert.NotNull(s.Levels);
                Assert.True(s.Levels!.Length >= 2);
            }
        });
        // id 唯一
        Assert.Equal(JudgeProtocol.Specs.Count, JudgeProtocol.Specs.Select(s => s.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("候选1", 3, 0)]
    [InlineData("候选3", 3, 2)]
    [InlineData("候选9", 3, -1)]
    [InlineData("候选0", 3, -1)]
    [InlineData("1", 3, -1)]
    [InlineData(null, 3, -1)]
    public void PickCandidate_解析候选标签(string? label, int count, int expected)
    {
        Assert.Equal(expected, JudgeProtocol.PickCandidate(label, count));
    }

    [Fact]
    public async Task JudgeAsync_三型答案进结论与摘要()
    {
        var source = new FakeSource
        {
            Answers =
            {
                [JudgeProtocol.IntentId] = new JudgeAnswer("choice", "seek_reaction", 0, 0.9),
                [JudgeProtocol.UrgencyId] = new JudgeAnswer("score", null, 2.5, 0.8),
                [JudgeProtocol.AdviceId] = new JudgeAnswer("choice", "reply_now", 0, 0.7),
                [JudgeProtocol.NeedReplyId] = new JudgeAnswer("noul", "是", 0.8, 0.8)
            }
        };
        var judge = new LayaJudge(ReadyModel(), source);

        var verdict = await judge.JudgeAsync(Request());

        Assert.Equal(4, verdict.Answers.Count);
        Assert.Equal("fake-model", verdict.Model);
        Assert.Equal("laya", verdict.BackendName);
        Assert.Equal("想引起我的反应或注意，急迫度 2.5/3，立即回复", verdict.Summary);
        Assert.False(verdict.IsBlindDraft);
    }

    [Fact]
    public async Task JudgeAsync_模型未就绪_抛JudgeException并指出目录()
    {
        var model = new LayaModel(NewDir()); // 空目录
        var judge = new LayaJudge(model, new FakeSource());

        var ex = await Assert.ThrowsAsync<JudgeException>(() => judge.JudgeAsync(Request()));

        Assert.Contains("未就绪", ex.Message);
        Assert.Contains($"0/{LayaModel.Files.Length}", ex.Message);
        Assert.Contains(model.Directory, ex.Message);
    }

    [Fact]
    public async Task JudgeAsync_引擎异常_包装为JudgeException()
    {
        var judge = new LayaJudge(ReadyModel(), new FakeSource { Fail = true });

        var ex = await Assert.ThrowsAsync<JudgeException>(() => judge.JudgeAsync(Request()));

        Assert.Contains("模拟引擎失败", ex.Message);
    }

    [Fact]
    public async Task RerankAsync_选中候选置顶()
    {
        var source = new FakeSource
        {
            Answers = { [JudgeProtocol.RerankId] = new JudgeAnswer("choice", "候选2", 0, 0.9) }
        };
        var judge = new LayaJudge(ReadyModel(), source);
        var candidates = Candidates("A", "B", "C");

        var ordered = await judge.RerankAsync(Request(), candidates);

        Assert.Equal(["B", "A", "C"], ordered.Select(c => c.Text));
        // 候选作为选项进入 state
        Assert.NotNull(source.LastState!["候选回复"]);
    }

    [Fact]
    public async Task RerankAsync_标签无效_保持原序()
    {
        var source = new FakeSource
        {
            Answers = { [JudgeProtocol.RerankId] = new JudgeAnswer("choice", "候选9", 0, 0.9) }
        };
        var judge = new LayaJudge(ReadyModel(), source);

        var ordered = await judge.RerankAsync(Request(), Candidates("A", "B", "C"));

        Assert.Equal(["A", "B", "C"], ordered.Select(c => c.Text));
    }

    [Fact]
    public async Task RerankAsync_候选不足两条_不调用引擎()
    {
        var source = new FakeSource { Fail = true }; // 一旦被调用就会抛
        var judge = new LayaJudge(ReadyModel(), source);

        var ordered = await judge.RerankAsync(Request(), Candidates("A"));

        Assert.Single(ordered);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public void Summarize_缺题不抛_空答案给空串()
    {
        Assert.Equal("", JudgeProtocol.Summarize(new Dictionary<string, JudgeAnswer>()));
    }

    private static JudgeRequest Request()
    {
        return new JudgeRequest
        {
            SessionTitle = "小明",
            Turns = [new JudgeTurn("小明", "在吗", false)],
            Focus = new JudgeTurn("小明", "这个方案我看行", false)
        };
    }

    private static IReadOnlyList<DraftSuggestion> Candidates(params string[] texts)
    {
        return texts.Select((text, i) => new DraftSuggestion(i + 1, text, "稳妥")).ToArray();
    }

    private LayaModel ReadyModel()
    {
        return new LayaModel(ReadyDir());
    }

    private string ReadyDir()
    {
        var dir = NewDir();
        foreach (var relative in LayaModel.Files)
        {
            var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        return dir;
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-judge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>答案来源替身：直接给答案，不加载权重。</summary>
    private sealed class FakeSource : IAnswerSource
    {
        public Dictionary<string, JudgeAnswer> Answers { get; } = [];
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public JsonObject? LastState { get; private set; }

        public string ModelId => "fake-model";

        public Task<IReadOnlyDictionary<string, JudgeAnswer>> AnswerAsync(JsonObject state,
            IReadOnlyList<QuestionSpec> specs, CancellationToken ct)
        {
            Calls++;
            LastState = state;
            if (Fail) throw new IOException("模拟引擎失败");
            var map = specs.Where(s => Answers.ContainsKey(s.Id))
                .ToDictionary(s => s.Id, s => Answers[s.Id]);
            return Task.FromResult<IReadOnlyDictionary<string, JudgeAnswer>>(map);
        }

        public Task WarmupAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
