using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public sealed class ManagedQueueConfigTests
{
    private static ClusterQueue Managed(string alias, int id = 1) => new()
    {
        Id = id,
        Alias = alias,
        SchedulerType = ClusterScheduler.Managed,
        ManagedCores = 8,
        ManagedMemoryGb = 32,
        ManagedGpus = 1
    };

    [Fact]
    public void ASecondManagedQueueIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateOnly(
                [Managed("Workstation")],
                Managed("Copy", 2)));

        Assert.Contains("Workstation", error.Message);
    }

    [Fact]
    public void DuplicateManagedQueuesLoadedFromConfigurationAreDisabledDeterministically()
    {
        var first = Managed("Workstation", 3);
        var second = Managed("Copy", 7);

        var disabled = ManagedQueueRules.DisableDuplicateManagedQueues([second, first]);

        Assert.Same(second, Assert.Single(disabled));
        Assert.Null(first.ManagedDisabledReason);
        Assert.Contains("Workstation", second.ManagedDisabledReason);
    }

    [Fact]
    public void DisabledVerdictIsLiftedWhenDuplicationIsRemoved()
    {
        var first = Managed("Workstation", 1);
        var second = Managed("Copy", 2);
        ManagedQueueRules.DisableDuplicateManagedQueues([first, second]);

        first.SchedulerType = ClusterScheduler.Slurm;
        ManagedQueueRules.DisableDuplicateManagedQueues([first, second]);

        Assert.Null(second.ManagedDisabledReason);
    }

    [Fact]
    public void ChangingBackendOwnershipIsRefusedWhileAttemptsAreActive()
    {
        var current = Managed("Workstation");
        var proposed = Managed("Workstation");
        proposed.SchedulerType = ClusterScheduler.Slurm;

        Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateChange(
                current,
                proposed,
                [current],
                hasActiveAttempts: true));
    }

    [Fact]
    public void EditingExternalSchedulerProtocolIsSafeForActiveAttempts()
    {
        var current = new ClusterQueue
        {
            Id = 1,
            SchedulerType = ClusterScheduler.Slurm
        };
        var proposed = new ClusterQueue
        {
            Id = 1,
            SchedulerType = ClusterScheduler.Flux
        };

        ManagedQueueRules.ValidateChange(
            current,
            proposed,
            [current],
            hasActiveAttempts: true);
    }

    [Fact]
    public void ActiveQueueCannotBeDeleted()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateDelete(
                Managed("Workstation"),
                hasActiveAttempts: true));
    }
}
