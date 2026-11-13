using System.Text.Json.Nodes;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Judge;

/// <summary>单轮对话（判读 state 的一部分）。</summary>
public sealed record JudgeTurn(string Speaker, string Text, bool IsMine);

/// <summary>判读请求：上下文轮次 + 待分析消息 + 会话描述。</summary>
public sealed record JudgeRequest
{
    /// <summary>会话标题（单聊为对方名，群聊为群名）。</summary>
    public string SessionTitle { get; init; } = "";

    /// <summary>true = 群聊。</summary>
    public bool IsGroup { get; init; }

    /// <summary>最近对话（按时间升序，不含待分析消息）。</summary>
    public required IReadOnlyList<JudgeTurn> Turns { get; init; }

    /// <summary>待分析消息（判读焦点）。</summary>
    public required JudgeTurn Focus { get; init; }

    /// <summary>焦点消息的引用原文（如引用了别人一段话）。</summary>
    public string? FocusQuote { get; init; }
}

/// <summary>题目规格。</summary>
public sealed record QuestionSpec(
    string Id,
    string Kind,
    string Instructions,
    (string Label, string Description)[]? Options = null,
    string[]? Levels = null);

/// <summary>
///     判读契约（七题口径：意图/紧急度/需要回复/风险/情绪/建议/风格）：
///     state 构建、题目清单、请求组装、答案→结论、摘要、重排序解析。
///     纯逻辑，不依赖引擎；laya 只负责按题目给答案。
/// </summary>
public static class JudgeProtocol
{
    /// <summary>重排序题目 id（选项 = 各候选回复）。</summary>
    public const string RerankId = "best_reply";

    /// <summary>
    ///     state 字符预算。引擎对非会话 state 从尾部截断（checkpoint max_len≈1024 token），
    ///     所以焦点必须排在最前，且对话本身要先进预算内，不能指望引擎帮我们截。
    /// </summary>
    private const int StateCharBudget = 900;

    public static readonly string IntentId = "intent";
    public static readonly string UrgencyId = "urgency";
    public static readonly string NeedReplyId = "need_reply";
    public static readonly string RiskId = "risk";
    public static readonly string EmotionId = "emotion";
    public static readonly string AdviceId = "advice";
    public static readonly string ReplyStyleId = "reply_style";

    /// <summary>题目清单（id / 类型 / 题干 / 选项）。</summary>
    public static IReadOnlyList<QuestionSpec> Specs { get; } =
    [
        new(IntentId, "choice", "对方最新这条消息的核心意图是什么？",
        [
            ("seek_reaction", "想引起我的反应或注意"),
            ("ask_advice", "征询我的意见或建议"),
            ("share_daily", "分享日常或见闻"),
            ("ask_help", "请求帮助或办事"),
            ("vent", "情绪宣泄或吐槽"),
            ("probe", "试探或闲聊铺垫"),
            ("invite_meet", "约见面或邀约活动"),
            ("push_urgent", "催促我尽快做某事"),
            ("express_care", "表达关心或问候"),
            ("other", "其他意图")
        ]),
        new(UrgencyId, "score", "这条消息有多急迫？", Levels:
        [
            "完全不急，随便什么时候回",
            "一般，今天内回就行",
            "比较急，几小时内想要回应",
            "非常急，希望立刻回应"
        ]),
        new(NeedReplyId, "noul", "这条消息需要我回复吗？"),
        new(RiskId, "noul", "这条消息包含风险、敏感或让我不适的内容吗？"),
        new(EmotionId, "choice", "对方此刻的主导情绪是？",
        [
            ("positive", "积极愉快"),
            ("calm", "平静中性"),
            ("anxious", "焦虑担忧"),
            ("angry", "愤怒不满"),
            ("sad", "低落难过"),
            ("excited", "兴奋热烈"),
            ("helpless", "无奈无力"),
            ("curious", "好奇试探"),
            ("neutral_mixed", "混合或不明确")
        ]),
        new(AdviceId, "choice", "此刻最合适的应对动作是？",
        [
            ("reply_now", "立即回复"),
            ("reply_later", "稍后从容回复"),
            ("reassure", "先安抚情绪再回应"),
            ("ask_detail", "追问细节弄清意图"),
            ("propose_next", "推进下一步（约时间/给方案）"),
            ("polite_decline", "礼貌拒绝或保持边界"),
            ("observe", "暂时观望不必回复"),
            ("escalate", "需要谨慎处理或求助他人")
        ]),
        new(ReplyStyleId, "choice", "若要回复，用什么语气最合适？",
        [
            ("formal", "正式认真"),
            ("casual", "轻松随意"),
            ("humorous", "幽默诙谐"),
            ("caring", "温暖关心"),
            ("concise", "简短直接")
        ])
    ];

