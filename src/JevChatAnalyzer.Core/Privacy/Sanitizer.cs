using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JevChatAnalyzer.Core.Privacy;

/// <summary>
/// 出站脱敏：昵称匿名化、号码/URL/证件号本地掩蔽。送云端判读默认开启，
/// 本地后端不经过本管线（数据不出机）。规则参考 参考工具 的 privacy.py。
/// </summary>
public sealed partial class Sanitizer
{
    /// <summary>群成员依次映射为 成员A/成员B/…；单聊对方映射为「对方」。</summary>
    private readonly Dictionary<string, string> _alias = new(StringComparer.Ordinal);
    private int _nextAlias;

    [GeneratedRegex(@"1[3-9]\d{9}")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:[0-2]\d|3[01])\d{3}[\dXx]")]
    private static partial Regex IdCardRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b\d{15,19}\b")]
    private static partial Regex CardLikeRegex();

    /// <summary>重置别名映射（每个判读请求一套，避免跨请求关联）。</summary>
    public void Reset()
    {
        _alias.Clear();
        _nextAlias = 0;
    }

    /// <summary>说话人匿名化：我 → 我；其他人按首次出现顺序 成员A/成员B/…。</summary>
    public string AliasOf(string speaker)
    {
        if (speaker == "我")
        {
            return speaker;
        }
        if (!_alias.TryGetValue(speaker, out var alias))
        {
            alias = speaker.Length == 0 ? "对方" : $"成员{(char)('A' + _nextAlias++)}";
            _alias[speaker] = alias;
        }
        return alias;
    }

    /// <summary>文本脱敏：证件号/手机号/URL 参数/长号码掩蔽。说话人字段请用 <see cref="AliasOf"/>。</summary>
    public string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        var result = IdCardRegex().Replace(text, "[证件号]");
        result = PhoneRegex().Replace(result, "[手机号]");
        result = UrlRegex().Replace(result, m =>
        {
            // 保留域名，参数全掩蔽
            var idx = m.Value.IndexOf('/', "https://".Length);
            return idx > 0 ? m.Value[..idx] + "/…" : "[链接]";
        });
        result = CardLikeRegex().Replace(result, "[号码]");
        return result;
    }

    /// <summary>
    /// 递归脱敏 state JSON：speaker 字段匿名化，字符串字段做文本脱敏。
    /// 对嵌套结构原位变异（不复_assign已有父级的节点）。
    /// </summary>
    public static JsonNode? SanitizeNode(JsonNode? node, Sanitizer? instance = null)
    {
        instance ??= new Sanitizer();
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue("speaker", out var sp) &&
                    sp is JsonValue spv && spv.TryGetValue<string>(out var speaker))
                {
                    obj["speaker"] = JsonValue.Create(instance.AliasOf(speaker));
                }
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    var value = obj[key];
                    switch (value)
                    {
                        case JsonObject or JsonArray:
                            SanitizeNode(value, instance); // 原位处理，不重新赋值
                            break;
                        case JsonValue v when v.TryGetValue<string>(out var s):
                            obj[key] = JsonValue.Create(instance.SanitizeText(s));
                            break;
                    }
                }
                return obj;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    switch (arr[i])
                    {
                        case JsonObject or JsonArray:
                            SanitizeNode(arr[i], instance);
                            break;
                        case JsonValue v when v.TryGetValue<string>(out var s):
                            arr[i] = JsonValue.Create(instance.SanitizeText(s));
                            break;
                    }
                }
                return arr;
            }
            default:
                return node;
        }
    }
}
