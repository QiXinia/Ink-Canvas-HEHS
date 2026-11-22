namespace InkCanvas.Features.Ppt;

/// <summary>
/// Serializes navigation requests and suppresses the loopback event that the host receives
/// after it itself moved the slide show. All members are safe to call off the UI thread.
/// </summary>
public sealed class NavigationGuard
{
    private readonly object _sync = new();
    private readonly int _minIntervalMs;
    private readonly int _loopbackSuppressMs;
    private bool _held;
    private long _lastEnterMs = long.MinValue;
    private long _lastExitMs = long.MinValue;
    private long _suppressUntilMs = long.MinValue;

    public NavigationGuard(int minIntervalMs = 180, int loopbackSuppressMs = 350)
    {
        _minIntervalMs = Math.Max(0, minIntervalMs);
        _loopbackSuppressMs = Math.Max(0, loopbackSuppressMs);
    }

    /// <summary>Timestamp supplied to the last <see cref="Exit"/>; diagnostics only.</summary>
    public long LastExitMs
    {
        get { lock (_sync) return _lastExitMs; }
    }

    public bool TryEnter(long nowMs)
    {
        return TryEnter(nowMs, out _);
    }

    /// <summary>
    /// Same contract as <see cref="TryEnter(long)"/>, but also reports how long the caller has to
    /// wait before a retry can succeed. Zero means the guard is currently held, so no retry is
    /// meaningful. Callers that want a debounced request delivered instead of dropped use this
    /// value to schedule exactly one retry.
    /// </summary>
    public bool TryEnter(long nowMs, out long retryAfterMs)
    {
        lock (_sync)
        {
            retryAfterMs = 0;
            if (_held) return false;
            if (_lastEnterMs != long.MinValue)
            {
                long remaining = _minIntervalMs - (nowMs - _lastEnterMs);
                if (remaining > 0) { retryAfterMs = remaining; return false; }
            }
            _held = true;
            _lastEnterMs = nowMs;
            return true;
        }
    }

    public void Exit(long nowMs)
    {
        lock (_sync)
        {
            _held = false;
            _lastExitMs = nowMs;
        }
    }

    public void SuppressExternal(long nowMs)
    {
        lock (_sync)
        {
            _suppressUntilMs = nowMs + _loopbackSuppressMs;
        }
    }

    public bool IsSuppressed(long nowMs)
    {
        lock (_sync)
        {
            return nowMs <= _suppressUntilMs;
        }
    }
}