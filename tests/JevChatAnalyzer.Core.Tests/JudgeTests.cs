using Xunit;
using System.Text.Json.Nodes;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Privacy;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class JudgeTests
{
    [Fact]
    public void BuildState_包含会话描述与待分析消息_引用并入()
    {
        var request = new JudgeRequest
        {
            SessionTitle = "小明",
            IsGroup = false,
            Turns = [new JudgeTurn("小明", "在吗", false)],
            Focus = new JudgeTurn("小明", "这个方案我看行", false),
            FocusQuote = "小明：明天开会怎么样",
        };

        var state = JudgeQuestions.BuildState(request);

        Assert.Contains("单聊", state["会话"]!.GetValue<string>());
        var focus = (JsonObject)state["待分析消息"]!;
        Assert.Equal("这个方案我看行", focus["text"]!.GetValue<string>());
        Assert.NotNull(state["待分析消息"]!["引用的原文"]);
        var recent = (JsonArray)state["最近对话"]!;
        Assert.Single(recent);
    }

    [Fact]
    public void Specs_七题口径_类型与选项完备()
    {
        Assert.Equal(7, JudgeQuestions.Specs.Count);
        Assert.All(JudgeQuestions.Specs, s =>
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
        Assert.Equal(JudgeQuestions.Specs.Count, JudgeQuestions.Specs.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Sanitizer_号码与URL掩蔽()
    {
        var sanitizer = new Sanitizer();
        var out1 = sanitizer.SanitizeText("打我电话 13812345678，看 https://example.com/path?a=1");
        Assert.DoesNotContain("13812345678", out1);
        Assert.DoesNotContain("example.com/path", out1);
        Assert.Contains("[手机号]", out1);
        Assert.Contains("example.com/…", out1);
    }

    [Fact]
    public void Sanitizer_说话人按首次出现顺序匿名化()
    {
        var sanitizer = new Sanitizer();
        Assert.Equal("我", sanitizer.AliasOf("我"));
        Assert.Equal("成员A", sanitizer.AliasOf("小明"));
        Assert.Equal("成员B", sanitizer.AliasOf("小红"));
        Assert.Equal("成员A", sanitizer.AliasOf("小明")); // 同名稳定
    }

    [Fact]
    public void Sanitizer_递归匿名化state()
    {
        var sanitizer = new Sanitizer();
        var state = JsonNode.Parse("""
            {"会话":"群聊","最近对话":[{"speaker":"小明","text":"打我电话 13812345678"},{"speaker":"我","text":"嗯"}]}
            """);
        var sanitized = (JsonObject)Sanitizer.SanitizeNode(state, sanitizer)!;

        var turns = (JsonArray)sanitized["最近对话"]!;
        var first = (JsonObject)turns[0]!;
        Assert.Equal("成员A", first["speaker"]!.GetValue<string>());
        // 文本中的手机号被掩蔽
        Assert.DoesNotContain("13812345678", first["text"]!.GetValue<string>());
        // 「我」保持原样
        var second = (JsonObject)turns[1]!;
        Assert.Equal("我", second["speaker"]!.GetValue<string>());
    }

    [Fact]
    public void CircuitBreaker_连续失败断开_冷却后半开()
    {
        var breaker = new CircuitBreaker(failThreshold: 2, coolDown: TimeSpan.FromMilliseconds(50));
        Assert.False(breaker.IsOpen);

        breaker.RecordFailure();
        Assert.False(breaker.IsOpen); // 未达阈值

        breaker.RecordFailure();
        Assert.True(breaker.IsOpen); // 达阈值断开

        Thread.Sleep(80); // 冷却结束
        Assert.False(breaker.IsOpen); // 半开放行
    }

    [Fact]
    public void CircuitBreaker_成功重置计数()
    {
        var breaker = new CircuitBreaker(failThreshold: 2);
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public async Task JudgeService_无后端时抛出中文异常()
    {
        await using var service = new JudgeService();
        var request = new JudgeRequest
        {
            Turns = [],
            Focus = new JudgeTurn("对方", "你好", false),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.JudgeAsync(request));
        Assert.Contains("没有可用的判读后端", ex.Message);
    }

    private sealed class StubBackend(string name, bool fail) : IJudgeBackend
    {
        public string Name => name;
        public string Model => "stub";
        public int Calls;

        public Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct)
        {
            Calls++;
            if (fail)
            {
                throw new IOException("模拟失败");
            }
            return Task.FromResult(new JudgeVerdict
            {
                BackendName = name,
                Model = Model,
                JudgedAt = DateTimeOffset.Now,
                Answers = new Dictionary<string, JudgeAnswer>(),
                Summary = "stub",
            });
        }

        public Task<IReadOnlyDictionary<string, JudgeAnswer>> AskAsync(object state,
            IReadOnlyList<QuestionSpec> specs, CancellationToken ct)
        {
            // 测试桩：按 specs 的第一题返回确定性答案（choice 取第一个选项）
            var answers = new Dictionary<string, JudgeAnswer>();
            foreach (var spec in specs)
            {
                answers[spec.Id] = spec.Kind == "choice" && spec.Options is { Length: > 0 } opts
                    ? new JudgeAnswer("choice", opts[0].Label, 1, 1)
                    : new JudgeAnswer("noul", "否", 0, 0.5);
            }
            return Task.FromResult<IReadOnlyDictionary<string, JudgeAnswer>>(answers);
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task JudgeService_首选失败_自动降级备用后端()
    {
        await using var service = new JudgeService();
        var primary = new StubBackend("主后端", fail: true);
        var fallback = new StubBackend("备后端", fail: false);
        service.Register(primary);
        service.Register(fallback);

        var request = new JudgeRequest { Turns = [], Focus = new JudgeTurn("对方", "你好", false) };
        var verdict = await service.JudgeAsync(request, preferredBackend: "主后端");

        Assert.Equal("备后端", verdict.BackendName);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task JudgeService_熔断中快速失败并降级()
    {
        await using var service = new JudgeService();
        var primary = new StubBackend("主后端", fail: true);
        var fallback = new StubBackend("备后端", fail: false);
        service.Register(primary);
        service.Register(fallback);

        var request = new JudgeRequest { Turns = [], Focus = new JudgeTurn("对方", "你好", false) };
        // 两次失败让主后端熔断（阈值 3）
        for (var i = 0; i < 3; i++)
        {
            await service.JudgeAsync(request, preferredBackend: "主后端");
        }
        Assert.Equal(3, primary.Calls);
        Assert.Equal(3, fallback.Calls);

        // 第四次：主后端熔断快速失败，不再调用它
        await service.JudgeAsync(request, preferredBackend: "主后端");
        Assert.Equal(3, primary.Calls);
        Assert.Equal(4, fallback.Calls);
    }
}
