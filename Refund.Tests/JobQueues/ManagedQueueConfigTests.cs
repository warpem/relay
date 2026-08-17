using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ManagedQueueConfigTests
{
    private static ClusterQueue Managed(string alias, ManagedExecutor executor = null) =>
        new ClusterQueue((_, _) => { })
        {
            Alias = alias,
            SchedulerType = ClusterScheduler.Managed,
            ManagedCores = 8,
            ManagedMemoryGb = 32,
            ManagedGpus = 1,
            Executor = executor,
        };

    [Fact]
    public void ASecondManagedQueue_IsRefused()
    {
        var existing = new[] { Managed("Local") };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateOnly(existing, candidate: Managed("Local Copy")));

        Assert.Contains("Local", error.Message);
    }

    [Fact]
    public void ReconfiguringTheSameManagedQueue_IsAllowed()
    {
        var queue = Managed("Local");

        // Editing the queue that already exists must not trip the single-queue rule against itself.
        ManagedQueueRules.ValidateOnly(new[] { queue }, candidate: queue);
    }

    [Fact]
    public void AnotherSchedulerAlongsideAManagedQueue_IsFine()
    {
        var existing = new[] { Managed("Local") };
        var slurm = new ClusterQueue((_, _) => { })
                    { Alias = "Cluster", SchedulerType = ClusterScheduler.Slurm };

        ManagedQueueRules.ValidateOnly(existing, candidate: slurm);
    }

    [Fact]
    public void ChangingTotals_IsRefusedWhileTheQueueHasLiveEntries()
    {
        var executor = new ManagedExecutor();
        var queue = Managed("Local", executor);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateTotalsChange(queue, hasLiveEntries: true));

        Assert.Contains("running", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangingTotals_IsAllowedWhenIdle()
    {
        ManagedQueueRules.ValidateTotalsChange(Managed("Local"), hasLiveEntries: false);
    }

    [Fact]
    public void ANonManagedQueue_IsNeverBlockedByLiveEntries()
    {
        // Renaming a Slurm queue must not be refused just because something is running on the host.
        var slurm = new ClusterQueue((_, _) => { })
                    { Alias = "Cluster", SchedulerType = ClusterScheduler.Slurm };

        ManagedQueueRules.ValidateTotalsChange(slurm, hasLiveEntries: true);
    }
}
