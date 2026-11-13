namespace JevChatAnalyzer.Core.Messages;

/// <summary>
///     实时监听的会话白名单解析：设置里存一行由换行/逗号/分号（中英文）分隔的会话 id。
///     空串表示「全部活跃会话」，解析结果为空集合。
/// </summary>
public static class SessionWhitelist
{
    private static readonly char[] Separators = ['\r', '\n', ',', ';', '，', '；'];

    public static HashSet<string> Parse(string? raw)
    {
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
    }
}
