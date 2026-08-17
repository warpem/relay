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

    #region The single-managed-queue rule at load

    [Fact]
    public void LoadingTwoManagedQueues_DisablesAllButTheLowestNumbered()
    {
        // CreateClusterQueue and UpdateQueue both refuse a second managed queue, but nothing
        // guarded loading — so a hand-edited, copied or half-migrated state file could start Relay
        // in exactly the configuration its own UI cannot produce: two queues sharing the host-wide
        // executor while each declares the whole machine, both handing out CUDA device 0.
        var first = Managed("Workstation");  first.Id  = 3;
        var second = Managed("Copy of Workstation"); second.Id = 7;

        var disabled = ManagedQueueRules.DisableDuplicateManagedQueues(new[] { second, first });

        // Lowest Id wins: stable across reloads, and it is the one the user has been running on.
        Assert.Same(second, Assert.Single(disabled));
        Assert.Null(first.ManagedDisabledReason);
        Assert.Contains("Workstation", second.ManagedDisabledReason);
    }

    [Fact]
    public void ADisabledDuplicate_RejectsEveryJob_WithTheReasonItWasGiven()
    {
        var first = Managed("Workstation");  first.Id  = 1;
        var second = Managed("Copy", new ManagedExecutor()); second.Id = 2;

        ManagedQueueRules.DisableDuplicateManagedQueues(new[] { first, second });

        // Reject, not Busy: no amount of waiting resolves a duplicated configuration.
        var reject = Assert.IsType<AdmissionResult.Reject>(second.CanAdmit(null));
        Assert.Contains("only", reject.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneManagedQueue_IsLeftAlone_AndAVerdictIsLiftedWhenTheDuplicationGoesAway()
    {
        var first = Managed("Workstation");  first.Id  = 1;
        var second = Managed("Copy"); second.Id = 2;

        ManagedQueueRules.DisableDuplicateManagedQueues(new[] { first, second });
        Assert.NotNull(second.ManagedDisabledReason);

        // The recommended fix: switch the other one to a real scheduler. Recomputing must lift the
        // verdict, or the surviving queue would reject every job until Relay was restarted.
        first.SchedulerType = ClusterScheduler.Slurm;

        Assert.Empty(ManagedQueueRules.DisableDuplicateManagedQueues(new[] { first, second }));
        Assert.Null(second.ManagedDisabledReason);
    }

    [Fact]
    public void NonManagedQueues_AreNeverDisabled()
    {
        var slurm = new ClusterQueue((_, _) => { })
                    { Id = 1, Alias = "Cluster", SchedulerType = ClusterScheduler.Slurm };
        var flux = new ClusterQueue((_, _) => { })
                   { Id = 2, Alias = "Other", SchedulerType = ClusterScheduler.Flux };

        Assert.Empty(ManagedQueueRules.DisableDuplicateManagedQueues(new[] { slurm, flux }));
    }

    #endregion

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
    public void RenamingTheManagedQueue_IsAllowed_AndIsNotMistakenForASecondOne()
    {
        // Two things at once. The candidate handed to ValidateOnly is the copy, so its identity
        // check cannot recognise it as the queue being edited; ValidateChange has to exclude the
        // original by reference. And nothing about the host's capacity moved, so running jobs are
        // no reason to refuse — the queue must not be asked whether it is busy at all.
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
