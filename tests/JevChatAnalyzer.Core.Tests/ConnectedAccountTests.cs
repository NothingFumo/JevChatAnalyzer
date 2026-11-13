using System.Security.Cryptography;
using JevChatAnalyzer.Core.Accounts;
using JevChatAnalyzer.Core.Db;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

/// <summary>
///     已连接账号：真实加密分片 + 真实 ChatStore，覆盖展示名回退、释放幂等与打开失败不留句柄。
/// </summary>
public class ConnectedAccountTests
{
    [Fact]
    public void Open_分片可用_展示名回退昵称()
    {
        var dir = CreateAccountDir("wxid_me", out var key);
        try
        {
            using var account = ConnectedAccount.Open(dir, "wxid_me", key, "老王的号");

            Assert.Equal("wxid_me", account.Chat.MyWxId);
            Assert.Equal(key, account.DbKeyHex);
            Assert.Equal(dir, account.AccountDir);
            // 联系人库里没有这个 wxid：解析结果等于 wxid，回退到调用方给的昵称
            Assert.Equal("老王的号", account.DisplayName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Dispose_可重复调用()
    {
        var dir = CreateAccountDir("wxid_me", out var key);
        try
        {
            var account = ConnectedAccount.Open(dir, "wxid_me", key);
            Assert.Equal("当前账号", account.DisplayName); // 既无昵称也无联系人名
            account.Dispose();
            account.Dispose(); // 幂等：关闭时重复调用不应抛
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Open_没有消息分片时抛出()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-emptydir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ConnectedAccount.Open(dir, "wxid_me", new string('a', 64)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string CreateAccountDir(string wxId, out string key)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-acct-" + Guid.NewGuid().ToString("N"));
        var messageDir = Path.Combine(dir, "db_storage", "message");
        Directory.CreateDirectory(messageDir);
        key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var dbPath = Path.Combine(messageDir, "message_0.db");
        File.WriteAllBytes(dbPath, []);
        using var db = SqlCipherNative.OpenReadWrite(dbPath, key);
        db.Execute("CREATE TABLE Name2Id (user_name TEXT)");
        db.Execute($"INSERT INTO Name2Id (user_name) VALUES ('{wxId}')");
        db.Execute("CREATE TABLE Msg_abc (local_id INTEGER, sort_seq INTEGER)");
        return dir;
    }
}
