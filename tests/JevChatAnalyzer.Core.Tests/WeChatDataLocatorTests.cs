using Xunit;
using JevChatAnalyzer.Core.Keys;

namespace JevChatAnalyzer.Core.Tests;

public class WeChatDataLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jca-xwechat-" + Guid.NewGuid().ToString("N"));

    private string MakeAccountDir(string name)
    {
        var dir = Path.Combine(_root, name, "db_storage");
        Directory.CreateDirectory(dir);
        return Path.Combine(_root, name);
    }

    [Fact]
    public void ListAccounts_只认含db_storage的目录()
    {
        var account1 = MakeAccountDir("wxid_one_abcd");
        MakeAccountDir("wxid_two_wxyz");
        Directory.CreateDirectory(Path.Combine(_root, "all_users"));
        Directory.CreateDirectory(Path.Combine(_root, "not_an_account"));

        var locator = new WeChatDataLocator();

        var accounts = locator.ListAccounts(_root);

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, a => a.WxId == "wxid_one" && a.Dir == account1);
        Assert.Contains(accounts, a => a.WxId == "wxid_two");
    }

    [Fact]
    public void CleanWxId_去掉末尾4位随机后缀()
    {
        Assert.Equal("wxid_abc123", WeChatDataLocator.CleanWxId("wxid_abc123_abcd"));
        Assert.Equal("wxid_abc123", WeChatDataLocator.CleanWxId("wxid_abc123"));
        // 后缀长度不是 4 时不剥（例如自定义微信号自带下划线）
        Assert.Equal("custom_wx", WeChatDataLocator.CleanWxId("custom_wx"));
        Assert.Equal("wxid_long_suffix", WeChatDataLocator.CleanWxId("wxid_long_suffix"));
    }

    [Fact]
    public void ListMessageDbs_按名称排序列出分片()
    {
        var account = MakeAccountDir("wxid_one_abcd");
        var msgDir = Path.Combine(account, "db_storage", "message");
        Directory.CreateDirectory(msgDir);
        File.WriteAllText(Path.Combine(msgDir, "message_2.db"), "");
        File.WriteAllText(Path.Combine(msgDir, "message_10.db"), "");
        File.WriteAllText(Path.Combine(msgDir, "message_1.db"), "");
        File.WriteAllText(Path.Combine(msgDir, "message_fts.db"), "");

        var dbs = WeChatDataLocator.ListMessageDbs(account);

        // message_fts.db 不符合 message_<数字>.db 分片命名，被排除；按数字序排列。
        Assert.Equal(3, dbs.Count);
        Assert.Equal(["message_1.db", "message_2.db", "message_10.db"], dbs.Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void ResolveAccountDir_接受目录或wxid()
    {
        var account = MakeAccountDir("wxid_one_abcd");
        var locator = new WeChatDataLocator();

        Assert.Equal(account, locator.ResolveAccountDir(account));
        Assert.Null(locator.ResolveAccountDir("wxid_missing"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败不影响结果 */ }
    }
}
