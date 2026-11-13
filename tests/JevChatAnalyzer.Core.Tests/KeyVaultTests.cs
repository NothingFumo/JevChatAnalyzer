using JevChatAnalyzer.Core.Keys;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class KeyVaultTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jca-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* 测试清理失败不影响结果 */
        }
    }

    private KeyVault CreateVault()
    {
        return new KeyVault(Path.Combine(_tempDir, "keys.json"));
    }

    [Fact]
    public void SaveAndGet_往返保真()
    {
        var vault = CreateVault();
        vault.Save(new WeChatAccountInfo
        {
            WxId = "wxid_t1",
            DbKey = "48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f",
            ImageXorKey = "1a",
            ImageAesKey = "cfcd208495d565ef",
            DataPath = @"C:\xwechat_files\wxid_t1_abcd",
            WeixinVersion = "4.1.12.55"
        });

        var entry = vault.Get("wxid_t1");

        Assert.NotNull(entry);
        Assert.Equal("48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f", entry.DbKeyCipher);
        Assert.Equal("1a", entry.ImageXorKeyCipher);
        Assert.Equal("cfcd208495d565ef", entry.ImageAesKeyCipher);
        Assert.Equal("auto", entry.Source);
    }

    [Fact]
    public void Save_覆盖同WxId_只保留一份()
    {
        var vault = CreateVault();
        vault.SaveManual("wxid_t1", "48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f", null);
        vault.SaveManual("wxid_t1", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null);

        var entry = vault.Get("wxid_t1");

        Assert.Single(vault.ListWxIds());
        Assert.NotNull(entry);
        Assert.Equal('f', entry.DbKeyCipher[0]);
        Assert.Equal("manual", entry.Source);
    }

    [Fact]
    public void CurrentWxId_未显式设置时取最近保存的账号()
    {
        var vault = CreateVault();
        var key1 = "48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f";
        vault.Save(new WeChatAccountInfo { WxId = "wxid_a", DbKey = key1 });
        vault.Save(new WeChatAccountInfo { WxId = "wxid_b", DbKey = key1 });

        Assert.Equal("wxid_b", vault.CurrentWxId);

        vault.CurrentWxId = "wxid_a";
        Assert.Equal("wxid_a", vault.CurrentWxId);
    }

    [Fact]
    public void Delete_移除账号与当前指针()
    {
        var vault = CreateVault();
        vault.SaveManual("wxid_t1", "48b985c8a3a54b0f9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f", null);
        vault.Delete("wxid_t1");

        Assert.Null(vault.CurrentWxId);
        Assert.Empty(vault.ListWxIds());
        Assert.Null(vault.Get("wxid_t1"));
    }
}
