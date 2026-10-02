using Refund.Components.Jobs;

namespace Refund.Tests.Components;

/// <summary>
/// Hover previews open after a delay, focus previews immediately, and any later request must win
/// over a delayed open that is still pending.
/// </summary>
public sealed class HoverPreviewTests
{
    private sealed class Item;

    private readonly Item _a = new();
    private readonly Item _b = new();
    private int _changes;

    /// <summary>
    /// Delays that complete only when a test says so. Completion runs the awaiting continuation
    /// inline, so assertions right after <see cref="Elapse"/> observe its effect.
    /// </summary>
    private sealed class ManualDelays(bool honorCancellation = true)
    {
        public List<TaskCompletionSource> Pending { get; } = new();

        public Task Wait(TimeSpan delay, CancellationToken token)
        {
            Assert.Equal(HoverPreview<Item>.HoverDelay, delay);
            var completion = new TaskCompletionSource();
            if (honorCancellation)
                token.Register(() => completion.TrySetCanceled(token));
            Pending.Add(completion);
            return completion.Task;
        }

        public void Elapse(int index) => Pending[index].TrySetResult();
    }

    private HoverPreview<Item> Create(ManualDelays delays, Func<Item, bool>? canShow = null) =>
        new(() => { _changes++; return Task.CompletedTask; }, canShow, delays.Wait);

    [Fact]
    public async Task HoverOpensOnlyAfterTheDelay()
    {
        var delays = new ManualDelays();
        using var preview = Create(delays);

        var show = preview.ShowAsync(_a, immediate: false);
        Assert.Null(preview.Current);
        Assert.Equal(0, _changes);

        delays.Elapse(0);
        await show;
        Assert.Same(_a, preview.Current);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task LeavingBeforeTheDelayKeepsThePreviewClosed()
    {
        var delays = new ManualDelays();
        using var preview = Create(delays);

        var show = preview.ShowAsync(_a, immediate: false);
        await preview.HideAsync();
        await show;

        Assert.True(show.IsCompletedSuccessfully);
        Assert.Null(preview.Current);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task OnlyTheLatestRequestOpensEvenIfAnEarlierDelayCompletesLate()
    {
        // Delays that ignore cancellation exercise the superseded-request check itself
        var delays = new ManualDelays(honorCancellation: false);
        using var preview = Create(delays);

        var first = preview.ShowAsync(_a, immediate: false);
        await preview.HideAsync();
        var second = preview.ShowAsync(_b, immediate: false);

        delays.Elapse(0);
        await first;
        Assert.Null(preview.Current);

        delays.Elapse(1);
        await second;
        Assert.Same(_b, preview.Current);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task LeaveAfterAnEarlierDelayCompletedLateStillWins()
    {
        var delays = new ManualDelays(honorCancellation: false);
        using var preview = Create(delays);

        var show = preview.ShowAsync(_a, immediate: false);
        await preview.HideAsync();
        delays.Elapse(0);
        await show;

        Assert.Null(preview.Current);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task FocusOpensImmediatelyAndHoveringTheSameItemChangesNothing()
    {
        var delays = new ManualDelays();
        using var preview = Create(delays);

        await preview.ShowAsync(_a, immediate: true);
        Assert.Same(_a, preview.Current);
        Assert.Equal(1, _changes);

        await preview.ShowAsync(_a, immediate: false);
        Assert.Empty(delays.Pending);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task AnOpenPreviewSwitchesWhenAnotherItemsDelayElapses()
    {
        var delays = new ManualDelays();
        using var preview = Create(delays);
        await preview.ShowAsync(_a, immediate: true);

        var show = preview.ShowAsync(_b, immediate: false);
        Assert.Same(_a, preview.Current);

        delays.Elapse(0);
        await show;
        Assert.Same(_b, preview.Current);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public async Task UnavailableItemsDontOpen()
    {
        var delays = new ManualDelays();
        bool available = true;
        using var preview = Create(delays, _ => available);

        var show = preview.ShowAsync(_a, immediate: false);
        available = false;   // e.g. deleted while the pointer rested on it
        delays.Elapse(0);
        await show;
        await preview.ShowAsync(_a, immediate: true);

        Assert.Null(preview.Current);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task HidingAClosedPreviewDoesntNotify()
    {
        using var preview = Create(new ManualDelays());

        await preview.HideAsync();

        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task ResetClosesWithoutNotifyingAndCancelsThePendingOpen()
    {
        var delays = new ManualDelays(honorCancellation: false);
        using var preview = Create(delays);
        await preview.ShowAsync(_a, immediate: true);

        var show = preview.ShowAsync(_b, immediate: false);
        preview.Reset();
        delays.Elapse(0);
        await show;

        Assert.Null(preview.Current);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task DisposedPreviewsNeverOpenOrNotify()
    {
        var delays = new ManualDelays(honorCancellation: false);
        var preview = Create(delays);
        var show = preview.ShowAsync(_a, immediate: false);

        preview.Dispose();
        delays.Elapse(0);
        await show;
        await preview.ShowAsync(_a, immediate: true);
        await preview.HideAsync();

        Assert.Null(preview.Current);
        Assert.Equal(0, _changes);
    }
}
