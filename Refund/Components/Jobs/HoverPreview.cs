namespace Refund.Components.Jobs;

/// <summary>
/// Open/close state of a hover or focus preview. Hovering opens the preview after a short delay
/// that leaving cancels; focusing opens it immediately. Every request supersedes the previous one,
/// so a delayed open that is still pending can never win over a later leave, re-enter or close.
/// Meant to be driven from a component's render dispatcher, which serializes all calls.
/// </summary>
/// <typeparam name="T">Type of the previewed item</typeparam>
public sealed class HoverPreview<T> : IDisposable where T : class
{
    /// <summary>
    /// How long the pointer has to rest on an item before its preview opens.
    /// </summary>
    public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(200);

    private readonly Func<Task> _changed;
    private readonly Func<T, bool> _canShow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource _pending;
    private bool _disposed;

    /// <param name="changed">Invoked after the preview opened or closed through a request, e.g. to re-render</param>
    /// <param name="canShow">Checked when the preview would open; false keeps it closed (e.g. the item was deleted)</param>
    /// <param name="delay">Replaces <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in tests</param>
    public HoverPreview(Func<Task> changed,
                        Func<T, bool> canShow = null,
                        Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        _changed = changed ?? (() => Task.CompletedTask);
        _canShow = canShow ?? (_ => true);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    /// <summary>
    /// The item whose preview is open, or null while closed. Render heavy preview content only
    /// when this is set.
    /// </summary>
    public T Current { get; private set; }

    /// <summary>
    /// Requests the preview for <paramref name="target"/>, immediately (focus) or after
    /// <see cref="HoverDelay"/> (hover). An already open preview stays open until the new one opens.
    /// </summary>
    public async Task ShowAsync(T target, bool immediate)
    {
        CancelPending();
        if (_disposed || target == null || target == Current)
            return;

        if (!immediate)
        {
            var pending = new CancellationTokenSource();
            _pending = pending;
            try
            {
                await _delay(HoverDelay, pending.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Superseded by a later request while waiting
            if (pending != _pending)
                return;
            _pending = null;
            pending.Dispose();
        }

        if (_disposed || target == Current || !_canShow(target))
            return;

        Current = target;
        await _changed();
    }

    /// <summary>
    /// Cancels a pending open and closes the preview.
    /// </summary>
    public Task HideAsync()
    {
        CancelPending();
        if (Current == null)
            return Task.CompletedTask;

        Current = null;
        return _disposed ? Task.CompletedTask : _changed();
    }

    /// <summary>
    /// Cancels a pending open and closes the preview without notifying, for use while the owner is
    /// already rendering (parameter changes) or going away.
    /// </summary>
    public void Reset()
    {
        CancelPending();
        Current = null;
    }

    private void CancelPending()
    {
        if (_pending == null)
            return;

        _pending.Cancel();
        _pending.Dispose();
        _pending = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Reset();
    }
}
