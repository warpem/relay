namespace Refund.JobExecution;

/// <summary>
/// Allows cancellation through a captured preparation entry after the preparation has finished.
/// Disposal waits for callers already inside Cancel, without holding a lock during callbacks.
/// </summary>
internal sealed class PreparationCancellation : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _source;
    private int _cancelCalls;
    private bool _disposeRequested;

    public PreparationCancellation(CancellationToken lifetime)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    public CancellationToken Token => _source.Token;

    public void Cancel()
    {
        lock (_sync)
        {
            if (_disposeRequested)
                return;
            _cancelCalls++;
        }

        try
        {
            _source.Cancel();
        }
        finally
        {
            bool dispose;
            lock (_sync)
            {
                _cancelCalls--;
                dispose = _disposeRequested && _cancelCalls == 0;
            }
            if (dispose)
                _source.Dispose();
        }
    }

    public void Dispose()
    {
        bool dispose;
        lock (_sync)
        {
            if (_disposeRequested)
                return;
            _disposeRequested = true;
            dispose = _cancelCalls == 0;
        }
        if (dispose)
            _source.Dispose();
    }
}
