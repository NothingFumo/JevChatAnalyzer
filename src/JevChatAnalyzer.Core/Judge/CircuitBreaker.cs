namespace JevChatAnalyzer.Core.Judge;

/// <summary>
/// 熔断器：连续 failThreshold 次失败后断开 coolDown，期间调用直接快速失败；
/// 冷却后半开试探一次，成功即闭合。借鉴 OpenClaw PR #246 的工程模式。
/// </summary>
public sealed class CircuitBreaker(int failThreshold = 3, TimeSpan? coolDown = null)
{
    private readonly object _lock = new();
    private readonly TimeSpan _coolDown = coolDown ?? TimeSpan.FromSeconds(30);
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_consecutiveFailures < failThreshold)
                {
                    return false;
                }
                if (DateTimeOffset.UtcNow - _openedAt < _coolDown)
                {
                    return true;
                }
                // 冷却结束：半开（放行一次试探；再失败会重新计时）
                _consecutiveFailures = failThreshold - 1;
                return false;
            }
        }
    }

    public int RemainingCooldownSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_consecutiveFailures < failThreshold)
                {
                    return 0;
                }
                var left = _coolDown - (DateTimeOffset.UtcNow - _openedAt);
                return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : 0;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            if (_consecutiveFailures == 0)
            {
                _openedAt = DateTimeOffset.UtcNow;
            }
            _consecutiveFailures++;
        }
    }
}
