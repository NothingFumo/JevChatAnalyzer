using System.Xml.Linq;
using JevChatAnalyzer.Core.Messages;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class ContentDecoderTests
{
    private static string ResolveName(string id) => id == "wxid_bob" ? "小明" : id;

    [Theory]
    [InlineData(1, 1, null)]
    [InlineData(244813135921, 49, 57)]
    [InlineData(49, 49, null)]
    public void SplitType_拆出一级类型与appmsg子类型(long localType, long expectedBase, int? expectedSub)
    {
        var (baseType, sub) = ContentDecoder.SplitType(localType);
        Assert.Equal(expectedBase, baseType);
        Assert.Equal(expectedSub, sub);
    }

    [Fact]
    public void SplitType_引用消息类型还原()
    {
        // (57<<32)|49 = 244813135921
        Assert.Equal(244813135921L, (57L << 32) | 49);
        var (baseType, sub) = ContentDecoder.SplitType(244813135921);
        Assert.Equal(49, baseType);
        Assert.Equal(57, sub);
    }

    [Fact]
    public void Decode_非文本类型输出占位符()
    {
        Assert.Equal("[图片]", ContentDecoder.Decode(3, "", ResolveName).DisplayText);
        Assert.Equal("[语音]", ContentDecoder.Decode(34, "", ResolveName).DisplayText);
        Assert.Equal("[视频]", ContentDecoder.Decode(43, "", ResolveName).DisplayText);
        Assert.Equal("[动画表情]", ContentDecoder.Decode(47, "", ResolveName).DisplayText);
    }

    [Fact]
    public void Decode_引用消息解析refermsg()
    {
        const string xml = """
            <?xml version="1.0"?>
            <msg>
              <appmsg appid="" sdkver="0">
                <title>这个方案我看行</title>
                <type>57</type>
                <refermsg>
                  <type>1</type>
                  <svrid>778899001</svrid>
                  <fromusr>wxid_me</fromusr>
                  <chatusr>wxid_bob</chatusr>
                  <displayname>小明</displayname>
                  <content>明天下午三点开会怎么样</content>
                  <createtime>1759900000</createtime>
                </refermsg>
              </appmsg>
            </msg>
            """;

        var decoded = ContentDecoder.Decode(244813135921, xml, ResolveName);

        Assert.Equal(57, decoded.AppMsgSubType);
        Assert.Equal("这个方案我看行", decoded.DisplayText);
        var quote = Assert.IsType<QuoteInfo>(decoded.Quote);
        Assert.Equal("wxid_bob", quote.FromWxId);
        Assert.Equal("小明", quote.FromDisplayName);
        Assert.Equal("明天下午三点开会怎么样", quote.Content);
        Assert.Equal(778899001, quote.RefServerId);
        Assert.Equal(1759900000, quote.RefTime!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void Decode_引用消息缺少displayname时回退解析器()
    {
        var xml = "<msg><appmsg><title>收到</title><type>57</type>" +
                  "<refermsg><svrid>1</svrid><chatusr>wxid_bob</chatusr><content>在吗</content></refermsg>" +
                  "</appmsg></msg>";

        var decoded = ContentDecoder.Decode(244813135921, xml, ResolveName);

        Assert.Equal("小明", decoded.Quote!.FromDisplayName);
    }

    [Fact]
    public void Decode_群聊引用文本剥除发送者前缀()
    {
        var xml = "<msg><appmsg><title>好的</title><type>57</type>" +
                  "<refermsg><svrid>2</svrid><chatusr>wxid_bob</chatusr><displayname>小明</displayname>" +
                  "<content>wxid_bob:\n今晚吃饭吗</content></refermsg></appmsg></msg>";

        var decoded = ContentDecoder.Decode(244813135921, xml, ResolveName);

        Assert.Equal("今晚吃饭吗", decoded.Quote!.Content);
    }

    [Fact]
    public void Decode_appmsg链接与非引用类型()
    {
        const string link = "<msg><appmsg><title>一篇好文章</title><type>5</type></appmsg></msg>";
        var decoded = ContentDecoder.Decode(49, link, ResolveName);
        Assert.Equal("一篇好文章", decoded.DisplayText);
        Assert.Equal(5, decoded.AppMsgSubType);

        const string file = "<msg><appmsg><title>报告.pdf</title><type>6</type></appmsg></msg>";
        decoded = ContentDecoder.Decode((6L << 32) | 49, file, ResolveName);
        Assert.Contains("报告.pdf", decoded.DisplayText);
    }

    [Fact]
    public void Decode_非法XML不抛异常()
    {
        var decoded = ContentDecoder.Decode(49, "<不是xml的垃圾", ResolveName);
        Assert.NotEmpty(decoded.DisplayText);
        Assert.Null(decoded.Quote);
    }

    [Fact]
    public void ParseReferMsg_空节点返回null()
    {
        Assert.Null(ContentDecoder.ParseReferMsg(null, null));
    }
}
