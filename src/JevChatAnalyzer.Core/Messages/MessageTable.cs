using System.Security.Cryptography;
using System.Text;

namespace JevChatAnalyzer.Core.Messages;

/// <summary>
///     会话 id → 消息表名（<c>Msg_&lt;md5&gt;</c>）的唯一换算处。读端与写回端共用，表名不可能分叉。
/// </summary>
public static class MessageTable
{
    /// <summary>会话 id 的 MD5 十六进制（微信表名后缀）。</summary>
    public static string Hash(string sessionId)
    {
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(sessionId)));
    }

    /// <summary>
    ///     会话 id → 表名。反查不到名字的会话 id 是 <c>md5:&lt;hash&gt;</c>，直接取其哈希部分；
    ///     否则再哈希一次会得到一个不存在的表（会话可见但永远读不出消息）。
    /// </summary>
    public static string For(string sessionId)
    {
        return "Msg_" + (sessionId.StartsWith("md5:", StringComparison.Ordinal) ? sessionId[4..] : Hash(sessionId));
    }
}
