using System.Diagnostics;

namespace Refund.Services.Storage;

/// <summary>
/// Paces all walkers together: a token bucket caps metadata operations (entries) per second, and the cap halves
/// while entries keep taking much longer than usual — the file system is busy with work that matters more — and
/// recovers gradually once they don't.
/// </summary>
/// <remarks>
/// Latencies on a network file system are bimodal (cached entries take microseconds, others hundreds), so "recent" and
/// "usual" are geometric means over a few seconds and ten minutes, and only a slowdown that lasts counts.
/// </remarks>
public sealed class StorageThrottle
{
    public const double DefaultCeiling = 20_000, Floor = 200;
    private const double SlowFactor = 4, RecoveryPerSecond = 0.05, RecentSeconds = 3, UsualSeconds = 600;
    private const double SustainedSeconds = 10, BackoffSeconds = 10, WarmUpSeconds = 30;

    private readonly object _gate = new();
    private readonly Func<long> _now;          // Stopwatch ticks
    private readonly Action<TimeSpan> _sleep;
    private readonly ManualResetEventSlim _resumed = new(true);
    private double _ceiling, _rate, _tokens, _logRecent, _logUsual, _observed;
    private long _lastRefill, _lastBackoff, _lastObserved, _slowSince = -1;
    private bool _started;
    private bool _paused;

    // Throughput over the last few seconds, for progress.
    private long _windowStart, _windowEntries;
    private double _entriesPerSecond;

    public StorageThrottle(double ceiling = DefaultCeiling, Func<long> now = null, Action<TimeSpan> sleep = null)
    {
        _now = now ?? Stopwatch.GetTimestamp;
        _sleep = sleep ?? Thread.Sleep;
        _ceiling = Math.Max(Floor, ceiling);
        _rate = _ceiling;
        _tokens = _ceiling / 10;
        _lastRefill = _windowStart = _now();
        _lastBackoff = long.MinValue / 2;
    }

    /// <summary>The configured maximum, in entries per second.</summary>
    public double Ceiling
    {
        get { lock (_gate) return _ceiling; }
        set { lock (_gate) { _ceiling = Math.Max(Floor, value); _rate = Math.Min(_rate, _ceiling); } }
    }

    /// <summary>The cap in force right now.</summary>
    public double Rate { get { lock (_gate) return _rate; } }

    /// <summary>Whether the cap is well below the ceiling because the file system has been slow.</summary>
    public bool SlowedDown { get { lock (_gate) return _rate < _ceiling * 0.9; } }

    /// <summary>Entries measured per second recently, across all walkers.</summary>
    public double EntriesPerSecond { get { lock (_gate) { Roll(_now()); return _entriesPerSecond; } } }

    /// <summary>Usual and recent time per entry, in microseconds (0 until measured).</summary>
    public (double Usual, double Recent) Latency { get { lock (_gate) return _started ? (Math.Exp(_logUsual), Math.Exp(_logRecent)) : (0, 0); } }

    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate) _paused = value;
            if (value) _resumed.Reset(); else _resumed.Set();
        }
    }

    /// <summary>
    /// Called by a walker after every batch of <paramref name="entries"/>, with the time the batch took (not counting
    /// time spent waiting here). Waits as long as the cap requires; returns early when cancelled.
    /// </summary>
    public void Pace(int entries, TimeSpan batch, CancellationToken cancellation)
    {
        if (entries <= 0) return;
        if (!_resumed.IsSet)
            _resumed.Wait(cancellation);

        TimeSpan wait;
        lock (_gate)
        {
            long now = _now();
            Observe(batch.TotalMicroseconds / entries, now);
            Refill(now);
            _tokens -= entries;
            _windowEntries += entries;
            Roll(now);
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
        if (wait > TimeSpan.Zero && !cancellation.IsCancellationRequested)
            _sleep(wait < TimeSpan.FromSeconds(2) ? wait : TimeSpan.FromSeconds(2));
    }

    private void Observe(double microsPerEntry, long now)
    {
        if (microsPerEntry <= 0) return;
        double sample = Math.Log(microsPerEntry);
        if (!_started)
        {
            _started = true;
            _logRecent = _logUsual = sample;
            _lastObserved = now;
        }
        double dt = Math.Max(0, Seconds(now - _lastObserved));
        _lastObserved = now;
        _observed += dt;
        _logRecent += (sample - _logRecent) * (1 - Math.Exp(-dt / RecentSeconds));
        _logUsual += (sample - _logUsual) * (1 - Math.Exp(-dt / UsualSeconds));

        double seconds = Seconds(now - _lastRefill);
        bool slow = _observed >= WarmUpSeconds && _logRecent - _logUsual > Math.Log(SlowFactor);
        if (!slow)
        {
            _slowSince = -1;
            _rate = Math.Min(_ceiling, _rate + _ceiling * RecoveryPerSecond * seconds);
            return;
        }
        if (_slowSince < 0) _slowSince = now;
        if (Seconds(now - _slowSince) >= SustainedSeconds && Seconds(now - _lastBackoff) >= BackoffSeconds)
        {
            _rate = Math.Max(Floor, _rate / 2);
            _lastBackoff = now;
        }
    }

    private void Refill(long now)
    {
        double seconds = Seconds(now - _lastRefill);
        _lastRefill = now;
        // At most a tenth of a second of burst.
        _tokens = Math.Min(_rate / 10, _tokens + seconds * _rate);
    }

    private void Roll(long now)
    {
        double seconds = Seconds(now - _windowStart);
        if (seconds < 2) return;
        _entriesPerSecond = _windowEntries / seconds;
        _windowEntries = 0;
        _windowStart = now;
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
