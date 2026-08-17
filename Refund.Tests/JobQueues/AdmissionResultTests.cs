using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class AdmissionResultTests
{
    [Fact]
    public void ClusterQueue_WithAnExternalScheduler_AlwaysAdmits()
    {
        // Every non-managed queue must behave exactly as before this feature existed.
        var queue = new ClusterQueue((_, _) => { }) { SchedulerType = ClusterScheduler.Slurm };
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(null));
    }

    [Fact]
    public void Reject_CarriesItsReason()
    {
        var rejected = new AdmissionResult.Reject("needs 4 GPUs, queue has 1");
        Assert.Contains("4 GPUs", rejected.Reason);
    }

    [Fact]
    public void AdmitAndBusy_AreSharedSingletons()
    {
        // Returned for every waiting job on every daemon tick; do not allocate per call.
        // Comparing a static field to itself proves nothing, so pin what the fields actually are:
        // the right case, and distinct from each other.
        Assert.IsType<AdmissionResult.Admit>(AdmissionResult.Admitted);
        Assert.IsType<AdmissionResult.Busy>(AdmissionResult.IsBusy);
        Assert.NotSame(AdmissionResult.Admitted, AdmissionResult.IsBusy);
    }

    [Fact]
    public void BusyIsNotAdmit_SoTheCallerCannotConflateThem()
    {
        // "Busy, ask again" and "start now" differ only by type; a caller pattern-matching on
        // Admit must not match Busy.
        Assert.IsNotType<AdmissionResult.Admit>(AdmissionResult.IsBusy);
        Assert.IsNotType<AdmissionResult.Busy>(AdmissionResult.Admitted);
    }
}
