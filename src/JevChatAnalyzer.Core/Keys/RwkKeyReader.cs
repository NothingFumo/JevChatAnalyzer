using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace JevChatAnalyzer.Core.Keys;

/// <summary>rwk.dll 读取失败。常见原因：微信未运行、未登录、版本未被适配。</summary>
public sealed class KeyReadException : Exception
{
    public KeyReadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
///     通过 密钥读取工程 工程的 rwk.dll（C ABI，UTF-8 JSON 输出）读取已登录微信的密钥。
///     仅做外部只读内存访问。
/// </summary>
public sealed class RwkKeyReader
{
    private const string DllName = "rwk";

    /// <summary>诊断日志：每次读取都记一行，读不到时能看出是「返回空」还是「解析失败」。</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JevChatAnalyzer", "keyread.log");

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rwk_abi_version();

    /// <summary>返回值：&gt;0 = 所需缓冲区字节数（UTF-8 JSON）；&lt;=0 = 读取失败。</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rwk_extract_keys(nint buffer, int capacity);

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>读取当前登录微信的账号密钥信息。扫描耗时数百毫秒到数秒，在线程池上执行。</summary>
    public Task<IReadOnlyList<WeChatAccountInfo>> ReadAsync(CancellationToken ct = default)
    {
        return Task.Run(() => (IReadOnlyList<WeChatAccountInfo>)Read(), ct);
    }

    public IReadOnlyList<WeChatAccountInfo> Read()
    {
        int abi;
        try
        {
            abi = rwk_abi_version();
        }
        catch (DllNotFoundException e)
        {
            throw new KeyReadException("未找到 rwk.dll，请确认应用完整性或将其放置在程序目录下。", e);
        }
        catch (BadImageFormatException e)
        {
            throw new KeyReadException("rwk.dll 架构与当前进程不匹配（需要 x64）。", e);
        }

        if (!Elevation.CanReadWeChatProcesses(out var denied))
        {
            Log($"OpenProcess 被拒绝：{denied} 个 Weixin 进程，elevated={Elevation.IsElevated}");
            throw new KeyReadException(Elevation.IsElevated
                ? $"无法读取微信进程内存（{denied} 个进程都被拒绝）。微信可能启用了更强的保护，或正以更高权限运行。"
                : "微信以管理员权限运行，而本应用是普通权限，读不到它的内存。请点击下方「以管理员身份重启」后重试。");
        }

        int needed;
        try
        {
            needed = rwk_extract_keys(nint.Zero, 0);
        }
        catch (Exception e)
        {
            Log($"abi={abi} enumerate 抛异常：{e.GetType().Name} {e.Message}");
            throw new KeyReadException("调用 rwk.dll 读取密钥失败。请确认微信已运行并登录。", e);
        }

        if (needed <= 0)
        {
            Log($"abi={abi} enumerate 返回 {needed}（无账号/微信未登录/版本未适配）");
            throw new KeyReadException(
                $"rwk.dll 未能提取密钥（返回 {needed}）。常见原因：微信（Weixin.exe）未运行或未登录，" +
                "或当前微信版本未被适配。请先登录微信后重试，或在设置中手动输入密钥。");
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            var written = rwk_extract_keys(buffer, needed);
            if (written <= 0) throw new KeyReadException($"rwk.dll 未能提取密钥（写入阶段返回 {written}）。请确认微信已登录。");
            var bytes = new byte[written];
            Marshal.Copy(buffer, bytes, 0, written);
            var json = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            var accounts = ParsePayload(json);
            // 只记非敏感元数据：json 里含明文 dbKey / 手机号 / 图片密钥，任何情况下都不得落盘
            Log($"abi={abi} needed={needed} written={written} accounts={accounts.Count} " +
                $"wxids={string.Join(',', accounts.Select(a => a.WxId))}");
            if (accounts.Count == 0)
                throw new KeyReadException(
                    "rwk.dll 返回了空账号列表（微信可能未登录或版本未适配）。诊断日志：" + LogPath);
            return accounts;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>解析 rwk_extract_keys 输出的 JSON（字段大小写不确定，做宽松匹配）。</summary>
    internal static IReadOnlyList<WeChatAccountInfo> ParsePayload(string json)
    {
        List<WeChatAccountInfo> result = [];
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new KeyReadException("rwk.dll 返回内容不是有效 JSON，可能为版本不匹配。", e);
        }

        using (doc)
        {
            var version = doc.RootElement.TryGetPropertyInsensitive("weixinVersion", out var v) ? v.GetString() : null;
            if (!doc.RootElement.TryGetPropertyInsensitive("users", out var users) ||
                users.ValueKind != JsonValueKind.Array) return result;
            foreach (var user in users.EnumerateArray())
            {
                var wxId = user.TryGetPropertyInsensitive("wxId", out var wx) ? wx.GetString() : null;
                var dbKey = user.TryGetPropertyInsensitive("dbKey", out var key) ? key.GetString() : null;
                if (string.IsNullOrWhiteSpace(wxId) || string.IsNullOrWhiteSpace(dbKey)) continue;
                var info = new WeChatAccountInfo
                {
                    WxId = wxId.Trim(),
                    DbKey = dbKey.Trim(),
                    NickName = user.TryGetPropertyInsensitive("name", out var n) ? n.GetString() : null,
                    Alias = user.TryGetPropertyInsensitive("number", out var num) ? num.GetString() : null,
                    Phone = user.TryGetPropertyInsensitive("phone", out var p) ? p.GetString() : null,
                    ImageXorKey = user.TryGetPropertyInsensitive("imageXorKey", out var xk) ? xk.GetString() : null,
                    ImageAesKey = user.TryGetPropertyInsensitive("imageAesKey", out var ak) ? ak.GetString() : null,
                    DataPath = user.TryGetPropertyInsensitive("dataPath", out var dp) ? dp.GetString() : null,
                    WeixinVersion = version
                };
                if (info.IsValidDbKey) result.Add(info);
            }
        }

        return result;
    }
}

file static class JsonExtensions
{
    public static bool TryGetPropertyInsensitive(this JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }

        value = default;
        return false;
    }
}
