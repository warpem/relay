using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public sealed class ManagedRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartInterruptsManagedOwnershipWithoutActingOnItsSavedPid(bool activated)
    {
        var resources = new ResourceVector(4, 16, 1);
        var queue = new ExecutionQueuePolicy(1, ExecutionBackendKind.Managed, resources);
        var original = new ExecutionCoordinator([queue]);
        var previous = original.RequestRun(new JobAddress(1, 1, 1), 1, resources, true);
        original.PreparationCompleted(previous.Id);
        original.PlanEffects();
        original.StartCompleted(previous.Id, new BackendReceipt("12345"), isRunning: false);
        if (activated)
            original.ActivationCompleted(previous.Id);
        Assert.True(previous.HasAllocation);

        var restarted = new ExecutionCoordinator([queue]);
        restarted.Restore(original.CreateSnapshot());
        restarted.Recover();

        var interrupted = Assert.Single(restarted.Attempts);
        Assert.Equal(ExecutionPhase.Interrupted, interrupted.Phase);
        Assert.False(interrupted.HasAllocation);
        Assert.Empty(interrupted.GpuIndices);
        Assert.Empty(restarted.PlanEffects());

        // The old owner's EOF cleanup is separate from restart scheduling. Its
        // receipt must not become a probe, cancellation, or reactivation request.
        var next = restarted.RequestRun(new JobAddress(1, 1, 2), 1, resources, true);
        restarted.PreparationCompleted(next.Id);
        var start = Assert.IsType<StartExecution>(Assert.Single(restarted.PlanEffects()));
        Assert.Equal(next.Id, start.AttemptId);
        Assert.True(next.HasAllocation);
    }
}
