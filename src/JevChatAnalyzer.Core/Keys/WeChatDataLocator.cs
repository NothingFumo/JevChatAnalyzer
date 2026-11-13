namespace JevChatAnalyzer.Core.Keys;

/// <summary>微信 4.x 数据目录（xwechat_files）的定位与账号枚举。</summary>
public sealed class WeChatDataLocator
{
    /// <summary>候选根目录：默认在「文档」下，微信 4.x 为 xwechat_files。</summary>
    public IReadOnlyList<string> CandidateRoots
    {
        get
        {
            List<string> roots = [];
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents)) roots.Add(Path.Combine(documents, "xwechat_files"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents",
                "xwechat_files"));
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>找到第一个存在的 xwechat_files 根目录。</summary>
    public string? FindRoot()
    {
        return CandidateRoots.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    ///     枚举根目录下的账号目录。判定依据：目录内存在 db_storage 文件夹。
    ///     返回 (账号目录全路径, wxid)。
    /// </summary>
    public IReadOnlyList<(string Dir, string WxId)> ListAccounts(string? root = null)
    {
        var actualRoot = root ?? FindRoot();
        if (actualRoot is null || !Directory.Exists(actualRoot)) return [];
        List<(string, string)> result = [];
        foreach (var dir in Directory.EnumerateDirectories(actualRoot))
        {
            if (!Directory.Exists(Path.Combine(dir, "db_storage"))) continue;
            result.Add((dir, CleanWxId(Path.GetFileName(dir))));
        }

        return result;
    }

    /// <summary>校验给定路径（数据目录或 wxid）并返回账号目录；wxid 会在根目录下反查。</summary>
    public string? ResolveAccountDir(string dataPathOrWxId)
    {
        if (Directory.Exists(Path.Combine(dataPathOrWxId, "db_storage"))) return dataPathOrWxId;
        if (Directory.Exists(Path.Combine(dataPathOrWxId, "db_storage", "message"))) return dataPathOrWxId;
        var root = FindRoot();
        if (root is null) return null;
        foreach (var (dir, wxId) in ListAccounts(root))
            if (wxId == dataPathOrWxId)
                return dir;

        return null;
    }

    /// <summary>账号目录名形如 wxid_xxxx_abcd（末尾 _4 位随机数），还原为 wxid_xxxx；无后缀时原样返回。</summary>
    public static string CleanWxId(string accountDirName)
    {
        var idx = accountDirName.LastIndexOf('_');
        if (idx > 0 && accountDirName.Length - idx - 1 == 4) return accountDirName[..idx];
        return accountDirName;
    }

    /// <summary>消息库所在目录：&lt;account&gt;\db_storage\message。</summary>
    public static string MessageDbDir(string accountDir)
    {
        return Path.Combine(accountDir, "db_storage", "message");
    }

    public static string ContactDbPath(string accountDir)
    {
        return Path.Combine(accountDir, "db_storage", "Contact", "contact.db");
    }

    public static string SessionDbPath(string accountDir)
    {
        return Path.Combine(accountDir, "db_storage", "session", "session.db");
    }

    /// <summary>列出某账号下的全部消息分片库（message_&lt;数字&gt;.db，排除 message_fts 等辅助库），按分片序号排序。</summary>
    public static IReadOnlyList<string> ListMessageDbs(string accountDir)
    {
        var dir = MessageDbDir(accountDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "message_*.db")
            .Where(f => ShardNumber(Path.GetFileName(f)) is not null)
            .OrderBy(f => ShardNumber(Path.GetFileName(f))!.Value)
            .ToList();
    }

    private static int? ShardNumber(string fileName)
    {
        if (!fileName.StartsWith("message_", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            return null;
        var middle = fileName["message_".Length..^".db".Length];
        return int.TryParse(middle, out var n) ? n : null;
    }
}
