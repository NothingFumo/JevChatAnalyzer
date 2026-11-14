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

    /// <summary>可选背景（关系、备注、知识库命中）；非空时进入 state，并为每题附加“背景是上下文”的注记。</summary>
    public string? Background { get; init; }
}

/// <summary>题目规格。</summary>
public sealed record QuestionSpec(
    string Id,
    string Kind,
    string Instructions,
    (string Label, string Description, string Summary)[]? Options = null,
    string[]? Levels = null,
    (string True, string False)? Noul = null);

/// <summary>
///     判读契约（七题口径：意图/紧急度/需要回复/风险/情绪/建议/风格）：
///     state 构建、题目清单、请求组装、答案→结论、摘要、重排序解析。
///     纯逻辑，不依赖引擎；laya 只负责按题目给答案。
/// </summary>
public static class JudgeProtocol
{
    /// <summary>重排序题目 id（选项 = 各候选回复）。</summary>
    public const string RerankId = "best_reply";

    /// <summary>background 非空时追加到每道题后面，避免背景事实被判成跑题。</summary>
    public const string BackgroundNote = " 背景里给出的事实是既定上下文，不要当作跑题内容扣分。";

    /// <summary>关系类判断统一遵守“最新态优先”，避免被更早的怒气污染。</summary>
    private const string LatestStateRule =
        " 以最新一条为准：若对方已明确接受、道谢或收尾，按当前平静态判断，不要被更早的怒气或旧账带偏。";

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
        new(IntentId, "choice", "对方最新这条消息的核心意图是什么？" + LatestStateRule,
        [
            ("seek_reaction",
                "想引起我的反应或注意：卖关子、故意说半句、@我或连发消息等我接话。若同时有明确请求，按请求类判。",
                "想引起我的反应或注意"),
            ("ask_advice",
                "征询我的意见或建议：问我怎么看、选哪个、该怎么办。只是陈述自己的决定、不征求看法，不算。",
                "征询我的意见或建议"),
            ("share_daily",
                "分享日常或见闻：照片、见闻、心情碎片，没有要我表态或办事。若分享同时带明确邀约，按邀约判。",
                "分享日常或见闻"),
            ("ask_help",
                "请求帮助或办事：要我代做、代查、代买、帮忙联系。客套的“有空吗”后面跟具体事，也算。",
                "请求帮助或办事"),
            ("vent",
                "情绪宣泄或吐槽：主要想被听见，不是在向我要方案；常见“气死了”“烦死了”。若明确要求我出面解决，按请求帮助判。",
                "情绪宣泄或吐槽"),
            ("probe",
                "试探或闲聊铺垫：先问在不在、忙不忙、最近怎样，尚未提出真正话题。已带出请求的，按请求类判。",
                "试探或闲聊铺垫"),
            ("invite_meet",
                "约见面或邀约活动：给出或征询时间地点、问要不要一起。纯寒暄不算。",
                "约见面或邀约活动"),
            ("push_urgent",
                "催促我尽快完成或回复：问进度、说截止时间、表达等不及。",
                "催促我尽快做某事"),
            ("express_care",
                "表达关心或问候：问我身体、状态、吃饭、注意休息，主要目的不是求我办事。",
                "表达关心或问候"),
            ("other", "以上都不贴切，或一句话混了多种意图、难以归主。", "其他意图")
        ]),
        new(UrgencyId, "score", "这条消息有多急迫？", Levels:
        [
            "完全不急，随便什么时候回",
            "一般，今天内回就行",
            "比较急，几小时内想要回应",
            "非常急，希望立刻回应"
        ]),
        new(NeedReplyId, "choice", "对方是否需要我回复？" + LatestStateRule,
        [
            ("now", "提问、请求、邀约，明显在等我接话。", "需要现在回复"),
            ("later", "不急，但需要给一个回应。", "可以稍后回复"),
            ("no", "已接受、道谢、收尾，或只是通知、发泄。", "不需要回复")
        ]),
        new(RiskId, "noul",
            "这条消息是否让我面临实际损失风险（破财、账号或验证码泄露、隐私泄露、法律责任、被骗）？普通情绪冲突、催办、抱怨、亲密关系里的小额请客 → 否；陌生人/网友索要钱财、验证码、支付密码、点链接、转账或要求提供账号 → 是。以最新一条为准，不要因为历史里出现过收款码就判高风险。",
            Noul: ("存在破财、验证码/账号/隐私泄露、法律或被骗的实际风险。",
                "只是情绪、催办、闲聊、小额请客或普通广告，且没有索要敏感信息。")),
        new(EmotionId, "choice", "对方此刻的主导情绪是？" + LatestStateRule,
        [
            ("positive", "语气轻快、用表情或语气词、主动分享好事；不是客套。", "积极愉快"),
            ("calm", "陈述事实、安排事务，没有明显情绪色彩。", "平静中性"),
            ("anxious", "反复确认、担心结果、问“怎么办”“会不会”。", "焦虑担忧"),
            ("angry", "指责、反问、冷硬短句、“你总是”“凭什么”。", "愤怒不满"),
            ("sad", "表达失望、委屈、疲惫、“算了”“随便吧”。", "低落难过"),
            ("excited", "连发、感叹号、强烈期待，多为好消息或期待见面。", "兴奋热烈"),
            ("helpless", "叹气、认命、“我也没办法”“随你吧”，强度低于生气。", "无奈无力"),
            ("curious", "追问、打听、卖关子式反问，想获取信息而非表达情绪。", "好奇试探"),
            ("neutral_mixed", "情绪混杂、反讽难辨，或信息不足。", "混合或不明确")
        ]),
        new(AdviceId, "choice", "此刻最合适的应对动作是？" + LatestStateRule,
        [
            ("reply_now", "对方在等明确答复，且事实已清楚、能直接给出时间/方案/答案；关键信息缺失时不要为了快而编造。", "立即回复"),
            ("reply_later", "事情不急或需要整理，但必须先给一条“收到，稍后答”的确认。", "稍后从容回复"),
            ("reassure", "对方在气头上或明显受伤，先把情绪接住再谈事实；此时讲道理会升级。", "先安抚情绪再回应"),
            ("ask_detail", "信息不足，贸然答复会出错，或对方的话有歧义需要确认。", "追问细节弄清意图"),
            ("propose_next", "对方已认可方向，需要把下一步落到时间、地点或交付物。", "推进下一步（约时间/给方案）"),
            ("polite_decline", "做不到或不想答应，但希望保住关系；直接拒绝会造成更大损失时不要用。", "礼貌拒绝或保持边界"),
            ("observe", "对方在试探、发泄或只是通知，马上回复只会加戏；不是冷暴力，等对方说完更稳。", "暂时观望不必回复"),
            ("escalate", "涉及钱财、账号、隐私、法律或人身安全，需要先核实或请可信第三方介入。", "需要谨慎处理或求助他人")
        ]),
        new(ReplyStyleId, "choice", "若要回复，用什么语气/姿态最合适？只判姿态，不判具体内容。" + LatestStateRule,
        [
            ("playful", "关系轻松、对方也在开玩笑时；对正在生气或严肃事务不要用。", "调侃打趣"),
            ("normal", "中性、信息对等，既不热络也不冷淡。", "正常交流"),
            ("polite", "半熟关系、商务或需要保持距离时。", "礼貌客套"),
            ("praise", "对方分享成果、需要被认可时。", "肯定赞赏"),
            ("caring", "对方疲惫、生病或情绪低落，先给关心。", "关心问候"),
            ("vent_together", "对方在吐槽第三方或处境，跟着一起吐槽，但不升级攻击。", "共情吐槽"),
            ("sympathy", "对方遭遇不幸，需要被理解而不是被解决。", "表达同情"),
            ("soothe", "对方在气头上、自己有责任时，先降温再谈事实。", "先安抚道歉"),
            ("ask_detail", "信息不足，贸然表态会出错时。", "先问清细节"),
            ("decline", "做不到或不想答应，但想保住关系。", "温和婉拒"),
            ("hold", "对方在试探或发泄、继续说话只会升级时；不是冷暴力。", "暂不回复")
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

        var state = new JsonObject
        {
            ["待分析消息"] = focus,
            ["会话"] = request.IsGroup ? $"群聊「{request.SessionTitle}」" : $"与「{request.SessionTitle}」的单聊",
            ["最近对话"] = recent
        };
        if (!string.IsNullOrWhiteSpace(request.Background)) state["背景"] = request.Background;
        return state;
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
            if (spec.Options!.FirstOrDefault(o => o.Label == label) is { Summary: { } summary })
                parts.Add(summary);
        }

        if (answers.TryGetValue(UrgencyId, out var urgency)) parts.Add($"急迫度 {urgency.Value:0.0}/3");
        if (answers.TryGetValue(AdviceId, out var advice) && advice.Label is { } adviceLabel)
        {
            var spec = Specs.First(s => s.Id == AdviceId);
            if (spec.Options!.FirstOrDefault(o => o.Label == adviceLabel) is
                { Summary: { } adviceText }) parts.Add(adviceText);
        }

        return string.Join("，", parts);
    }

