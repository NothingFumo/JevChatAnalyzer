using Xunit;
using JevChatAnalyzer.Core.Keys;

namespace JevChatAnalyzer.Core.Tests;

public class RwkKeyReaderTests
{
    private const string SampleJson = """
        {
          "ok": true,
          "abi": 1,
          "weixinVersion": "4.1.12.55",
          "users": [
            {
              "wxId": "wxid_abc123",
              "name": "张三",
              "number": "zhangsan_wx",
              "phone": "138****0000",
              "dbKey": "48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f",
              "imageXorKey": "1a",
              "imageAesKey": "cfcd208495d565ef",
              "dataPath": "C:\\Users\\u\\Documents\\xwechat_files\\wxid_abc123_abcd"
            },
            {
              "wxId": "wxid_bad",
              "dbKey": "not-a-hex-key"
            }
          ]
        }
        """;

    [Fact]
    public void ParsePayload_解析有效JSON_过滤非法dbKey()
    {
        var accounts = RwkKeyReader.ParsePayload(SampleJson);

        var account = Assert.Single(accounts);
        Assert.Equal("wxid_abc123", account.WxId);
        Assert.Equal("48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f", account.DbKey);
        Assert.Equal("张三", account.NickName);
        Assert.Equal("4.1.12.55", account.WeixinVersion);
        Assert.Equal("C:\\Users\\u\\Documents\\xwechat_files\\wxid_abc123_abcd", account.DataPath);
        Assert.True(account.IsValidDbKey);
    }

    [Fact]
    public void ParsePayload_大小写不敏感_兼容不同字段命名()
    {
        var json = """{"WeixinVersion":"4.1.0","Users":[{"WXID":"wxid_x","DBKEY":"ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789"}]}""";

        var accounts = RwkKeyReader.ParsePayload(json);

        var account = Assert.Single(accounts);
        Assert.Equal("wxid_x", account.WxId);
        Assert.Equal("4.1.0", account.WeixinVersion);
    }

    [Fact]
    public void ParsePayload_无效JSON_抛出KeyReadException()
    {
        Assert.Throws<KeyReadException>(() => RwkKeyReader.ParsePayload("not json"));
    }

    [Fact]
    public void ParsePayload_空users_返回空列表()
    {
        var accounts = RwkKeyReader.ParsePayload("""{"ok":true,"users":[]}""");
        Assert.Empty(accounts);
    }
}
