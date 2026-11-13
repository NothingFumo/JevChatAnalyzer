using System.Text;
using System.Xml;
using System.Xml.Linq;
using ZstdSharp;

namespace JevChatAnalyzer.Core.Messages;

/// <summary>消息内容解码：类型占位、appmsg XML 解析、引用消息（refermsg）还原、zstd 解压。</summary>
public static class ContentDecoder
{
    /// <summary>把 local_type 大整数拆成 (一级类型, appmsg 子类型)。非 49 容器时子类型为 null。</summary>
    public static (long BaseType, int? AppSubType) SplitType(long localType)
    {
        const long typeMask = 0xFFFFFFFF;
        var baseType = localType & typeMask;
        if (baseType == ChatStore.TypeAppMsg && localType >> 32 > 0) return (baseType, (int)(localType >> 32));
        // local_type 恰为 49（无高位）时无法预知子类型，交给 XML 判断
        return (baseType, baseType == ChatStore.TypeAppMsg ? null : null);
    }

    /// <summary>解码消息内容为展示文本，引用消息额外产出 QuoteInfo。</summary>
    public static Decoded Decode(long localType, string content, Func<string, string> resolveDisplayName)
    {
        var (baseType, appSubType) = SplitType(localType);
        switch (baseType)
        {
            case ChatStore.TypeText:
                return new Decoded(content, null, null);
            case ChatStore.TypeImage:
                return new Decoded("[图片]", null, null);
            case ChatStore.TypeVoice:
                return new Decoded("[语音]", null, null);
            case ChatStore.TypeVideo:
                return new Decoded("[视频]", null, null);
            case ChatStore.TypeSticker:
                return new Decoded("[动画表情]", null, null);
            case ChatStore.TypeLocation:
                return new Decoded(DecodeLocation(content), null, null);
            case ChatStore.TypeVoip:
                return new Decoded("[通话]", null, null);
            case ChatStore.TypeSystem:
                return new Decoded(string.IsNullOrWhiteSpace(content) ? "[系统消息]" : SystemText(content), null, null);
            case ChatStore.TypeAppMsg:
                return DecodeAppMsg(content, appSubType, resolveDisplayName);
            default:
                return new Decoded(string.IsNullOrWhiteSpace(content) ? $"[类型 {baseType}]" : content, appSubType, null);
        }
    }

    /// <summary>appmsg 容器：解析交给 <see cref="QuoteMessage" />，这里只把结果映射成展示文本。</summary>
    private static Decoded DecodeAppMsg(string content, int? appSubType, Func<string, string> resolveDisplayName)
    {
        if (string.IsNullOrWhiteSpace(content)) return new Decoded("[链接]", appSubType, null);
        if (QuoteMessage.Parse(content, resolveDisplayName) is not { } msg)
            return new Decoded("[链接]", appSubType, null);
        var type = msg.AppType ?? appSubType ?? -1;
        var title = msg.Title;
        if (type == ChatStore.AppMsgQuote)
        {
            var text = string.IsNullOrWhiteSpace(title) ? "[引用消息]" : title;
            return new Decoded(text, ChatStore.AppMsgQuote, msg.Quote);
        }

        if (type == 19) return new Decoded(string.IsNullOrWhiteSpace(title) ? "[聊天记录]" : $"[聊天记录] {title}", 19, null);
        if (type == 6) return new Decoded(string.IsNullOrWhiteSpace(title) ? "[文件]" : $"[文件] {title}", 6, null);
        if (type is 2000 or 2001) return new Decoded(type == 2000 ? "[转账]" : "[红包]", type, null);
        return new Decoded(string.IsNullOrWhiteSpace(title) ? "[链接]" : title, type, null);
    }

    /// <summary>剥除系统消息 XML（如撤回提示）为纯文本。</summary>
    private static string SystemText(string content)
    {
        if (content.Contains('<') && content.Contains('>'))
            try
            {
                var doc = XDocument.Parse(content);
                return doc.Root?.Value.Trim() ?? content;
            }
            catch (XmlException)
            {
                // 非 XML 系统消息原样返回
            }

        return content.Trim();
    }

    /// <summary>位置消息：XML 提取 label/poiname。</summary>
    private static string DecodeLocation(string content)
    {
        try
        {
            var doc = XDocument.Parse(content);
            var location = doc.Descendants().FirstOrDefault(d => d.Name.LocalName == "location");
            if (location is null) return "[位置]";
            var poiname = (string?)location.Attribute("poiname");
            var label = (string?)location.Attribute("label");
            var text = FirstNonEmpty(poiname, label);
            return string.IsNullOrWhiteSpace(text) ? "[位置]" : $"[位置] {text}";
        }
        catch (XmlException)
        {
            return "[位置]";
        }
    }

    /// <summary>zstd 解压 compress_content（带微信前缀容错）；失败返回 null。</summary>
    public static string? TryDecompress(byte[] blob)
    {
        if (blob.Length < 4) return null;
        try
        {
            using var decompressor = new Decompressor();
            // zstd 帧以 0x28 0xB5 0x2F 0xFD 开头；微信可能带 1 字节版本前缀
            var offset = blob[0] == 0x28 && blob[1] == 0xB5 ? 0 : 1;
            var output = decompressor.Unwrap(blob.AsSpan(offset), (int)Math.Max(blob.Length * 8L, 1 << 16));
            return Encoding.UTF8.GetString(output);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    /// <summary>解码结果：展示文本 + appmsg 子类型 + 引用块（如有）。</summary>
    public readonly record struct Decoded(string DisplayText, int? AppMsgSubType, QuoteInfo? Quote);
}
