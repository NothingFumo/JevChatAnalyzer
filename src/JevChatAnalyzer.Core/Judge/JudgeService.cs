using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Judge;

/// <summary>判读服务编排：后端选择、熔断、上下文组装（含引用并入）</summary>
public sealed class JudgeService
{
    private readonly Dictionary<string, IJudgeBackend> _backends = [];
    private readonly Dictionary<string, CircuitBreaker> _breakers = [];
    private readonly Func<string?> _backendSelector;

    /// <summary>上下文轮数（待分析消息之前取最近 N 轮）。</summary>
    public int ContextTurns { get; set; } = 20;

    public JudgeService(Func<string?>? backendSelector = null)
    {
        _backendSelector = backendSelector ?? (() => _backends.Keys.FirstOrDefault());
    }

    /// <summary>注册后端（名字 → 后端）。同名覆盖。</summary>
    public void Register(IJudgeBackend backend)
    {
        _backends[backend.Name] = backend;
        _breakers[backend.Name] = new CircuitBreaker();
    }

    public IReadOnlyList<string> BackendNames => [.. _backends.Keys];

    /// <summary>从 ChatStore 消息组装判读请求（焦点消息 + 最近上下文 + 引用并入）。</summary>
    public static JudgeRequest BuildRequest(IReadOnlyList<ChatMessage> sessionMessages, ChatMessage focus,
        string sessionTitle, bool isGroup)
    {
        var focusIdx = -1;
        for (var i = sessionMessages.Count - 1; i >= 0; i--)
        {
            if (sessionMessages[i].Location == focus.Location)
            {
                focusIdx = i;
                break;
            }
        }
        var before = focusIdx > 0 ? sessionMessages.Take(focusIdx).ToList() : [];
        // 待分析消息若是我的发言，仍判读（自检场景）；上下文含对方最后一条
        var turns = before
            .Where(m => m.DisplayText.Length > 0)
            .Select(m => new JudgeTurn(m.SenderDisplayName, m.DisplayText, m.IsMine))
            .ToList();
        return new JudgeRequest
        {
            SessionTitle = sessionTitle,
            IsGroup = isGroup,
            Turns = turns,
            Focus = new JudgeTurn(focus.SenderDisplayName, focus.DisplayText, focus.IsMine),
            FocusQuote = focus.Quote is { } q ? $"{q.FromDisplayName}：{q.Content}" : null,
        };
    }

    /// <summary>执行判读：熔断快速失败 → 选定后端 → 失败降级到任一可用后端。</summary>
    public async Task<JudgeVerdict> JudgeAsync(JudgeRequest request, string? preferredBackend = null, CancellationToken ct = default)
    {
        var candidates = OrderedCandidates(preferredBackend).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("没有可用的判读后端。请在设置中配置云端 API 或部署本地 laya。");
        }
        Exception? lastError = null;
        foreach (var (name, backend) in candidates)
        {
            var breaker = _breakers[name];
            if (breaker.IsOpen)
            {
                lastError ??= new InvalidOperationException(
                    $"{name} 熔断中（{breaker.RemainingCooldownSeconds}s 后重试）。");
                continue;
            }
            try
            {
                var verdict = await backend.JudgeAsync(request, ct);
                breaker.RecordSuccess();
                return verdict;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                breaker.RecordFailure();
                lastError = e;
            }
        }
        throw new JudgeException("全部判读后端失败。", lastError);
    }

    private IEnumerable<(string, IJudgeBackend)> OrderedCandidates(string? preferred)
    {
        var preferredName = preferred ?? _backendSelector();
        if (preferredName is not null && _backends.TryGetValue(preferredName, out var p))
        {
            yield return (preferredName, p);
        }
        foreach (var (name, backend) in _backends)
        {
            if (name != preferredName)
            {
                yield return (name, backend);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var backend in _backends.Values)
        {
            await backend.DisposeAsync();
        }
        _backends.Clear();
    }
}

/// <summary>判读失败（全部后端不可用或全部出错）。</summary>
public sealed class JudgeException : Exception
{
    public JudgeException(string message, Exception? inner = null) : base(message, inner) { }
}
