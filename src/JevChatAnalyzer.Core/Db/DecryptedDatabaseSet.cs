using JevChatAnalyzer.Core.Keys;

namespace JevChatAnalyzer.Core.Db;

/// <summary>
/// 一次会话中解密出来的数据库集合：把账号目录下的消息分片、联系人、会话库
/// 解密到独立临时目录（原始库只读），Dispose 时整目录删除。
/// </summary>
public sealed class DecryptedDatabaseSet : IDisposable
{
    public string TempDir { get; }

    /// <summary>解密后的消息分片明文库，按分片序号排序。</summary>
    public IReadOnlyList<string> MessageDbPaths { get; }

    public string? ContactDbPath { get; }
    public string? SessionDbPath { get; }

    private DecryptedDatabaseSet(string tempDir, List<string> messageDbPaths, string? contactDbPath, string? sessionDbPath)
    {
        TempDir = tempDir;
        MessageDbPaths = messageDbPaths;
        ContactDbPath = contactDbPath;
        SessionDbPath = sessionDbPath;
    }

    /// <summary>解密一个账号的全部基础库。dbKey 为 64 位 hex。</summary>
    public static DecryptedDatabaseSet Open(string accountDir, string dbKeyHex, Action<string>? log = null)
    {
        if (!Directory.Exists(accountDir))
        {
            throw new DirectoryNotFoundException($"微信数据目录不存在：{accountDir}");
        }
        var tempDir = Path.Combine(Path.GetTempPath(), "JevChatAnalyzer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var messageDbPaths = new List<string>();
            foreach (var shard in WeChatDataLocator.ListMessageDbs(accountDir))
            {
                var name = Path.GetFileName(shard);
                log?.Invoke($"解密 {name} …");
                var output = Path.Combine(tempDir, name);
                SqlCipherDecryptor.DecryptToFile(shard, dbKeyHex, output);
                messageDbPaths.Add(output);
            }
            if (messageDbPaths.Count == 0)
            {
                throw new SqlCipherException($"在 {accountDir} 下未找到 message_*.db 消息分片库。");
            }

            // 首个分片做一次实际打开校验，密钥错误在这里就报出来
            SanityCheck(messageDbPaths[0]);

            string? contactDb = null;
            var contactSrc = WeChatDataLocator.ContactDbPath(accountDir);
            if (File.Exists(contactSrc))
            {
                contactDb = Path.Combine(tempDir, "contact.db");
                log?.Invoke("解密 contact.db …");
                SqlCipherDecryptor.DecryptToFile(contactSrc, dbKeyHex, contactDb, mergeWal: false);
            }

            string? sessionDb = null;
            var sessionSrc = WeChatDataLocator.SessionDbPath(accountDir);
            if (File.Exists(sessionSrc))
            {
                sessionDb = Path.Combine(tempDir, "session.db");
                log?.Invoke("解密 session.db …");
                SqlCipherDecryptor.DecryptToFile(sessionSrc, dbKeyHex, sessionDb, mergeWal: false);
            }

            return new DecryptedDatabaseSet(tempDir, messageDbPaths, contactDb, sessionDb);
        }
        catch
        {
            TryDeleteDir(tempDir);
            throw;
        }
    }

    private static void SanityCheck(string decryptedDbPath)
    {
        try
        {
            using var db = OpenReadOnly(decryptedDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master";
            cmd.ExecuteScalar();
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            throw new SqlCipherException($"解密副本无法按 SQLite 打开（{e.Message}）：密钥大概率不正确。");
        }
    }

    public static Microsoft.Data.Sqlite.SqliteConnection OpenReadOnly(string path)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
        };
        var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        TryDeleteDir(TempDir);
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删除失败不致命；下次启动按目录名前缀清理
        }
    }
}
