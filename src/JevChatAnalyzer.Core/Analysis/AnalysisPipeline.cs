using System.Collections.Concurrent;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Judge.Laya;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Voice;

namespace JevChatAnalyzer.Core.Analysis;

/// <summary>
///     分析管线：会话 → 读取上下文 → 自动焦点（对方最新可分析消息，语音先转写）
///     → 判读 → 记账 → 自动写回入队。手动分析与实时监听共用此唯一路径。
///     依赖全部走 port（见 <see cref="AnalysisPorts" />），不持有应用级状态。
///     线程安全：内部串行锁，监听与手动点击同时触发时排队执行。
/// </summary>
public sealed class AnalysisPipeline
{
    /// <summary>「全部上下文」时的单次上限：避免把整库几万条塞进 laya。</summary>
    private const int MaxContextMessages = 2000;

    /// <summary>单次判读最多转写多少条语音（全部上下文时控制耗时）。</summary>
    private const int MaxVoiceTranscriptions = 50;

    /// <summary>已分析焦点去重（监听模式防重复）：sessionId#localId。</summary>
    private readonly ConcurrentDictionary<string, byte> _analyzed = new();

    private readonly IChatReader _chat;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IConversationJudge _judge;
    private readonly Func<AnalysisSettings> _settings;
    private readonly IAnalysisSink _sink;
    private readonly IVoiceTranscriber _voice;

    public AnalysisPipeline(IChatReader chat, IVoiceTranscriber voice, IConversationJudge judge,
        IAnalysisSink sink, Func<AnalysisSettings> settings)
    {
        _chat = chat;
        _voice = voice;
        _judge = judge;
        _sink = sink;
        _settings = settings;
    }

    /// <summary>分析完成（成功才触发）。UI 用于刷新结果卡片。</summary>
    public event Action<AnalysisOutcome>? Analyzed;

    /// <summary>撤销写回后忘记该焦点，允许再次分析/写回同一条消息。</summary>
    public void Forget(string sessionId, long localId)
    {
        _analyzed.TryRemove($"{sessionId}#{localId}", out _);
    }

    /// <summary>焦点自动选择：对方最新一条可分析消息（语音先转写）。</summary>
    private ChatMessage? PickFocus(IReadOnlyList<ChatMessage> messages, AnalysisSettings settings)
    {
        foreach (var m in messages.OrderByDescending(x => x.SortSeq))
        {
            if (m.IsMine || m.DisplayText.Length == 0) continue;
            if (m.LocalType == ChatStore.TypeVoice)
            {
                if (!settings.VoiceEnabled || !_voice.IsModelReady) continue;
                var text = _voice.Transcribe(m);
                if (text.Length == 0) continue;
                return m with { DisplayText = $"[语音] {text}" };
            }

            return m;
        }

        return null;
    }

    /// <summary>
    ///     把上下文里的语音消息替换成「[语音] 转写文本」。
    ///     转写开启才会发生；已带转写文本的消息不重复处理，失败原样保留（占位符）。
    /// </summary>
    private IReadOnlyList<ChatMessage> TranscribeVoiceMessages(IReadOnlyList<ChatMessage> messages,
        AnalysisSettings settings)
    {
        if (!settings.VoiceEnabled || !_voice.IsModelReady) return messages;
        // 先挑出待转写的语音，只处理最新的 MaxVoiceTranscriptions 条，避免「全部上下文」时长时间解码
        var pending = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            // 解码器对语音只产出占位符 "[语音]"（4 个字）；带了后续文字说明已经转写过
            if (m.LocalType == ChatStore.TypeVoice &&
                !(m.DisplayText.StartsWith("[语音]", StringComparison.Ordinal) &&
                  m.DisplayText.Length > "[语音]".Length))
                pending.Add(i);
        }

        var budget = pending.Skip(Math.Max(0, pending.Count - MaxVoiceTranscriptions)).ToHashSet();

