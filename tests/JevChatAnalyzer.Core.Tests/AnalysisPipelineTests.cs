using JevChatAnalyzer.Core.Analysis;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Judge.Laya;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

/// <summary>
///     管线通过 port 装配：四种依赖全部换成内存替身，不需要微信库、模型或界面。
/// </summary>
public class AnalysisPipelineTests
{
    private static readonly ChatSession Session = new()
    {
        SessionId = "456789@chatroom",
        DisplayName = "项目组",
        Kind = SessionKind.Group
    };

    private static readonly ChatMessage Mine = Message(1, 1791457011000, true, "我先说一句");
    private static readonly ChatMessage Theirs = Message(2, 1791457022000, false, "今晚吃饭吗");

    private static ChatMessage Message(long localId, long sortSeq, bool mine, string text)
    {
        return new ChatMessage
        {
            LocalId = localId,
            SenderWxId = mine ? "wxid_me" : "wxid_bob",
            IsMine = mine,
            LocalType = ChatStore.TypeText,
            SortSeq = sortSeq,
            CreateTime = DateTimeOffset.FromUnixTimeSeconds(1791457000 + sortSeq / 1000),
            Content = text,
            DisplayText = text,
            Location = new MessageLocation(@"C:\x\message_0.db", "Msg_abc", localId)
        };
    }

    [Fact]
    public async Task 焦点是对方最新一条_记账并入队()
    {
        var sink = new FakeSink();
        var pipeline = New(new FakeChat([Mine, Theirs]), new FakeJudge(Verdict("对方在约时间")), sink);

        var outcome = (await pipeline.AnalyzeAsync(Session, true)).Outcome;

        Assert.NotNull(outcome);
        Assert.Equal(Theirs.LocalId, outcome!.Focus.LocalId);
        Assert.True(outcome.Enqueued);
        var judgment = Assert.Single(sink.Judgments);
        Assert.Equal("对方在约时间", judgment.Verdict.Summary);
        Assert.Equal("456789@chatroom", judgment.SessionId);
    }

    [Fact]
    public async Task 同一焦点重复触发被去重_忘记后可再分析()
    {
        var pipeline = New(new FakeChat([Theirs]), new FakeJudge(Verdict("摘要")), new FakeSink());

        Assert.NotNull((await pipeline.AnalyzeAsync(Session, true)).Outcome);
        var duplicate = await pipeline.AnalyzeAsync(Session, true);
        Assert.Null(duplicate.Outcome);
        Assert.Equal(AnalysisSkip.AlreadyAnalyzed, duplicate.Skip);

        pipeline.Forget(Session.SessionId, Theirs.LocalId);
        Assert.NotNull((await pipeline.AnalyzeAsync(Session, true)).Outcome);
    }

    [Fact]
    public async Task 判读失败且不允许兜底时_异常上抛且不记账()
    {
        var sink = new FakeSink();
        var pipeline = New(new FakeChat([Theirs]), new FakeJudge(new JudgeException("模型未就绪")), sink);

        await Assert.ThrowsAsync<JudgeException>(() =>
            pipeline.AnalyzeAsync(Session, true, false));

        Assert.Empty(sink.Judgments);
    }

    [Fact]
    public async Task 没有对方消息时返回null_不记账()
    {
        var sink = new FakeSink();
        var pipeline = New(new FakeChat([Mine]), new FakeJudge(Verdict("摘要")), sink);

        var result = await pipeline.AnalyzeAsync(Session, true);
        Assert.Null(result.Outcome);
        Assert.Equal(AnalysisSkip.NoMessage, result.Skip);
        Assert.Empty(sink.Judgments);
    }

    private static AnalysisPipeline New(IChatReader chat, IConversationJudge judge, IAnalysisSink sink)
    {
        return new AnalysisPipeline(chat, new FakeVoice(), judge, sink, () => new AnalysisSettings
        {
            MyWxId = "wxid_me",
            ContextTurns = 50
        });
    }

    private static JudgeVerdict Verdict(string summary)
    {
        return new JudgeVerdict
        {
            BackendName = "laya",
            Model = "laya-multilingual",
            JudgedAt = DateTimeOffset.Now,
            Answers = new Dictionary<string, JudgeAnswer>(),
            Summary = summary
        };
    }

    private sealed class FakeChat(IReadOnlyList<ChatMessage> messages) : IChatReader
    {
        public IReadOnlyList<ChatMessage> ReadSessionMessages(string sessionId, int limit)
        {
            return messages;
        }
    }

    private sealed class FakeVoice : IVoiceTranscriber
    {
        public bool IsModelReady => false;

        public string Transcribe(ChatMessage voiceMessage)
        {
            return "";
        }
    }

    private sealed class FakeJudge : IConversationJudge
    {
        private readonly Exception? _error;
        private readonly JudgeVerdict? _verdict;

        public FakeJudge(JudgeVerdict verdict)
        {
            _verdict = verdict;
        }

        public FakeJudge(Exception error)
        {
            _error = error;
        }

        public Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct)
        {
            return _error is not null ? Task.FromException<JudgeVerdict>(_error) : Task.FromResult(_verdict!);
        }
    }

    private sealed class FakeSink : IAnalysisSink
    {
        public List<JudgmentRecord> Judgments { get; } = [];

        public IReadOnlyList<DraftSuggestion>? Blind { get; private set; }

        public void BlindDraftProduced(IReadOnlyList<DraftSuggestion> suggestions)
        {
            Blind = suggestions;
        }

        public bool Record(JudgmentRecord judgment, ChatMessage focus, bool enqueue)
        {
            Judgments.Add(judgment);
            return enqueue;
        }
    }
}
