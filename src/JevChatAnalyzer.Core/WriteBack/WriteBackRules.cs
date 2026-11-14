using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.WriteBack;

/// <summary>
///     写回目标资格：与 AccountWriteBack.Commit 的库内校验保持同一套规则。
///     入队前先判定，避免把语音/图片等无法加引用块的消息放进待写队列后反复失败。
/// </summary>
public static class WriteBackRules
{
    public static bool CanAnnotate(ChatMessage message)
    {
        if (message.IsMine) return false;
        var subType = message.LocalType & 0xFFFFFFFF;
        if (subType == ChatStore.TypeText) return true;
        return subType == ChatStore.TypeAppMsg
               && message.Content.Contains("<refermsg>", StringComparison.Ordinal);
    }
}
