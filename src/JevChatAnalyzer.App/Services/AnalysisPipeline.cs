using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.History;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Voice;

namespace JevChatAnalyzer.App.Services;

/// <summary>一次分析的结果。</summary>
public sealed record AnalysisOutcome(ChatSession Session, ChatMessage Focus, JudgeVerdict Verdict, bool Enqueued);

/// <summary>
/// 分析管线：会话 → 读取上下文 → 自动焦点（对方最新可分析消息，语音先转写）
/// → 判读 → 历史 → 自动写回入队。手动分析与实时监听共用此唯一路径。
/// 线程安全：内部串行锁，监听与手动点击同时触发时排队执行。
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>已分析焦点去重（监听模式防重复）：sessionId#localId。</summary>
    private readonly HashSet<string> _analyzed = [];

    /// <summary>分析完成（成功才触发）。UI 用于刷新结果卡片。</summary>
    public event Action<AnalysisOutcome>? Analyzed;

    /// <summary>焦点自动选择：对方最新一条可分析消息（语音先转写）。</summary>
    public async Task<ChatMessage?> PickFocusAsync(AppState state, IReadOnlyList<ChatMessage> messages)
    {
        foreach (var m in messages.OrderByDescending(x => x.SortSeq))
        {
            if (m.IsMine || m.DisplayText.Length == 0)
            {
                continue;
            }
            if (m.LocalType == ChatStore.TypeVoice)
            {
                if (!state.Settings.VoiceEnabled || !state.Transcriber.IsModelReady ||
                    state.CurrentAccountDir is null)
                {
                    continue;
                }
                try
                {
                    var silk = VoiceLocator.FindVoiceData(state.CurrentAccountDir, state.CurrentDbKey!,
                        m.ServerId, m.SenderWxId, m.CreateTime.ToUnixTimeSeconds(), m.LocalId);
                    if (silk is null)
                    {
                        continue;
                    }
                    var cacheKey = VoiceTranscriber.CacheKey(m.ServerId,
                        m.CreateTime.ToUnixTimeSeconds(), m.LocalId);
                    var text = state.Transcriber.Transcribe(silk, cacheKey);
                    if (text.Length == 0)
                    {
                        continue;
                    }
                    return m with { DisplayText = $"[语音] {text}" };
                }
                catch
                {
                    continue;
                }
            }
            return m;
        }
        return await Task.FromResult<ChatMessage?>(null);
    }

    /// <summary>
    /// 分析一个会话（焦点 = 对方最新可分析消息）。enqueue=true 时判读结论入待写队列。
    /// blindFallback=true：判读后端全挂时降级「盲起草」（无判读校准，不写回数据库）。
    /// 返回 null 表示没有可分析的对方消息。
    /// </summary>
    public async Task<AnalysisOutcome?> AnalyzeAsync(AppState state, ChatSession session,
        bool enqueue, bool blindFallback = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var chat = state.Chat ?? throw new InvalidOperationException("微信数据未打开。");
            var messages = chat.ReadSessionMessages(session.SessionId,
                limit: state.Settings.ContextTurns + 5);
            var focus = await PickFocusAsync(state, messages);
            if (focus is null)
            {
                return null;
            }
            var key = $"{session.SessionId}#{focus.LocalId}";
            if (_analyzed.Contains(key))
            {
                return null; // 监听与手动重复触发同一焦点
            }

            var request = JudgeService.BuildRequest(
                messages, focus, session.DisplayName, session.Kind == SessionKind.Group);
            JudgeVerdict verdict;
            try
            {
                verdict = await state.Judge.JudgeAsync(request, ct: ct);
            }
            catch (JudgeException) when (blindFallback && state.Settings.DraftEnabled &&
                                          !string.IsNullOrWhiteSpace(state.Settings.DraftEndpoint))
            {
                // 盲起草兜底：无判读校准，不写回数据库
                var options = new DraftOptions
                {
                    Endpoint = new Uri(state.Settings.DraftEndpoint),
                    ApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY"),
                    Model = state.Settings.DraftModel,
                    CandidateCount = 3,
                };
                var client = new DraftClient(options);
                var (suggestions, summary) = await client.BlindDraftAsync(request, ct);
                verdict = new JudgeVerdict
                {
                    BackendName = "盲起草",
                    Model = state.Settings.DraftModel,
                    JudgedAt = DateTimeOffset.Now,
                    Answers = new Dictionary<string, JudgeAnswer>(),
                    Summary = summary,
                };
                state.BlindDraftResult = suggestions;
            }
            _analyzed.Add(key);

            state.History.Add(new HistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                WxId = state.CurrentWxId ?? "",
                SessionId = session.SessionId,
                SessionTitle = session.DisplayName,
                FocusText = focus.DisplayText,
                MessageKey = VoiceTranscriber.CacheKey(focus.ServerId,
                    focus.CreateTime.ToUnixTimeSeconds(), focus.LocalId),
                Verdict = verdict,
                CreatedAt = DateTimeOffset.Now,
            });

            var enqueued = false;
            if (enqueue && state.Settings.AutoWriteEnabled && verdict.BackendName != "盲起草")
            {
                state.WriteBack.EnqueuePending(state.CurrentWxId!, session.SessionId,
                    focus, verdict, quoteText: verdict.Summary);
                state.EnsureAutoWriteWatch();
                enqueued = true;
            }

            var outcome = new AnalysisOutcome(session, focus, verdict, enqueued);
            Analyzed?.Invoke(outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }
}
