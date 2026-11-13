namespace JevChatAnalyzer.Core.Ledger;

/// <summary>
///     写回状态的中文文案：界面与台账明细共用一个定义，避免各页各写一份 switch 后漂移。
/// </summary>
public static class WriteStateLabel
{
    /// <summary>状态短语；没有写回记录时为「未写回」。</summary>
    public static string Of(WriteState? state)
    {
        return state switch
        {
            WriteState.Committed => "已写回",
            WriteState.Revoked => "已撤销",
            WriteState.Pending => "待写回",
            WriteState.Orphaned => "待确认",
            _ => "未写回"
        };
    }

    /// <summary>台账明细行：生效/撤销用长措辞，撤销带时间。</summary>
    public static string Detail(WriteState state, DateTimeOffset? revokedAt)
    {
        return state switch
        {
            WriteState.Committed => "生效中",
            WriteState.Revoked => revokedAt is { } at ? $"已撤销 {at:MM-dd HH:mm}" : "已撤销",
            WriteState.Pending => "待写回",
            _ => "状态待确认"
        };
    }
}
