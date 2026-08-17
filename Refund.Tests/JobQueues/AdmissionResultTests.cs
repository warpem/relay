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
        Assert.Same(AdmissionResult.Admitted, AdmissionResult.Admitted);
        Assert.Same(AdmissionResult.IsBusy, AdmissionResult.IsBusy);
    }
}
