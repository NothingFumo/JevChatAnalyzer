using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace JevChatAnalyzer.Core.Keys;

/// <summary>rwk.dll 读取失败。常见原因：微信未运行、未登录、版本未被适配。</summary>
public sealed class KeyReadException : Exception
{
    public KeyReadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 通过 密钥读取工程 工程的 rwk.dll（C ABI，UTF-8 JSON 输出）读取已登录微信的密钥。
/// 仅做外部只读内存访问
/// </summary>
public sealed class RwkKeyReader
{
    private const string DllName = "rwk";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rwk_abi_version();

    /// <summary>返回值：&gt;0 = 所需缓冲区字节数（UTF-8 JSON）；&lt;=0 = 读取失败。</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rwk_extract_keys(nint buffer, int capacity);

    /// <summary>读取当前登录微信的账号密钥信息。扫描耗时数百毫秒到数秒，在线程池上执行。</summary>
    public Task<IReadOnlyList<WeChatAccountInfo>> ReadAsync(CancellationToken ct = default) =>
        Task.Run(() => (IReadOnlyList<WeChatAccountInfo>)Read(), ct);

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

        int needed;
        try
        {
            needed = rwk_extract_keys(nint.Zero, 0);
        }
        catch (Exception e)
        {
            throw new KeyReadException("调用 rwk.dll 读取密钥失败。请确认微信已运行并登录。", e);
        }
        if (needed <= 0)
        {
            throw new KeyReadException(
                $"rwk.dll 未能提取密钥（返回 {needed}）。常见原因：微信（Weixin.exe）未运行或未登录，" +
                "或当前微信版本未被适配。请先登录微信后重试，或在设置中手动输入密钥。");
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            int written = rwk_extract_keys(buffer, needed);
            if (written <= 0)
            {
                throw new KeyReadException($"rwk.dll 未能提取密钥（写入阶段返回 {written}）。请确认微信已登录。");
            }
            var bytes = new byte[written];
            Marshal.Copy(buffer, bytes, 0, written);
            var json = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return ParsePayload(json);
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
            if (!doc.RootElement.TryGetPropertyInsensitive("users", out var users) || users.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var user in users.EnumerateArray())
            {
                var wxId = user.TryGetPropertyInsensitive("wxId", out var wx) ? wx.GetString() : null;
                var dbKey = user.TryGetPropertyInsensitive("dbKey", out var key) ? key.GetString() : null;
                if (string.IsNullOrWhiteSpace(wxId) || string.IsNullOrWhiteSpace(dbKey))
                {
                    continue;
                }
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
                    WeixinVersion = version,
                };
                if (info.IsValidDbKey)
                {
                    result.Add(info);
                }
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
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
