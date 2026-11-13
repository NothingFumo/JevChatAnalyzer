using System.Security.Cryptography;
using System.Text;
using JevChatAnalyzer.Core.Db;
using JevChatAnalyzer.Core.Messages;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class ChatStoreTests
{
    [Fact]
    public void DecodeTextColumn_TEXT存储直接返回文本()
    {
        // 真实库里群聊正文常见 TEXT 存储；只按 BLOB 读会解成空串（正文/引用块丢失）
        var row = new SqlCipherNative.Row(new Dictionary<string, object?>
        {
            ["message_content"] = "今晚吃饭吗"
        });

        Assert.Equal("今晚吃饭吗", ChatStore.DecodeTextColumn(row, "message_content"));
    }

    [Fact]
    public void DecodeTextColumn_UTF8_BLOB解码为文本()
    {
        var row = new SqlCipherNative.Row(new Dictionary<string, object?>
        {
            ["message_content"] = Encoding.UTF8.GetBytes("群里说个事")
        });

        Assert.Equal("群里说个事", ChatStore.DecodeTextColumn(row, "message_content"));
    }

    [Fact]
    public void DecodeTextColumn_空值返回空串()
    {
        var row = new SqlCipherNative.Row(new Dictionary<string, object?> { ["message_content"] = null });
        Assert.Equal("", ChatStore.DecodeTextColumn(row, "message_content"));

        var missing = new SqlCipherNative.Row(new Dictionary<string, object?>());
        Assert.Equal("", ChatStore.DecodeTextColumn(missing, "message_content"));
    }

    [Fact]
    public void ListSessions_统计排除注解气泡()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-store-" + Guid.NewGuid().ToString("N"));
        var messageDir = Path.Combine(dir, "db_storage", "message");
        Directory.CreateDirectory(messageDir);
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var dbPath = Path.Combine(messageDir, "message_0.db");
        File.WriteAllBytes(dbPath, []);
        try
        {
            using (var db = SqlCipherNative.OpenReadWrite(dbPath, key))
            {
                db.Execute("""
                           CREATE TABLE "Msg_abc" (local_id INTEGER, sort_seq INTEGER, source TEXT, message_content TEXT)
                           """);
                db.Execute("INSERT INTO Msg_abc VALUES (1, 100, '', '正常消息')");
                db.Execute($"INSERT INTO Msg_abc VALUES (2, 100, '{ChatStore.AnnotationBubbleSource}', '<msg/>')");
            }

            using var store = ChatStore.Open(dir, "wxid_me", key);
            var session = Assert.Single(store.ListSessions());
            Assert.Equal(1, session.MessageCount); // 注解气泡不计入会话消息数
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
