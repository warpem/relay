using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public class ExecutionCoordinatorTests
{
    private static readonly ExecutionQueuePolicy LocalQueue =
        new(-1, ExecutionBackendKind.Local, new ResourceVector(4, 32, 2));

    private static JobAddress Job(int id) => new(1, 1, id);

    [Fact]
    public void OneJobCannotHaveTwoActiveAttempts()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        coordinator.RequestRun(Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.RequestRun(Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true));
    }

    [Fact]
    public void DependencyBlockedWorkDoesNotJoinFifoUntilItBecomesEligible()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);

        var blocked = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: false);
        var ready = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        Assert.Null(blocked.Attempt.EnqueueSequence);
        Assert.Equal(1, ready.Attempt.EnqueueSequence);
        Assert.Single(ready.Effects, effect => effect is PrepareExecution);

        var effects = coordinator.DependenciesSatisfied(blocked.Attempt.Id);

        Assert.Equal(2, blocked.Attempt.EnqueueSequence);
        Assert.Single(effects, effect => effect is PrepareExecution);
    }

    [Fact]
    public void StrictFifoDoesNotFillAGapBehindAnOlderAttempt()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var holder = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(3, 1, 0), dependenciesReady: true).Attempt;
        var oldLarge = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(2, 1, 0), dependenciesReady: true).Attempt;
        var newSmall = coordinator.RequestRun(
            Job(3), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;

        Assert.Single(coordinator.PreparationCompleted(holder.Id), effect => effect is StartExecution);
        coordinator.StartCompleted(holder.Id, new BackendReceipt("holder"), isRunning: true);

        Assert.Empty(coordinator.PreparationCompleted(oldLarge.Id));
        Assert.Empty(coordinator.PreparationCompleted(newSmall.Id));
        Assert.Equal(ExecutionPhase.Queued, oldLarge.Phase);
        Assert.Equal(ExecutionPhase.Queued, newSmall.Phase);
    }

    [Fact]
    public void AnOlderPreparingAttemptBlocksLaterPreparedWork()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        var second = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;

        Assert.Empty(coordinator.PreparationCompleted(second.Id));
        Assert.Equal(ExecutionPhase.Queued, second.Phase);

        var effects = coordinator.PreparationCompleted(first.Id);

        Assert.Equal(2, effects.Count(effect => effect is StartExecution));
        Assert.Equal(ExecutionPhase.Starting, first.Phase);
        Assert.Equal(ExecutionPhase.Starting, second.Phase);
    }

    [Fact]
    public void IndeterminateObservationsDoNotChangePhaseOrAppendHistory()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("pid"), isRunning: true);
        int historyCount = attempt.History.Count;

        coordinator.Observe(attempt.Id, new BackendObservation(
            BackendObservationKind.Indeterminate, "scheduler accounting is delayed"));
        coordinator.Observe(attempt.Id, new BackendObservation(
            BackendObservationKind.AbsentFromActiveView, "not in active queue"));

        Assert.Equal(ExecutionPhase.Running, attempt.Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, attempt.Health);
        Assert.Equal(historyCount, attempt.History.Count);
    }

    [Fact]
    public void CancelWhileWaitingForDependenciesIsImmediatelyTerminal()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: false).Attempt;

        var effects = coordinator.RequestCancel(attempt.Id);

        Assert.Empty(effects);
        Assert.Equal(ExecutionPhase.Canceled, attempt.Phase);
    }

    [Fact]
    public void CancelDuringPreparationWaitsForPreparationToSettle()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;

        var cancel = Assert.Single(coordinator.RequestCancel(attempt.Id));

        Assert.Null(Assert.IsType<CancelExecution>(cancel).Receipt);
        Assert.Equal(ExecutionPhase.Cancelling, attempt.Phase);

        coordinator.PreparationCompleted(attempt.Id);

        Assert.Equal(ExecutionPhase.Canceled, attempt.Phase);
    }

    [Fact]
    public void CancelDuringStartCancelsTheReceiptWhenItArrives()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        coordinator.PreparationCompleted(attempt.Id);

        var initialCancel = Assert.Single(coordinator.RequestCancel(attempt.Id));
        Assert.Null(Assert.IsType<CancelExecution>(initialCancel).Receipt);
        var effects = coordinator.StartCompleted(
            attempt.Id, new BackendReceipt("late-receipt"), isRunning: true);

        var cancel = Assert.Single(effects);
        Assert.Equal("late-receipt", Assert.IsType<CancelExecution>(cancel).Receipt.Id);
        Assert.Equal(ExecutionPhase.Cancelling, attempt.Phase);
    }

    [Fact]
    public void RerunGetsANewIdentityAndIgnoresLateResultsFromTheOldAttempt()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        coordinator.PreparationFailed(first.Id, "bad input");

        var second = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        var lateEffects = coordinator.StartCompleted(
            first.Id, new BackendReceipt("stale"), isRunning: true);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Empty(lateEffects);
        Assert.Equal(ExecutionPhase.Preparing, second.Phase);
    }

    [Fact]
    public void TerminalObservationReleasesCapacityBeforeFinalization()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(4, 1, 2), dependenciesReady: true).Attempt;
        var second = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(4, 1, 2), dependenciesReady: true).Attempt;
        coordinator.PreparationCompleted(first.Id);
        coordinator.StartCompleted(first.Id, new BackendReceipt("first"), isRunning: true);
        coordinator.PreparationCompleted(second.Id);

        var effects = coordinator.Observe(
            first.Id, new BackendObservation(BackendObservationKind.Succeeded));

        Assert.Contains(effects, effect => effect is FinalizeExecution { AttemptId: var id } && id == first.Id);
        Assert.Contains(effects, effect => effect is StartExecution { AttemptId: var id } && id == second.Id);
        Assert.Equal(ExecutionPhase.Finalizing, first.Phase);
        Assert.Equal(new[] { 0, 1 }, second.GpuIndices);
    }

    [Fact]
    public void DuplicateObservationsAppendEachTransitionOnlyOnce()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("job"), isRunning: false);

        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Running));
        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Running));

        Assert.Equal(1, attempt.History.Count(entry => entry.Phase == ExecutionPhase.Running));
    }

    [Fact]
    public void RecoveryInterruptsAllOwnerBoundWorkBeforeSchedulingAnythingElse()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var running = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(4, 1, 0), dependenciesReady: true).Attempt;
        var queued = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(4, 1, 0), dependenciesReady: true).Attempt;
        coordinator.PreparationCompleted(running.Id);
        coordinator.StartCompleted(running.Id, new BackendReceipt("running"), isRunning: true);
        coordinator.PreparationCompleted(queued.Id);

        var effects = coordinator.InterruptOwnerBoundAttempts();

        Assert.Empty(effects);
        Assert.Equal(ExecutionPhase.Interrupted, running.Phase);
        Assert.Equal(ExecutionPhase.Interrupted, queued.Phase);
    }

    [Fact]
    public void SnapshotRoundTripPreservesIdentityOrderingReceiptsAndHistory()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([LocalQueue, external]);
        var attempt = original.RequestRun(
            Job(1), 4, new ResourceVector(8, 64, 1), dependenciesReady: true).Attempt;
        original.PreparationCompleted(attempt.Id);
        original.StartCompleted(attempt.Id, new BackendReceipt("slurm-42"), isRunning: false);
        original.Observe(attempt.Id, new BackendObservation(
            BackendObservationKind.Indeterminate, "accounting delayed"));

        var restored = new ExecutionCoordinator([LocalQueue, external]);
        restored.Restore(original.CreateSnapshot());

        var copy = Assert.Single(restored.Attempts);
        Assert.Equal(attempt.Id, copy.Id);
        Assert.Equal(attempt.EnqueueSequence, copy.EnqueueSequence);
        Assert.Equal("slurm-42", copy.Receipt.Id);
        Assert.Equal(ExecutionPhase.Pending, copy.Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, copy.Health);
        Assert.Equal(attempt.History, copy.History);

        original.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        original.FinalizationCompleted(attempt.Id);

        var rerun = restored.RequestRun(
            Job(2), 4, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        Assert.True(rerun.EnqueueSequence > copy.EnqueueSequence);
    }

    [Fact]
    public void RestoreRejectsTwoActiveAttemptsForOneJob()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        var firstSnapshot = first.CreateSnapshot();
        var duplicate = firstSnapshot with { Id = Guid.NewGuid() };
        var snapshot = new ExecutionCoordinatorSnapshot(
            first.EnqueueSequence!.Value,
            [firstSnapshot, duplicate]);

        var restored = new ExecutionCoordinator([LocalQueue]);

        Assert.Throws<InvalidOperationException>(() => restored.Restore(snapshot));
    }

    [Fact]
    public void WorkerGroupStartsWithManagerAndReplacesTerminalWorkers()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 2, SubmissionLimit: 4)).Attempt;
        coordinator.PreparationCompleted(attempt.Id);

        var effects = coordinator.StartCompleted(
            attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var starts = effects.OfType<StartWorker>().ToArray();

        Assert.Equal(2, starts.Length);
        Assert.Equal(2, attempt.WorkerGroup.TotalSubmissions);

        coordinator.WorkerStarted(
            attempt.Id, starts[0].OperationId, new BackendReceipt("worker-1"), isRunning: true);
        coordinator.WorkerStarted(
            attempt.Id, starts[1].OperationId, new BackendReceipt("worker-2"), isRunning: false);

        effects = coordinator.ObserveWorkers(attempt.Id,
        [
            new WorkerObservation("worker-1", BackendObservationKind.Succeeded),
            new WorkerObservation("worker-2", BackendObservationKind.Running)
        ]);

        Assert.Single(effects, effect => effect is StartWorker);
        Assert.Equal(3, attempt.WorkerGroup.TotalSubmissions);
        Assert.Equal(2, attempt.WorkerGroup.AliveCount);
        Assert.Equal(1, attempt.WorkerGroup.RunningCount);
    }

    [Fact]
    public void WorkerGroupCanBeResizedWhileManagerRuns()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 2, SubmissionLimit: 6)).Attempt;
        coordinator.PreparationCompleted(attempt.Id);
        var starts = coordinator.StartCompleted(
                attempt.Id, new BackendReceipt("manager"), isRunning: true)
            .OfType<StartWorker>()
            .ToArray();
        coordinator.WorkerStarted(
            attempt.Id, starts[0].OperationId, new BackendReceipt("worker-1"), isRunning: true);
        coordinator.WorkerStarted(
            attempt.Id, starts[1].OperationId, new BackendReceipt("worker-2"), isRunning: true);

        var shrink = coordinator.ResizeWorkerGroup(attempt.Id, 1);
        var cancel = Assert.Single(shrink);
        Assert.Single(Assert.IsType<CancelWorkers>(cancel).Receipts);

        coordinator.WorkersCanceled(attempt.Id, ["worker-2"]);
        var grow = coordinator.ResizeWorkerGroup(attempt.Id, 3);

        Assert.Equal(2, grow.Count(effect => effect is StartWorker));
        Assert.Equal(3, attempt.WorkerGroup.DesiredCount);
    }

    [Fact]
    public void ManagerFinalizationWaitsForWorkerCleanup()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 2)).Attempt;
        coordinator.PreparationCompleted(attempt.Id);
        var start = Assert.Single(coordinator.StartCompleted(
            attempt.Id, new BackendReceipt("manager"), isRunning: true));
        var workerStart = Assert.IsType<StartWorker>(start);
        coordinator.WorkerStarted(
            attempt.Id, workerStart.OperationId, new BackendReceipt("worker"), isRunning: true);

        var completion = coordinator.Observe(
            attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));

        var cancel = Assert.Single(completion.OfType<CancelWorkers>());
        Assert.Equal("worker", Assert.Single(cancel.Receipts).Id);
        Assert.DoesNotContain(completion, effect => effect is FinalizeExecution);

        var cleanup = coordinator.WorkersCanceled(attempt.Id, ["worker"]);

        Assert.Single(cleanup, effect => effect is FinalizeExecution);
    }

    [Fact]
    public void FinalizeOnlyAttemptUsesTheSameLifecycleAuthority()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);

        var requested = coordinator.RequestFinalization(Job(1), -1);

        Assert.Equal(ExecutionPurpose.FinalizeOnly, requested.Attempt.Purpose);
        Assert.Equal(ExecutionPhase.Finalizing, requested.Attempt.Phase);
        Assert.Single(requested.Effects, effect => effect is FinalizeExecution);

        coordinator.FinalizationCompleted(requested.Attempt.Id);

        Assert.Equal(ExecutionPhase.Succeeded, requested.Attempt.Phase);
    }

    [Fact]
    public void RecoveryDoesNotResubmitAnAmbiguousExternalStart()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([external]);
        var attempt = original.RequestRun(
            Job(1), 4, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        original.PreparationCompleted(attempt.Id);

        var restored = new ExecutionCoordinator([external]);
        restored.Restore(original.CreateSnapshot());
        var effects = restored.Recover();
        var copy = Assert.Single(restored.Attempts);

        Assert.Empty(effects);
        Assert.Equal(ExecutionPhase.Starting, copy.Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, copy.Health);
        Assert.DoesNotContain(copy.History, entry => entry.Detail == copy.HealthDetail);
    }

    [Fact]
    public void RestoreKeepsAttemptsWhoseQueueDefinitionWasDeleted()
    {
        var external = new ExecutionQueuePolicy(
            4,
            ExecutionBackendKind.ExternalScheduler,
            BackendConfiguration: "{\"scheduler\":\"snapshot\"}");
        var original = new ExecutionCoordinator([external]);
        var attempt = original.RequestRun(
            Job(1), 4, new ResourceVector(1, 1, 0), dependenciesReady: true).Attempt;
        original.PreparationCompleted(attempt.Id);
        original.StartCompleted(attempt.Id, new BackendReceipt("42"), isRunning: false);

        var restored = new ExecutionCoordinator([]);
        restored.Restore(original.CreateSnapshot());

        var copy = Assert.Single(restored.Attempts);
        Assert.Equal("42", copy.Receipt.Id);
        Assert.Empty(restored.Recover());
    }
}
