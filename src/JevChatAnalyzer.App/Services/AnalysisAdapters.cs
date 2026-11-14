using JevChatAnalyzer.Core.Analysis;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Judge.Laya;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Voice;
using JevChatAnalyzer.Core.WriteBack;

namespace JevChatAnalyzer.App.Services;

// 管线端口的真实 adapter：把「当前连接」翻译成 port 需要的最小事实。
// 管线本身不再认识 AppState，测试用内存 adapter 即可覆盖同一 interface。

internal sealed class ConnectedChatReader(AppState state) : IChatReader
{
    public IReadOnlyList<ChatMessage> ReadSessionMessages(string sessionId, int limit)
    {
        return (state.Account?.Chat ?? throw new InvalidOperationException("微信数据未打开。")).ReadSessionMessages(sessionId,
            limit);
    }
}

internal sealed class ConnectedVoiceTranscriber(AppState state) : IVoiceTranscriber
{
    public bool IsModelReady => state.Transcriber.IsModelReady;

    public string Transcribe(ChatMessage voiceMessage)
    {
        if (!IsModelReady || state.Account is not { } account) return "";
        try
        {
            var silk = VoiceLocator.FindVoiceData(account.AccountDir, account.DbKeyHex,
                voiceMessage.ServerId, voiceMessage.SenderWxId,
                voiceMessage.CreateTime.ToUnixTimeSeconds(), voiceMessage.LocalId);
            if (silk is null) return "";
            var cacheKey = VoiceTranscriber.CacheKey(voiceMessage.ServerId,
                voiceMessage.CreateTime.ToUnixTimeSeconds(), voiceMessage.LocalId);
            return state.Transcriber.Transcribe(silk, cacheKey);
        }
        catch
        {
            return ""; // 判读不应因语音失败中断
        }
    }
}

internal sealed class LayaConversationJudge(LayaJudge judge) : IConversationJudge
{
    public Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct)
    {
        return judge.JudgeAsync(request, ct);
    }
}

internal sealed class LedgerAnalysisSink(AppState state) : IAnalysisSink
{
    public void BlindDraftProduced(IReadOnlyList<DraftSuggestion> suggestions)
    {
        state.BlindDraftResult = suggestions;
    }

    public bool Record(JudgmentRecord judgment, ChatMessage focus, bool enqueue)
    {
        state.Ledger.AddJudgment(judgment);
        if (!enqueue || !state.Settings.AutoWriteEnabled || judgment.Verdict.IsBlindDraft) return false;
        if (!WriteBackRules.CanAnnotate(focus)) return false; // 语音/图片等无法加引用块，只判读不写回
        if (state.WriteBack is not { } writeBack) return false;
        writeBack.EnqueuePending(judgment.WxId, judgment.SessionId, focus, judgment.Verdict.Summary, judgment.Id);
        return true;
    }
}
