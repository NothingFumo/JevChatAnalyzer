using JevChatAnalyzer.Core.Analysis;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.App.Services;

/// <summary>消息流状态（供界面状态行展示）。线程池线程上触发。</summary>
public record MonitorStatus(string Message, DateTimeOffset At);

/// <summary>
///     消息流：拥有「轮询 → 判读 → 入队 → 落库 → 对账」的全部排程。
///     单一时钟、单一退避、单一状态事件；内部三段实现（监听 / 管线 / 写回）仍各自独立。
///     只读访问微信库，不注入、不写微信进程。
/// </summary>
public sealed class MessageFlow
{
    /// <summary>落库失败后的退避：60 秒内不再尝试（如 WAL 未合并需用户启动微信合并）。</summary>
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>统一时钟：轮询与落库都按各自到期时间在这条时钟上触发。</summary>
    private static readonly TimeSpan ClockPeriod = TimeSpan.FromSeconds(1);

    /// <summary>各会话已见到的最大 sort_seq（基线）。0 = 尚未建立基线。</summary>
    private readonly Dictionary<string, long> _cursors = [];

    private readonly object _lock = new();
    private readonly AnalysisPipeline _pipeline;

    private readonly AppState _state;

    /// <summary>tick 互斥：同时充当「等在飞 tick 结束」的闸门。</summary>
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private DateTimeOffset _lastPoll;
    private DateTimeOffset _nextWriteAttempt;
    private bool _reconciled;

    private Timer? _timer;

    public MessageFlow(AppState state, AnalysisPipeline pipeline)
    {
        _state = state;
        _pipeline = pipeline;
    }

    public bool IsRunning { get; private set; }

    public event Action<MonitorStatus>? StatusChanged;

    /// <summary>一批待写条目的落库结果（成功条目 / 失败原因）。</summary>
    public event Action<IReadOnlyList<(WriteBackEntry? Entry, string? Error)>>? Wrote;

    /// <summary>启动消息流（未连接账号或两个开关都关时不动）。</summary>
    public void Start()
    {
        if (IsRunning || _state.Account is null) return;
        if (!_state.Settings.MonitorEnabled && !_state.Settings.AutoWriteEnabled) return;
        IsRunning = true;
        _reconciled = false; // 每次连接对账一次：上次可能停在「已记账未落库」
        _lastPoll = DateTimeOffset.MinValue;
        _nextWriteAttempt = DateTimeOffset.MinValue;
        _timer = new Timer(_ => _ = TickAsync(), null, ClockPeriod, ClockPeriod);
        StatusChanged?.Invoke(new MonitorStatus(Describe(), DateTimeOffset.Now));
    }

    /// <summary>按最新设置重启（白名单/开关变化后热生效）。</summary>
    public void Restart()
    {
        Stop();
        _reconciled = false;
        Start();
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
        // 等在飞 tick 结束：它可能正用旧 ChatStore 读库，换会话前必须先排空
        _tickGate.Wait();
        _tickGate.Release();
        StatusChanged?.Invoke(new MonitorStatus("实时监听已停止。", DateTimeOffset.Now));
    }

    private string Describe()
    {
        return
            $"实时监听已启动（间隔 {PollInterval().TotalSeconds:0}s，会话白名单：{WatchedSessions().Count} 个，自动写回：{(_state.Settings.AutoWriteEnabled ? "开" : "关")}）";
    }

    private TimeSpan PollInterval()
    {
        return TimeSpan.FromSeconds(Math.Max(2, _state.Settings.MonitorIntervalSeconds));
    }

    /// <summary>监听的会话白名单（空 = 全部活跃会话）。</summary>
    private HashSet<string> WatchedSessions()
    {
        return SessionWhitelist.Parse(_state.Settings.MonitorSessions);
    }

    private async Task TickAsync()
    {
        if (!_tickGate.Wait(0)) return;
        try
        {
            if (!IsRunning) return;
            if (!_reconciled)
            {
                _reconciled = true;
                ReconcileOnce();
            }

            var now = DateTimeOffset.Now;
            if (_state.Settings.MonitorEnabled && now - _lastPoll >= PollInterval())
            {
                _lastPoll = now;
                await PollAsync();
            }

            FlushWriteBack(now);
        }
        catch (Exception e)
        {
            StatusChanged?.Invoke(new MonitorStatus($"消息流异常（下轮继续）：{e.Message}", DateTimeOffset.Now));
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>启动对账：把停在「待写」的条目与微信库实际状态对齐。</summary>
    private void ReconcileOnce()
    {
        try
        {
            _state.WriteBack?.Reconcile();
        }
        catch
        {
            // 对账失败不阻塞消息流；下次连接会再试
        }
    }

    private void FlushWriteBack(DateTimeOffset now)
    {
        if (!_state.Settings.AutoWriteEnabled || now < _nextWriteAttempt) return;
        if (_state.WriteBack is not { } writeBack || _state.Ledger.PendingCount() == 0) return;
        var results = writeBack.FlushPending();
        if (results.Any(r => r.Error is not null)) _nextWriteAttempt = now.Add(WriteRetryDelay);
        Wrote?.Invoke(results);
    }

    private async Task PollAsync()
    {
        var chat = _state.Account?.Chat;
        if (chat is null || !IsRunning) return;
        var watched = WatchedSessions();
        var sessions = chat.ListSessions(50) // 只监听最近活跃的 50 个会话
            .Where(s => s.Kind is SessionKind.Group or SessionKind.Single)
            .Where(s => _state.Settings.MonitorAll || watched.Contains(s.SessionId))
            .ToList();

        foreach (var session in sessions)
        {
            lock (_lock)
            {
                if (!_cursors.ContainsKey(session.SessionId))
                {
                    // 首次见到该会话：读最新 1 条作为基线，不触发分析
                    var latest = chat.ReadSessionMessages(session.SessionId, 1);
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
                    lock (_lock)
                    {
                        _cursors[session.SessionId] =
                            Math.Max(_cursors.GetValueOrDefault(session.SessionId), max.Value);
                    }

                continue;
            }

            // 立即触发分析（管线内部串行 + 焦点去重）
            var outcome = (await _pipeline.AnalyzeAsync(session, true)).Outcome;
            lock (_lock)
            {
                _cursors[session.SessionId] = focusCandidate.SortSeq;
            }

            if (outcome is not null)
                StatusChanged?.Invoke(new MonitorStatus(
                    $"[{session.DisplayName}] 新消息已分析：{outcome.Verdict.Summary}", DateTimeOffset.Now));
        }
    }
}
