using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public class PreparationCancellationTests
{
    [Fact]
    public void CapturedEntryCanBeCanceledAfterPreparationFinishes()
    {
        var preparation = new PreparationCancellation(CancellationToken.None);
        var capturedByShutdown = preparation;

        preparation.Dispose();

        // Force the ordering that used to race between dictionary lookup and Cancel.
        capturedByShutdown.Cancel();
        preparation.Dispose();
    }

    [Fact]
    public async Task CompletionDuringCancellationDefersDisposalUntilCallbacksReturn()
    {
        using var preparation = new PreparationCancellation(CancellationToken.None);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = preparation.Token.Register(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(preparation.Token.IsCancellationRequested);
        });

        var cancel = Task.Run(preparation.Cancel);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preparation.Dispose();
            preparation.Cancel();
        }
        finally
        {
            release.Set();
        }
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => preparation.Token);
    }

    [Fact]
    public void CallbackCanCompletePreparationWithoutDeadlocking()
    {
        using var preparation = new PreparationCancellation(CancellationToken.None);
        using var registration = preparation.Token.Register(preparation.Dispose);

        preparation.Cancel();

        Assert.Throws<ObjectDisposedException>(() => preparation.Token);
    }

    [Fact]
    public void LifetimeCancellationStillCancelsPreparation()
    {
        using var lifetime = new CancellationTokenSource();
        using var preparation = new PreparationCancellation(lifetime.Token);

        lifetime.Cancel();

        Assert.True(preparation.Token.IsCancellationRequested);
    }
}