        var result = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!budget.Contains(i))
            {
                result.Add(message);
                continue;
            }

            var text = _voice.Transcribe(message);
            result.Add(text.Length == 0 ? message : message with { DisplayText = $"[语音] {text}" });
        }

        return result;
    }

    /// <summary>
    ///     分析一个会话（焦点 = 对方最新可分析消息）。enqueue=true 时判读结论入待写队列。
    ///     blindFallback=true：laya 不可用时降级「盲起草」（无判读校准，不写回数据库）。
    ///     返回 null 表示没有可分析的对方消息。
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(ChatSession session, bool enqueue,
        bool blindFallback = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = _settings();
            // ContextTurns = 0 表示「全部」：SQL 不设 LIMIT，随后按 MaxContextMessages 截断
            var allContext = settings.ContextTurns <= 0;
            var messages = _chat.ReadSessionMessages(session.SessionId,
                allContext ? -1 : settings.ContextTurns + 5);
            if (messages.Count > MaxContextMessages)
                messages = messages.Skip(messages.Count - MaxContextMessages).ToList();
            // 语音转写开启时，上下文里的语音也转成文字再送入 laya（焦点消息同样受益）
            messages = TranscribeVoiceMessages(messages, settings);
            var focus = PickFocus(messages, settings);
            if (focus is null && !allContext)
            {
                // 最近窗口可能全是我方/系统消息：向外多扫一段，找到目标后再把该段作为上下文。
                var deeper = _chat.ReadSessionMessages(session.SessionId, Math.Max(200, settings.ContextTurns + 5));
                if (deeper.Count > MaxContextMessages)
                    deeper = deeper.Skip(deeper.Count - MaxContextMessages).ToList();
                deeper = TranscribeVoiceMessages(deeper, settings);
                focus = PickFocus(deeper, settings);
                if (focus is not null) messages = deeper;
            }

            if (focus is null) return AnalysisResult.NoMessage;
            var key = $"{session.SessionId}#{focus.LocalId}";
            if (_analyzed.ContainsKey(key)) return AnalysisResult.Duplicate; // 监听与手动重复触发同一焦点

            var request = JudgeProtocol.BuildRequest(
                messages, focus, session.DisplayName, session.Kind == SessionKind.Group, settings.MyWxId);
            JudgeVerdict verdict;
            try
            {
                verdict = await _judge.JudgeAsync(request, ct);
            }
            catch (JudgeException) when (blindFallback && settings.DraftEnabled &&
                                         !string.IsNullOrWhiteSpace(settings.DraftEndpoint))
            {
                // 盲起草兜底：无判读校准，不写回数据库
                var options = new DraftOptions
                {
                    Endpoint = new Uri(settings.DraftEndpoint),
                    ApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY"),
                    Model = settings.DraftModel,
                    CandidateCount = 3
                };
                using var client = new DraftClient(options);
                var (suggestions, summary) = await client.BlindDraftAsync(request, ct);
                verdict = new JudgeVerdict
                {
                    BackendName = JudgeVerdict.BlindDraftBackendName,
                    Model = settings.DraftModel,
                    JudgedAt = DateTimeOffset.Now,
                    Answers = new Dictionary<string, JudgeAnswer>(),
                    Summary = summary
                };
                _sink.BlindDraftProduced(suggestions);
            }

            _analyzed.TryAdd(key, 0);

            var judgment = new JudgmentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                WxId = settings.MyWxId,
                SessionId = session.SessionId,
                SessionTitle = session.DisplayName,
                FocusText = focus.DisplayText,
                MessageKey = VoiceTranscriber.CacheKey(focus.ServerId,
                    focus.CreateTime.ToUnixTimeSeconds(), focus.LocalId),
                Verdict = verdict,
                CreatedAt = DateTimeOffset.Now
            };
            var enqueued = _sink.Record(judgment, focus, enqueue);

            var outcome = new AnalysisOutcome(session, focus, verdict, enqueued);
            Analyzed?.Invoke(outcome);
            return AnalysisResult.Done(outcome);
        }
        finally
        {
            _gate.Release();
        }
    }
}
