namespace JevChatAnalyzer.Core.Messages;

/// <summary>会话类型。</summary>
public enum SessionKind
{
    Unknown = 0,
    Single = 1, // 单聊（wxid_*）
    Group = 2, // 群聊（*@chatroom）
    Official = 3, // 公众号等（gh_*）
    System = 4 // 系统账号（weixin / filehelper 等）
}

/// <summary>会话列表条目。</summary>
public sealed record ChatSession
{
    /// <summary>会话标识：单聊为对方 wxid，群聊为 xxx@chatroom。</summary>
    public required string SessionId { get; init; }

    /// <summary>展示名（群名 / 备注 / 昵称 / wxid 回退）。</summary>
    public string DisplayName { get; init; } = "";

    public SessionKind Kind { get; init; }

    /// <summary>最近消息预览。</summary>
    public string Summary { get; init; } = "";

    /// <summary>会话内消息数（跨分片聚合）。</summary>
    public long MessageCount { get; init; }

    public DateTimeOffset LastTime { get; init; }
}

/// <summary>消息在数据库中的定位键：分片库 + 表 + local_id。</summary>
public sealed record MessageLocation(string ShardPath, string TableName, long LocalId)
{
    public override string ToString()
    {
        return $"{Path.GetFileName(ShardPath)}::{TableName}#{LocalId}";
    }
}

/// <summary>消息附带的引用块（来自引用消息的 refermsg 解析，或聊天记录转发的摘录）。</summary>
public sealed record QuoteInfo
{
    /// <summary>被引用者的 wxid。</summary>
    public string? FromWxId { get; init; }

    /// <summary>被引用者展示名。</summary>
    public string? FromDisplayName { get; init; }

    /// <summary>被引用原文。</summary>
    public string Content { get; init; } = "";

    /// <summary>被引用消息的 server_id（svrid，可反查）。</summary>
    public long? RefServerId { get; init; }

    /// <summary>被引用消息的时间（refermsg.createtime）。</summary>
    public DateTimeOffset? RefTime { get; init; }
}

/// <summary>消息条目（已跨分片排序、已还原发送者与引用）。</summary>
public sealed record ChatMessage
{
    public required long LocalId { get; init; }

    /// <summary>服务端消息 ID（跨库定位 / 语音匹配主键）。</summary>
    public long? ServerId { get; init; }

    /// <summary>发送者 wxid（real_sender_id 经 Name2Id 还原；单聊为自己的 wxid）。</summary>
    public required string SenderWxId { get; init; }

    /// <summary>发送者展示名（自己/我、群昵称 > 备注 > 昵称 > wxid）。</summary>
    public string SenderDisplayName { get; init; } = "";

    /// <summary>true = 我发送的。</summary>
    public bool IsMine { get; init; }

    /// <summary>4.x local_type 大整数（如 244813135921 = 引用回复）。</summary>
    public required long LocalType { get; init; }

    /// <summary>appmsg 容器下的子类型（local_type 低 32 位为 49 时拆出，其余为 null）。</summary>
    public int? AppMsgSubType { get; init; }

    /// <summary>毫秒排序序列；缺失时 = create_time * 1000。</summary>
    public required long SortSeq { get; init; }

    public required DateTimeOffset CreateTime { get; init; }

    /// <summary>原始内容：文本即正文，appmsg 为 XML，非文本可能为空。</summary>
    public string Content { get; init; } = "";

    /// <summary>消息来源元数据（source 列，可能 zstd 压缩存储；写回引用消息时填入 refermsg.msgsource）。</summary>
    public string MsgSource { get; init; } = "";

    /// <summary>展示文本（解析后的正文或占位符，如 [图片]）。</summary>
    public string DisplayText { get; init; } = "";

    /// <summary>引用块（仅引用消息等场景非空）。</summary>
    public QuoteInfo? Quote { get; init; }

    public required MessageLocation Location { get; init; }

    /// <summary>判读结论（判读后有值）。</summary>
    public JudgeVerdict? Verdict { get; set; }
}

/// <summary>一次判读的结论（题目 id → 结论摘要）。</summary>
public sealed record JudgeVerdict
{
    /// <summary>盲起草兜底的来源标记（判读不可用时的产物，不写回数据库）。</summary>
    public const string BlindDraftBackendName = "盲起草";

    public required string BackendName { get; init; }
    public required string Model { get; init; }
    public DateTimeOffset JudgedAt { get; init; }

    /// <summary>题目 id → (标签, 概率或分值)。</summary>
    public required IReadOnlyDictionary<string, JudgeAnswer> Answers { get; init; }

    /// <summary>用于展示的一句话摘要（如「对方在征询意见，不急」）。</summary>
    public string Summary { get; init; } = "";

    /// <summary>这条结论是否来自盲起草（未经判读校准）。</summary>
    public bool IsBlindDraft => BackendName == BlindDraftBackendName;
}

/// <summary>单题答案。</summary>
public sealed record JudgeAnswer(string Kind, string? Label, double Value, double Confidence,
    IReadOnlyDictionary<string, double>? Probabilities = null)
{
    public override string ToString()
    {
        return Kind switch
        {
            "choice" => $"{Label} ({Confidence:P0})",
            "score" => $"{Value:0.00}",
            _ => $"{(Value >= 0.5 ? "是" : "否")} ({Confidence:P0})"
        };
    }
}

public static class SessionKindExtensions
{
    public static SessionKind Classify(string sessionId)
    {
        if (sessionId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)) return SessionKind.Group;
        if (sessionId.StartsWith("gh_", StringComparison.OrdinalIgnoreCase)) return SessionKind.Official;
        if (sessionId is "weixin" or "filehelper" or "fmessage" or "newsapp" ||
            sessionId.StartsWith("gh_", StringComparison.OrdinalIgnoreCase))
            return SessionKind.System;
        return sessionId.Contains('@') ? SessionKind.Unknown : SessionKind.Single;
    }
}
