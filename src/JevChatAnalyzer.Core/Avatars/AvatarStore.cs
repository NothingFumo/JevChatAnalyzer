using System.Security.Cryptography;
using System.Text;
using JevChatAnalyzer.Core.Db;

namespace JevChatAnalyzer.Core.Avatars;

/// <summary>
///     会话头像：微信 4.x 把联系人/群头像缓存在
///     <c>&lt;账号目录&gt;/db_storage/head_image/head_image.db</c> 的
///     <c>head_image(username, md5, image_buffer, update_time)</c> 表里（BLOB 为 JPEG/PNG）。
///     这里只读该库，把 BLOB 落成应用自己目录下的图片文件，供 <c>BitmapImage</c> 直接加载。
///     微信数据全程只读；缓存写在 <c>%LOCALAPPDATA%\JevChatAnalyzer\avatars</c>。
/// </summary>
public sealed class AvatarStore : IDisposable
{
    private const int MaxImageBytes = 512 * 1024;
    private readonly string _cacheDir;

    private readonly SqlCipherNative? _db;
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
    private bool _disposed;

    public AvatarStore(string accountDir, string dbKeyHex, string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "avatars");
        Directory.CreateDirectory(_cacheDir);
        var dbPath = Path.Combine(accountDir, "db_storage", "head_image", "head_image.db");
        if (File.Exists(dbPath))
            try
            {
                _db = SqlCipherNative.OpenReadOnly(dbPath, dbKeyHex);
            }
            catch (SqliteNativeException)
            {
                // 头像库不可用不影响主流程：调用方回退到首字色块
                _db = null;
            }
    }

    public bool IsAvailable => _db is not null;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db?.Dispose();
            _resolved.Clear();
        }
    }

    /// <summary>返回该会话头像的本地文件路径；没有缓存头像时返回 null。</summary>
    public string? GetAvatarPath(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            if (_resolved.TryGetValue(sessionId, out var cached)) return cached;
            var path = Resolve(sessionId);
            _resolved[sessionId] = path;
            return path;
        }
    }

    private string? Resolve(string sessionId)
    {
        if (_db is null) return null;
        try
        {
            var rows = _db.Query($"SELECT image_buffer FROM head_image WHERE username = '{Esc(sessionId)}' LIMIT 1");
            if (rows.Count == 0) return null;
            var bytes = rows[0].Blob("image_buffer");
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
            var ext = DetectExtension(bytes);
            if (ext is null) return null;
            var name = $"{HashName(sessionId)}.{ext}";
            var path = Path.Combine(_cacheDir, name);
            // 已经落盘且大小一致就复用，避免每次启动都重写 200 个小文件
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != bytes.Length) File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception e) when (e is SqliteNativeException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? DetectExtension(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "jpg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E &&
            bytes[3] == 0x47) return "png";
        return null;
    }

    private static string HashName(string sessionId)
    {
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(sessionId)));
    }

    private static string Esc(string s)
    {
        return s.Replace("'", "''");
    }
}
