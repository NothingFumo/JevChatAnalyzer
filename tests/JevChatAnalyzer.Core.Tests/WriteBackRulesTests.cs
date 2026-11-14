using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.WriteBack;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

/// <summary>写回资格：只有对方的文本或引用回复才能加引用块。</summary>
public class WriteBackRulesTests
{
    private static ChatMessage Message(long localType, bool mine = false, string content = "")
    {
        return new ChatMessage
        {
            LocalId = 1,
            SenderWxId = "wxid_bob",
            IsMine = mine,
            LocalType = localType,
            SortSeq = 1,
            CreateTime = DateTimeOffset.UnixEpoch,
            Content = content,
            DisplayText = "x",
            Location = new MessageLocation(@"C:\x\message_0.db", "Msg_abc", 1)
        };
    }

    [Fact]
    public void 对方文本可写回()
    {
        Assert.True(WriteBackRules.CanAnnotate(Message(ChatStore.TypeText)));
    }

    [Fact]
    public void 自己的消息不可写回()
    {
        Assert.False(WriteBackRules.CanAnnotate(Message(ChatStore.TypeText, mine: true)));
    }

    [Fact]
    public void 语音与图片不可写回()
    {
        Assert.False(WriteBackRules.CanAnnotate(Message(34))); // 语音
        Assert.False(WriteBackRules.CanAnnotate(Message(3))); // 图片
    }

    [Fact]
    public void 只有带refermsg的appmsg可写回()
    {
        const long quoteReply = 244813135921; // 49 容器 + 高 32 位
        Assert.True(WriteBackRules.CanAnnotate(
            Message(quoteReply, content: "<msg><appmsg><refermsg><title>x</title></refermsg></appmsg></msg>")));
        Assert.False(WriteBackRules.CanAnnotate(
            Message(49, content: "<msg><appmsg><title>分享</title></appmsg></msg>")));
    }
}
