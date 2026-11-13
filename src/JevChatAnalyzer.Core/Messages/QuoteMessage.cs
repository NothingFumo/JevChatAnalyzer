using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace JevChatAnalyzer.Core.Messages;

/// <summary>
///     引用消息（appmsg / type 57）的唯一解析与生成处：读端解码与写回拼装都走这里，
///     字段名、XML 转义与非法控制字符处理只有一份。
/// </summary>
public static class QuoteMessage
{
    /// <summary>appmsg 子类型：引用回复。</summary>
    public const int Type = 57;

    /// <summary>
    ///     解析 appmsg XML：取可见正文、子类型、引用块与 refermsg.fromusr。
    ///     非法字符会先清洗重试；仍不是 XML / 没有 appmsg 节点时返回 null。
    /// </summary>
    public static Parsed? Parse(string? content, Func<string, string>? resolveDisplayName = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var doc = Load(content);
        if (doc?.Root is not { } root) return null;
        var appmsg = root.Name.LocalName == "appmsg"
            ? root
            : root.Descendants().FirstOrDefault(d => d.Name.LocalName == "appmsg");
        if (appmsg is null) return null;
        var refer = appmsg.Descendants().FirstOrDefault(d => d.Name.LocalName == "refermsg");
        var referFrom = refer is null ? "" : Read(refer, "fromusr") ?? "";
        // type/title 必须取 appmsg 的直接子节点：真实库里存在 refermsg 排在 type 之前的样本（约 1.4%），
        // 按文档顺序取「第一个 type」会读到 refermsg 内部的子类型（1），把引用消息误判成普通 appmsg
        var typeText = ReadChild(appmsg, "type") ?? Read(appmsg, "type");
        return new Parsed(
            ReadChild(appmsg, "title") ?? Read(appmsg, "title") ?? "",
            int.TryParse(typeText, out var t) ? t : null,
            refer is null ? null : ParseReferMsg(refer, resolveDisplayName),
            referFrom);
    }

    /// <summary>解析 refermsg 节点为引用块。</summary>
    public static QuoteInfo? ParseReferMsg(XElement? refer, Func<string, string>? resolveDisplayNameFallback = null)
    {
        if (refer is null) return null;
        long? svrid = long.TryParse(Read(refer, "svrid"), out var s) ? s : null;
        DateTimeOffset? createTime = long.TryParse(Read(refer, "createtime"), out var ct)
            ? DateTimeOffset.FromUnixTimeSeconds(ct)
            : null;
        var from = Read(refer, "chatusr") ?? Read(refer, "fromusr");
        var content = Read(refer, "content") ?? "";
        // 群聊中引用文本可能带 "wxid_x:\n正文" 前缀，剥掉发送者前缀
        if (from is not null && content.StartsWith($"{from}:\n", StringComparison.Ordinal))
            content = content[(from.Length + 2)..];
        var display = Read(refer, "displayname");
        if (string.IsNullOrWhiteSpace(display) && from is not null) display = resolveDisplayNameFallback?.Invoke(from);
        return new QuoteInfo
        {
            FromWxId = from,
            FromDisplayName = display ?? "",
            Content = content,
            RefServerId = svrid,
            RefTime = createTime
        };
    }

    /// <summary>生成 message_content；与微信真实样本同形（type 57 + refermsg）。</summary>
    public static string Build(Parts parts)
    {
        var judgment = Truncate(parts.QuoteText, 800);
        var title = Truncate(parts.Title, 500);
        var now = parts.CreatedAt.ToUnixTimeSeconds();
        return $"""
                <?xml version="1.0"?>
                <msg>
                	<appmsg appid="" sdkver="0">
                		<title>{Escape(title)}</title>
                		<des />
                		<action />
                		<type>57</type>
                		<showtype>0</showtype>
                		<soundtype>0</soundtype>
                		<mediatagname />
                		<messageext />
                		<messageaction />
                		<content />
                		<contentattr>0</contentattr>
                		<url />
                		<lowurl />
                		<dataurl />
                		<lowdataurl />
                		<songalbumurl />
                		<songlyric />
                		<template_id />
                		<appattach>
                			<totllen>0</totllen>
                			<attachid />
                			<emoticonmd5 />
                			<fileext />
                			<aeskey />
                		</appattach>
                		<extinfo />
                		<sourceusername />
                		<sourcedisplayname />
                		<thumburl />
                		<md5 />
                		<statextstr />
                		<refermsg>
                			<content>{Escape(judgment)}</content>
                			<createtime>{now}</createtime>
                			<displayname>我</displayname>
                			<fromusr>{Escape(parts.ReferFromUsr)}</fromusr>
                			<svrid>{parts.ReferServerId}</svrid>
                			<msgsource></msgsource>
                			<type>1</type>
                			<chatusr>{Escape(parts.MyWxId)}</chatusr>
                		</refermsg>
                	</appmsg>
                	<fromusername>{Escape(parts.SenderWxId)}</fromusername>
                	<scene>0</scene>
                	<appinfo>
                		<version>1</version>
                		<appname></appname>
                	</appinfo>
                	<commenturl></commenturl>
                </msg>
                """;
    }

    /// <summary>XML 实体转义（&amp; 优先），并剔除 XML 1.0 非法控制字符（保留 \t \n \r）。</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c < 0x20 && c is not ('\t' or '\n' or '\r') ? ' ' : c);
        return sb.ToString().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string Truncate(string s, int max)
    {
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static XDocument? Load(string content)
    {
        try
        {
            return XDocument.Parse(content);
        }
        catch (XmlException)
        {
            var sb = new StringBuilder(content.Length);
            foreach (var c in content) sb.Append(c < 0x20 && c is not ('\t' or '\n' or '\r') ? ' ' : c);
            try
            {
                return XDocument.Parse(sb.ToString());
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }

    private static string? Read(XElement el, string name)
    {
        return el.Descendants().FirstOrDefault(d => d.Name.LocalName == name)?.Value.Trim();
    }

    private static string? ReadChild(XElement el, string name)
    {
        return el.Elements().FirstOrDefault(d => d.Name.LocalName == name)?.Value.Trim();
    }

    /// <summary>解析结果。ReferFromUsr 保留原始 fromusr，用于判定「这条引用块是不是本工具写的」。</summary>
    public readonly record struct Parsed(string Title, int? AppType, QuoteInfo? Quote, string ReferFromUsr);

    /// <summary>生成一条引用回复所需的全部字段：判读文本进 refermsg.content，对方原话进 title。</summary>
    public sealed record Parts
    {
        public required string QuoteText { get; init; }

        public required string Title { get; init; }

        public required string MyWxId { get; init; }

        /// <summary>refermsg.fromusr（本工具写判读伪会话，微信原生的则是被引用者）。</summary>
        public required string ReferFromUsr { get; init; }

        public required string SenderWxId { get; init; }

        public long ReferServerId { get; init; }

        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    }
}
