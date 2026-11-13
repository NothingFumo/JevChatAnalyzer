namespace JevChatAnalyzer.Core.Net;

/// <summary>
///     模型目录约定（模型放「文档」下便于用户查找与清理）：
///     Documents\JevChatAnalyzer\models\{sensevoice|laya-multilingual}。
///     旧版默认在 %LOCALAPPDATA%，首次访问时自动迁移已下载文件。
/// </summary>
public static class ModelDirs
{
    public static string DocumentsRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "JevChatAnalyzer", "models");
    }

    public static string LegacyRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "models");
    }

    public static string SenseVoice()
    {
        return Path.Combine(DocumentsRoot(), "sensevoice");
    }

    public static string LayaMultilingual()
    {
        return Path.Combine(DocumentsRoot(), "laya-multilingual");
    }

    /// <summary>把旧目录下已下载的文件搬到新目录（新目录已齐时不动作）。返回新目录。</summary>
    public static string Migrate(string newDir, string legacyDir, params string[] fileNames)
    {
        var missing = fileNames.Where(f => !File.Exists(Path.Combine(newDir, f))).ToList();
        if (missing.Count == 0) return newDir;
        foreach (var f in missing)
        {
            var old = Path.Combine(legacyDir, f);
            if (!File.Exists(old)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(newDir, f))!);
                File.Move(old, Path.Combine(newDir, f), false);
            }
            catch (IOException)
            {
                // 迁移失败不影响下载路径（大不了重新下载）
            }
        }

        return newDir;
    }
}