    /// <summary>构建 state JSON：待分析消息在前，会话描述其次，最近对话最后（截断只会伤到最旧的尾部）。</summary>
    public static JsonObject BuildState(JudgeRequest request)
    {
        var focus = new JsonObject
        {
            ["speaker"] = request.Focus.Speaker,
            ["mine"] = request.Focus.IsMine,
            ["text"] = request.Focus.Text
        };
        if (!string.IsNullOrWhiteSpace(request.FocusQuote)) focus["引用的原文"] = request.FocusQuote;

        // 从最新一条往回取，直到预算用尽，再恢复时间升序
        var budget = StateCharBudget - request.Focus.Text.Length;
        var kept = new List<JudgeTurn>();
        for (var i = request.Turns.Count - 1; i >= 0; i--)
        {
            var turn = request.Turns[i];
            var cost = turn.Text.Length + 32; // 32 ≈ 每轮 JSON 的键名与标点开销
            if (budget - cost < 0) break;
            budget -= cost;
            kept.Add(turn);
        }

        kept.Reverse();

        var recent = new JsonArray();
        foreach (var turn in kept)
            recent.Add(new JsonObject
            {
                ["speaker"] = turn.Speaker,
                ["mine"] = turn.IsMine,
                ["text"] = turn.Text
            });

        return new JsonObject
        {
            ["待分析消息"] = focus,
            ["会话"] = request.IsGroup ? $"群聊「{request.SessionTitle}」" : $"与「{request.SessionTitle}」的单聊",
            ["最近对话"] = recent
        };
    }

    /// <summary>
    ///     从 ChatStore 消息组装判读请求（焦点消息 + 最近上下文 + 引用并入）。
    ///     myWxId 用于跳过「引用了本工具自己写回的判读」的引用块：写回会把判读挂到对方消息上，
    ///     再次判读那条消息时不能把自己的结论当成被引用的原文喂回去。
    /// </summary>
    public static JudgeRequest BuildRequest(IReadOnlyList<ChatMessage> sessionMessages, ChatMessage focus,
        string sessionTitle, bool isGroup, string? myWxId = null)
    {
        var focusIdx = -1;
        for (var i = sessionMessages.Count - 1; i >= 0; i--)
            if (sessionMessages[i].Location == focus.Location)
            {
                focusIdx = i;
                break;
            }

        var before = focusIdx > 0 ? sessionMessages.Take(focusIdx).ToList() : [];
        // 待分析消息若是我的发言，仍判读（自检场景）；上下文含对方最后一条
        var turns = before
            .Where(m => m.DisplayText.Length > 0)
            .Select(m => new JudgeTurn(m.SenderDisplayName, m.DisplayText, m.IsMine))
            .ToList();
        return new JudgeRequest
        {
            SessionTitle = sessionTitle,
            IsGroup = isGroup,
            Turns = turns,
            Focus = new JudgeTurn(focus.SenderDisplayName, focus.DisplayText, focus.IsMine),
            FocusQuote = focus.Quote is { } q && q.FromWxId != myWxId
                ? $"{q.FromDisplayName}：{q.Content}"
                : null
        };
    }

    /// <summary>答案 → 判读结论（含一句话摘要）。缺题的答案直接略过。</summary>
    public static JudgeVerdict ToVerdict(IReadOnlyDictionary<string, JudgeAnswer> answers, string model)
    {
        var kept = new Dictionary<string, JudgeAnswer>();
        foreach (var spec in Specs)
            if (answers.TryGetValue(spec.Id, out var answer))
                kept[spec.Id] = answer;

        return new JudgeVerdict
        {
            BackendName = "laya",
            Model = model,
            JudgedAt = DateTimeOffset.Now,
            Answers = kept,
            Summary = Summarize(kept)
        };
    }

    /// <summary>按题序生成一句话摘要（意图 + 急迫 + 建议）。</summary>
    public static string Summarize(IReadOnlyDictionary<string, JudgeAnswer> answers)
    {
        var parts = new List<string>();
        if (answers.TryGetValue(IntentId, out var intent) && intent.Label is { } label)
        {
            var spec = Specs.First(s => s.Id == IntentId);
            if (spec.Options!.FirstOrDefault(o => o.Label == label) is { Description: { } description })
                parts.Add(description);
        }

        if (answers.TryGetValue(UrgencyId, out var urgency)) parts.Add($"急迫度 {urgency.Value:0.0}/3");
        if (answers.TryGetValue(AdviceId, out var advice) && advice.Label is { } adviceLabel)
        {
            var spec = Specs.First(s => s.Id == AdviceId);
            if (spec.Options!.FirstOrDefault(o => o.Label == adviceLabel) is
                { Description: { } adviceText }) parts.Add(adviceText);
        }

        return string.Join("，", parts);
    }

    /// <summary>重排序题目：选项 = 各候选回复（label 为「候选N」）。</summary>
    public static QuestionSpec RerankSpec(IReadOnlyList<string> candidateTexts)
    {
        return new QuestionSpec(RerankId, "choice", "结合对话与判读结论，哪条回复最合适？",
            candidateTexts.Select((text, i) => ($"候选{i + 1}", text)).ToArray());
    }

    /// <summary>把「候选N」解析成 0 基下标；无法解析或越界返回 -1。</summary>
    public static int PickCandidate(string? label, int count)
    {
        if (label is null || !label.StartsWith("候选", StringComparison.Ordinal)) return -1;
        var idx = int.TryParse(label[2..], out var n) ? n - 1 : -1;
        return idx >= 0 && idx < count ? idx : -1;
    }
}
