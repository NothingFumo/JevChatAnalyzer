using System.Text.Json;
using System.Text.Json.Nodes;

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

/// <summary>
/// 判读题库：参考工具 同源的七题口径（意图/紧急度/需要回复/风险/情绪/建议/风格）。
/// choice/score/noul 三类题目与 线协议一一对应。
/// </summary>
public static class JudgeQuestions
{
    public static readonly string IntentId = "intent";
    public static readonly string UrgencyId = "urgency";
    public static readonly string NeedReplyId = "need_reply";
    public static readonly string RiskId = "risk";
    public static readonly string EmotionId = "emotion";
    public static readonly string AdviceId = "advice";
    public static readonly string ReplyStyleId = "reply_style";

    /// <summary>构建 state JSON（插入序有语义：关系描述在前，最近对话在后）。</summary>
    public static JsonObject BuildState(JudgeRequest request)
    {
        var recent = new JsonArray();
        foreach (var turn in request.Turns)
        {
            recent.Add(new JsonObject
            {
                ["speaker"] = turn.Speaker,
                ["mine"] = turn.IsMine,
                ["text"] = turn.Text,
            });
        }
        var state = new JsonObject
        {
            ["会话"] = request.IsGroup ? $"群聊「{request.SessionTitle}」" : $"与「{request.SessionTitle}」的单聊",
            ["最近对话"] = recent,
            ["待分析消息"] = new JsonObject
            {
                ["speaker"] = request.Focus.Speaker,
                ["mine"] = request.Focus.IsMine,
                ["text"] = request.Focus.Text,
            },
        };
        if (!string.IsNullOrWhiteSpace(request.FocusQuote))
        {
            state["待分析消息"]["引用的原文"] = request.FocusQuote;
        }
        return state;
    }

    /// <summary>题目清单（id / 类型 / 题干 / 选项），供两类后端共用。</summary>
    public static IReadOnlyList<QuestionSpec> Specs { get; } =
    [
        new QuestionSpec(IntentId, "choice", "对方最新这条消息的核心意图是什么？",
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
            ("other", "其他意图"),
        ]),
        new QuestionSpec(UrgencyId, "score", "这条消息有多急迫？", Levels:
        [
            "完全不急，随便什么时候回",
            "一般，今天内回就行",
            "比较急，几小时内想要回应",
            "非常急，希望立刻回应",
        ]),
        new QuestionSpec(NeedReplyId, "noul", "这条消息需要我回复吗？"),
        new QuestionSpec(RiskId, "noul", "这条消息包含风险、敏感或让我不适的内容吗？"),
        new QuestionSpec(EmotionId, "choice", "对方此刻的主导情绪是？",
        [
            ("positive", "积极愉快"),
            ("calm", "平静中性"),
            ("anxious", "焦虑担忧"),
            ("angry", "愤怒不满"),
            ("sad", "低落难过"),
            ("excited", "兴奋热烈"),
            ("helpless", "无奈无力"),
            ("curious", "好奇试探"),
            ("neutral_mixed", "混合或不明确"),
        ]),
        new QuestionSpec(AdviceId, "choice", "此刻最合适的应对动作是？",
        [
            ("reply_now", "立即回复"),
            ("reply_later", "稍后从容回复"),
            ("reassure", "先安抚情绪再回应"),
            ("ask_detail", "追问细节弄清意图"),
            ("propose_next", "推进下一步（约时间/给方案）"),
            ("polite_decline", "礼貌拒绝或保持边界"),
            ("observe", "暂时观望不必回复"),
            ("escalate", "需要谨慎处理或求助他人"),
        ]),
        new QuestionSpec(ReplyStyleId, "choice", "若要回复，用什么语气最合适？",
        [
            ("formal", "正式认真"),
            ("casual", "轻松随意"),
            ("humorous", "幽默诙谐"),
            ("caring", "温暖关心"),
            ("concise", "简短直接"),
        ]),
    ];
}

/// <summary>题目规格。</summary>
public sealed record QuestionSpec(
    string Id,
    string Kind,
    string Instructions,
    (string Label, string Description)[]? Options = null,
    string[]? Levels = null);
