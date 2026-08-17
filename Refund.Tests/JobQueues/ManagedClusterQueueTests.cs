using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ManagedClusterQueueTests
{
    private static ClusterQueue Managed() => new ClusterQueue((_, _) => { })
    {
        SchedulerType = ClusterScheduler.Managed,
        ManagedCores = 8,
        ManagedMemoryGb = 32,
        ManagedGpus = 2,
    };

    [Fact]
    public void ManagedDefaults_AreSensibleForASingleWorkstation()
    {
        var queue = new ClusterQueue((_, _) => { });

        Assert.Equal(Environment.ProcessorCount, queue.ManagedCores);
        Assert.Equal(64, queue.ManagedMemoryGb);
        Assert.Equal(1, queue.ManagedGpus);
    }

    [Fact]
    public void ManagedProperties_RoundTripThroughJson()
    {
        var saved = Managed().ToJson();

        var loaded = new ClusterQueue((_, _) => { });
        loaded.ReadFromJson(saved, (_, _, _) => null);

        Assert.Equal(ClusterScheduler.Managed, loaded.SchedulerType);
        Assert.Equal(8, loaded.ManagedCores);
        Assert.Equal(32, loaded.ManagedMemoryGb);
        Assert.Equal(2, loaded.ManagedGpus);
    }

    [Fact]
    public void ManagedTotals_ReadTheQueuesCurrentValues()
    {
        // Read per call, never snapshotted: ClusterQueue is constructed before ReadFromJson
        // hydrates it, and an admin can edit the totals later.
        var queue = Managed();
        Assert.Equal(new ResourceTotals(8, 32, 2), queue.ManagedTotals);

        queue.ManagedGpus = 4;
        Assert.Equal(new ResourceTotals(8, 32, 4), queue.ManagedTotals);
    }

    [Fact]
    public void ParsersAreNeverConsultedForAManagedQueue()
    {
        // There is no scheduler output to parse; reaching a parser means a wiring mistake.
        var queue = Managed();

        Assert.Throws<InvalidOperationException>(() => queue.ParseClusterJobId("anything"));
        Assert.Equal(ClusterJobStatus.Unknown, queue.ParseClusterJobStatus("anything"));
    }

    [Fact]
    public void CanAdmit_WithoutAnExecutor_RejectsRatherThanRunningUnaccounted()
    {
        // QueueRepository injects the host-wide executor. If that wiring is missing, failing loudly
        // beats silently spawning processes nobody is accounting for.
        var result = Managed().CanAdmit(null);

        var reject = Assert.IsType<AdmissionResult.Reject>(result);
        Assert.Contains("executor", reject.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonManagedQueue_AdmitsWithoutConsultingAnExecutor()
    {
        // The external scheduler arbitrates; Relay must not second-guess it, and there is no
        // executor attached to such a queue in the first place. Every non-managed queue must
        // behave exactly as it did before this feature existed.
        var queue = new ClusterQueue((_, _) => { }) { SchedulerType = ClusterScheduler.Slurm };

        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(null));
    }

    [Fact]
    public void AdmitAndBusy_AreSharedSingletons()
    {
        // Returned for every waiting job on every daemon tick; do not allocate per call.
        // Comparing a static field to itself proves nothing, so pin what the fields actually are:
        // the right case, and distinct from each other. "Busy, ask again" and "start now" differ
        // only by type, so a caller pattern-matching on Admit must not match Busy.
        Assert.IsType<AdmissionResult.Admit>(AdmissionResult.Admitted);
        Assert.IsType<AdmissionResult.Busy>(AdmissionResult.IsBusy);
        Assert.NotSame(AdmissionResult.Admitted, AdmissionResult.IsBusy);
    }
}
