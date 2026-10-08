using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.App.Services;

/// <summary>监听状态变化（供 UI 状态行展示）。线程池线程上触发。</summary>
public record MonitorStatus(string Message, DateTimeOffset At);

/// <summary>
/// 实时消息监听（参考 参考工具 的增量只读监听）：
/// 按配置间隔轮询活跃会话的新消息（微信运行中只读查询可并发），
/// 发现「对方新消息」立即触发分析管线（判读→入待写队列），微信退出后自动落库。
/// 只读访问，不注入、不写微信进程。
/// </summary>
public sealed class MessageMonitor : IAsyncDisposable
{
    private readonly AppState _state;
    private readonly AnalysisPipeline _pipeline;
    private System.Threading.Timer? _timer;
    private int _tickBusy;
    /// <summary>各会话已见到的最大 sort_seq（基线）。0 = 尚未建立基线。</summary>
    private readonly Dictionary<string, long> _cursors = [];
    private readonly object _lock = new();

    public event Action<MonitorStatus>? StatusChanged;
    public event Action<AnalysisOutcome>? Analyzed;

    public bool IsRunning { get; private set; }
    public int TickCount { get; private set; }

    public MessageMonitor(AppState state, AnalysisPipeline pipeline)
    {
        _state = state;
        _pipeline = pipeline;
        _pipeline.Analyzed += o => Analyzed?.Invoke(o);
    }

    /// <summary>监听的会话白名单（空/null = 全部活跃会话）。</summary>
    private HashSet<string> WatchedSessions()
    {
        var raw = _state.Settings.MonitorSessions;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return raw.Split(['\r', '\n', ',', ';', '，', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    public void Start()
    {
        if (IsRunning || !_state.Settings.MonitorEnabled)
        {
            return;
        }
        IsRunning = true;
        var interval = Math.Max(2, _state.Settings.MonitorIntervalSeconds) * 1000;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, dueTime: interval, period: interval);
        StatusChanged?.Invoke(new MonitorStatus($"实时监听已启动（间隔 {interval / 1000}s，会话白名单：{WatchedSessions().Count} 个）", DateTimeOffset.Now));
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
        StatusChanged?.Invoke(new MonitorStatus("实时监听已停止。", DateTimeOffset.Now));
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1)
        {
            return;
        }
        try
        {
            var chat = _state.Chat;
            if (chat is null || !_state.Settings.MonitorEnabled || !IsRunning)
            {
                return;
            }
            var watched = WatchedSessions();
            var sessions = chat.ListSessions(50) // 只监听最近活跃的 50 个会话
                .Where(s => s.Kind is SessionKind.Group or SessionKind.Single)
                .Where(s => watched.Count == 0 || watched.Contains(s.SessionId))
                .ToList();

            foreach (var session in sessions)
            {
                lock (_lock)
                {
                    if (!_cursors.ContainsKey(session.SessionId))
                    {
                        // 首次见到该会话：读最新 1 条作为基线，不触发分析
                        var latest = chat.ReadSessionMessages(session.SessionId, limit: 1);
                        _cursors[session.SessionId] = latest.LastOrDefault()?.SortSeq ?? 0;
                        continue;
                    }
                }

                var news = chat.ReadNewMessages(session.SessionId, _cursors[session.SessionId]);
                var focusCandidate = news.LastOrDefault(m => !m.IsMine && m.DisplayText.Length > 0);
                if (focusCandidate is null)
                {
                    // 只更新游标（新消息全是我发的或不可分析）
                    var max = news.LastOrDefault()?.SortSeq;
                    if (max is > 0)
                    {
                        lock (_lock)
                        {
                            _cursors[session.SessionId] = Math.Max(_cursors.GetValueOrDefault(session.SessionId), max.Value);
                        }
                    }
                    continue;
                }

                // 立即触发分析（管线内部串行 + 焦点去重）
                var outcome = await _pipeline.AnalyzeAsync(_state, session, enqueue: true);
                lock (_lock)
                {
                    _cursors[session.SessionId] = focusCandidate.SortSeq;
                }
                if (outcome is not null)
                {
                    StatusChanged?.Invoke(new MonitorStatus(
                        $"[{session.DisplayName}] 新消息已分析：{outcome.Verdict.Summary}", DateTimeOffset.Now));
                }
                TickCount++;
            }
        }
        catch (Exception e)
        {
            StatusChanged?.Invoke(new MonitorStatus($"监听轮询异常（下轮继续）：{e.Message}", DateTimeOffset.Now));
        }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await ValueTask.CompletedTask;
    }
}
