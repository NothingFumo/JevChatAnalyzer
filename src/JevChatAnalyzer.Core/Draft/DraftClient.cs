using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Draft;

/// <summary>起草配置：OpenAI 兼容端点（DeepSeek/OpenAI/Ollama 等）。</summary>
public sealed class DraftOptions
{
    /// <summary>chat/completions 完整地址，如 https://api.deepseek.com/chat/completions。</summary>
    public required Uri Endpoint { get; init; }

    public string? ApiKey { get; init; }

    public required string Model { get; init; }

    public int CandidateCount { get; init; } = 3;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>一条候选回复。</summary>
public sealed record DraftSuggestion(int Index, string Text, string Tone);

/// <summary>起草失败。</summary>
public sealed class DraftException : Exception
{
    public DraftException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 候选回复起草（可选功能，默认关闭）：判读结论折成中文小抄，
/// 交给 OpenAI 兼容 chat/completions 生成 N 条候选（三段式）。
/// </summary>
public sealed class DraftClient
{
    private readonly HttpClient _http;
    private readonly DraftOptions _options;
    private readonly bool _ownsHttp;

    public DraftClient(DraftOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    /// <summary>基于判读结论与最近对话起草候选回复。</summary>
    public async Task<IReadOnlyList<DraftSuggestion>> DraftAsync(JudgeRequest request, JudgeVerdict verdict, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(request, verdict, _options.CandidateCount);
        var content = await CompleteAsync(prompt, ct);
        return ParseCandidates(content, _options.CandidateCount);
    }

    /// <summary>发起一次 chat/completions 并返回首条文本（起草与盲起草共用）。</summary>
    private async Task<string> CompleteAsync(string prompt, CancellationToken ct)
    {
        var body = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = "你是聊天回复助手。根据对话上下文，写出可直接发送的回复。只输出回复本身，不要解释、不要引号。" },
                new { role = "user", content = prompt },
            },
            temperature = 0.8,
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(_options.Endpoint, body, cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new DraftException($"起草请求失败：{e.Message}", e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                throw new DraftException($"起草接口返回 {(int)response.StatusCode}：{Truncate(text)}");
            }
            ChatCompletionResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
            }
            catch (JsonException e)
            {
                throw new DraftException("起草接口返回的不是有效 JSON。", e);
            }
            var content = payload?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new DraftException("起草接口返回空内容。");
            }
            return content;
        }
    }

    /// <summary>
    /// 盲起草（判读后端全挂时的兜底）：
    /// 不带判读小抄，直接让起草模型按上下文给出候选并附一句话对话判断。
    /// 返回 (候选列表, 判断结论)。
    /// </summary>
    public async Task<(IReadOnlyList<DraftSuggestion> Suggestions, string Summary)> BlindDraftAsync(
        JudgeRequest request, CancellationToken ct = default)
    {
        var prompt = BuildBlindPrompt(request, _options.CandidateCount);
        var content = await CompleteAsync(prompt, ct);
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var suggestions = ParseCandidates(content, _options.CandidateCount);
        var summary = lines.FirstOrDefault(l => l.StartsWith("判断：") || l.StartsWith("判断:"))?.Trim()
                      ?? "盲起草（判读后端不可用，未经判读校准）";
        return (suggestions, summary);
    }

    /// <summary>盲起草提示词：只有上下文与输出格式要求（无判读小抄）。</summary>
    internal static string BuildBlindPrompt(JudgeRequest request, int candidateCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【会话】{(request.IsGroup ? $"群聊「{request.SessionTitle}」" : $"与「{request.SessionTitle}」的单聊")}");
        sb.AppendLine("【最近对话】");
        foreach (var turn in request.Turns.TakeLast(12))
        {
            sb.AppendLine($"{turn.Speaker}: {turn.Text}");
        }
        sb.AppendLine("【要回复的消息】");
        sb.AppendLine($"{request.Focus.Speaker}: {request.Focus.Text}");
        sb.AppendLine($"【要求】判断对方意图并给出 {candidateCount} 条候选回复。" +
                      "每条候选一行，以「1.」「2.」「3.」开头；最后一行以「判断：」开头给一句话结论（对方意图与建议动作）。");
        return sb.ToString();
    }

    /// <summary>把判读小抄 + 上下文折成起草提示词。</summary>
    internal static string BuildPrompt(JudgeRequest request, JudgeVerdict verdict, int candidateCount = 3)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【会话】{(request.IsGroup ? $"群聊「{request.SessionTitle}」" : $"与「{request.SessionTitle}」的单聊")}");
        sb.AppendLine("【最近对话】");
        foreach (var turn in request.Turns.TakeLast(12))
        {
            sb.AppendLine($"{turn.Speaker}: {turn.Text}");
        }
        sb.AppendLine("【要回复的消息】");
        sb.AppendLine($"{request.Focus.Speaker}: {request.Focus.Text}");
        if (!string.IsNullOrEmpty(request.FocusQuote))
        {
            sb.AppendLine($"（引用了：{request.FocusQuote}）");
        }
        sb.AppendLine("【判读结论】");
        foreach (var (id, answer) in verdict.Answers)
        {
            sb.AppendLine($"- {id}: {answer}");
        }
        if (!string.IsNullOrEmpty(verdict.Summary))
        {
            sb.AppendLine($"- 摘要: {verdict.Summary}");
        }
        sb.AppendLine($"【要求】给出 {candidateCount} 条候选回复，每条一行，以「1.」「2.」「3.」开头，风格递进：稳妥 / 自然 / 主动。");
        return sb.ToString();
    }

    /// <summary>解析「1. xxx」行式候选；没有编号时整段作为单条。</summary>
    internal static IReadOnlyList<DraftSuggestion> ParseCandidates(string content, int max)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<DraftSuggestion>();
        var tones = new[] { "稳妥", "自然", "主动", "补充" };
        var idx = 0;
        foreach (var line in lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)[.、)．]\s*(.+)$");
            if (m.Success)
            {
                idx++;
                result.Add(new DraftSuggestion(idx, m.Groups[2].Value.Trim(), tones[Math.Min(idx - 1, tones.Length - 1)]));
                if (result.Count >= max)
                {
                    break;
                }
            }
        }
        if (result.Count == 0)
        {
            result.Add(new DraftSuggestion(1, content.Trim(), "建议"));
        }
        return result;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessageBody? Message);

    private sealed record ChatMessageBody(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);
}
