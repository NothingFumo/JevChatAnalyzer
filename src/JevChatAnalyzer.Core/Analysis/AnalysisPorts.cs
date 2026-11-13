using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Analysis;

/// <summary>一次分析的结果。</summary>
public sealed record AnalysisOutcome(ChatSession Session, ChatMessage Focus, JudgeVerdict Verdict, bool Enqueued);

/// <summary>判读窗口参数：每次分析取一次快照，设置改了立刻生效。</summary>
public sealed record AnalysisSettings
{
    public required string MyWxId { get; init; }

    /// <summary>上下文条数；&lt;= 0 表示「全部」（上限见管线常量）。</summary>
    public int ContextTurns { get; init; } = 50;

    public bool VoiceEnabled { get; init; }

    public bool DraftEnabled { get; init; }

    public string DraftEndpoint { get; init; } = "";

    public string DraftModel { get; init; } = "";
}

/// <summary>读会话消息。真实 adapter 是微信库，测试 adapter 是内存列表。</summary>
public interface IChatReader
{
    /// <summary>按 sort_seq 升序返回；limit 为倒序截取条数，-1 表示不限。</summary>
    IReadOnlyList<ChatMessage> ReadSessionMessages(string sessionId, int limit);
}

/// <summary>语音转写。adapter 负责定位 Silk 数据与缓存，端口只回答「文本是什么」。</summary>
public interface IVoiceTranscriber
{
    bool IsModelReady { get; }

    /// <summary>转写一条语音；模型未就绪或找不到数据时返回空串，不抛。</summary>
    string Transcribe(ChatMessage voiceMessage);
}

/// <summary>判读引擎（laya）。</summary>
public interface IConversationJudge
{
    Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct);
}

/// <summary>判读结果的去处：台账记账、待写队列、盲起草候选。</summary>
public interface IAnalysisSink
{
    /// <summary>判读全挂时的兜底候选（仅供界面展示，不写回库）。</summary>
    void BlindDraftProduced(IReadOnlyList<DraftSuggestion> suggestions);

    /// <summary>记账并按需入待写队列；返回是否真的入了队。</summary>
    bool Record(JudgmentRecord judgment, ChatMessage focus, bool enqueue);
}
