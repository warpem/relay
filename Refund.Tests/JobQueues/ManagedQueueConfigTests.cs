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
    public void ChangingTheSchedulerOfABusyManagedQueue_SaysSoInTheMessage()
    {
        // totalsChanged covers SchedulerType, so this refusal is reachable by switching a busy
        // managed queue to Slurm — a message naming only cores, memory and GPUs would not describe
        // what the user actually did.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateTotalsChange(Managed("Local"), hasLiveEntries: true));

        Assert.Contains("scheduler", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonManagedQueue_IsNeverBlockedByLiveEntries()
    {
        // Renaming a Slurm queue must not be refused just because something is running on the host.
        var slurm = new ClusterQueue((_, _) => { })
                    { Alias = "Cluster", SchedulerType = ClusterScheduler.Slurm };

        ManagedQueueRules.ValidateTotalsChange(slurm, hasLiveEntries: true);
    }

    #region Deleting a managed queue

    [Fact]
    public void DeletingAManagedQueue_IsRefusedWhileItHasLiveEntries()
    {
        // Worse than an edit: nothing polls the deleted queue's jobs afterwards, the executor holds
        // their cores and GPUs for as long as their status says they are active, and a replacement
        // queue declaring the host's full totals is two clicks away.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateDelete(Managed("Local"), hasLiveEntries: true));

        Assert.Contains("Local", error.Message);
    }

    [Fact]
    public void DeletingAManagedQueue_IsAllowedWhenTheHostHoldsNothingForIt()
    {
        ManagedQueueRules.ValidateDelete(Managed("Local"), hasLiveEntries: false);
    }

    [Fact]
    public void DeletingANonManagedQueue_IsNeverBlocked()
    {
        var slurm = new ClusterQueue((_, _) => { })
                    { Alias = "Cluster", SchedulerType = ClusterScheduler.Slurm };

        ManagedQueueRules.ValidateDelete(slurm, hasLiveEntries: true);
    }

    #endregion

    #region Judging a proposed edit (the dry-run probe)

    /// <summary>
    /// A live-entries answer that fails the test if it is asked for. The probe must not reconcile
    /// the executor for an edit that does not touch the totals.
    /// </summary>
    private static readonly Func<bool> NeverAsked =
        () => throw new Xunit.Sdk.XunitException(
            "ValidateChange asked whether the queue is busy for an edit that changes no totals.");

    [Fact]
    public void AnEditThatWouldCreateASecondManagedQueue_IsRefused()
    {
        var existing = Managed("Local");
        var slurm = new ClusterQueue((_, _) => { })
                    { Alias = "Cluster", SchedulerType = ClusterScheduler.Slurm };

        // Switching an ordinary queue to Managed is the same mistake as copying the managed one,
        // reached through UpdateQueue instead of CreateClusterQueue.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateChange(
                slurm,
                q => ((ClusterQueue)q).SchedulerType = ClusterScheduler.Managed,
                new[] { existing, slurm },
                () => false));

        Assert.Contains("Local", error.Message);
        Assert.Equal(ClusterScheduler.Slurm, slurm.SchedulerType);   // the probe took the edit, not the queue
    }

    [Fact]
    public void EditingTheManagedQueueItself_IsNotMistakenForASecondOne()
    {
        // The candidate handed to ValidateOnly is the copy, so its identity check cannot recognise
        // it as the queue being edited; ValidateChange has to exclude the original by reference.
        var queue = Managed("Local");

        ManagedQueueRules.ValidateChange(queue, q => q.Alias = "Workstation",
                                         new[] { queue }, NeverAsked);
    }

    [Fact]
    public void RenamingABusyManagedQueue_IsAllowed()
    {
        // Nothing about the host's capacity moved, so running jobs are no reason to refuse — and
        // the queue must not be asked whether it is busy at all.
        var queue = Managed("Local");

        ManagedQueueRules.ValidateChange(queue, q => q.Alias = "Workstation",
                                         new[] { queue }, NeverAsked);

        Assert.Equal("Local", queue.Alias);   // validation alone changes nothing
    }

    [Fact]
    public void ChangingTheTotalsOfABusyManagedQueue_IsRefused()
    {
        var queue = Managed("Local");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateChange(queue, q => ((ClusterQueue)q).ManagedGpus = 4,
                                             new[] { queue }, () => true));

        Assert.Contains("running", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, queue.ManagedGpus);
    }

    [Fact]
    public void SwitchingABusyManagedQueueToAnotherScheduler_IsRefused()
    {
        var queue = Managed("Local");

        Assert.Throws<InvalidOperationException>(() =>
            ManagedQueueRules.ValidateChange(queue, q => ((ClusterQueue)q).SchedulerType = ClusterScheduler.Slurm,
                                             new[] { queue }, () => true));
    }

    [Fact]
    public void ChangingTheTotalsOfAnIdleManagedQueue_IsAllowed()
    {
        var queue = Managed("Local");

        ManagedQueueRules.ValidateChange(queue, q => ((ClusterQueue)q).ManagedGpus = 4,
                                         new[] { queue }, () => false);
    }

    [Fact]
    public void TheProbeSeesTheQueuesRealValues_NotFreshDefaults()
    {
        // The copy is what the update action reads, so an action that consults a property the probe
        // does not carry would judge the edit against a default. Pin the seven it does carry: an
        // action reading them must see what the real queue holds.
        var queue = Managed("Local");
        queue.SubmissionScriptTemplate = "#!/bin/bash\n{{ command }}";
        queue.CustomVariables["threads"] = ("Threads", "4");

        ClusterQueue? probed = null;

        ManagedQueueRules.ValidateChange(queue, q => probed = (ClusterQueue)q,
                                         new[] { queue }, NeverAsked);

        var seen = Assert.IsType<ClusterQueue>(probed);
        Assert.NotSame(queue, seen);
        Assert.Equal(queue.Id, seen.Id);
        Assert.Equal(queue.Alias, seen.Alias);
        Assert.Equal(queue.SchedulerType, seen.SchedulerType);
        Assert.Equal(queue.ManagedCores, seen.ManagedCores);
        Assert.Equal(queue.ManagedMemoryGb, seen.ManagedMemoryGb);
        Assert.Equal(queue.ManagedGpus, seen.ManagedGpus);
        Assert.Equal(queue.SubmissionScriptTemplate, seen.SubmissionScriptTemplate);
        Assert.Equal(("Threads", "4"), seen.CustomVariables["threads"]);

        // And the dictionary is a copy: an action editing a variable must not reach the real queue.
        seen.CustomVariables["threads"] = ("Threads", "8");
        Assert.Equal(("Threads", "4"), queue.CustomVariables["threads"]);
    }

    #endregion
}