    /// <summary>选项的展示文案：已选摘要 +（有分布时）前二名及概率。</summary>
    public static string DescribeAnswer(QuestionSpec spec, JudgeAnswer answer)
    {
        if (spec.Kind != "choice" || answer.Label is null || spec.Options is null)
            return answer.ToString();

        var chosen = spec.Options.FirstOrDefault(o => o.Label == answer.Label);
        var text = chosen == default ? answer.Label : chosen.Summary;
        if (answer.Probabilities is { Count: > 1 } probabilities)
        {
            var top = probabilities
                .OrderByDescending(p => p.Value)
                .Take(2)
                .Select(p =>
                {
                    var option = spec.Options.FirstOrDefault(o => o.Label == p.Key);
                    var name = option == default ? p.Key : option.Summary;
                    return $"{name} {p.Value:P0}";
                });
            text += "（" + string.Join(" · ", top) + "）";
        }

        return text;
    }

    /// <summary>重排序题目：选项 = 各候选回复（label 为「候选N」）。</summary>
    public static QuestionSpec RerankSpec(IReadOnlyList<string> candidateTexts)
    {
        return new QuestionSpec(RerankId, "choice", "结合对话与判读结论，哪条回复最合适？",
            candidateTexts.Select((text, i) => ($"候选{i + 1}", text, text)).ToArray());
    }

    /// <summary>把「候选N」解析成 0 基下标；无法解析或越界返回 -1。</summary>
    public static int PickCandidate(string? label, int count)
    {
        if (label is null || !label.StartsWith("候选", StringComparison.Ordinal)) return -1;
        var idx = int.TryParse(label[2..], out var n) ? n - 1 : -1;
        return idx >= 0 && idx < count ? idx : -1;
    }
}
