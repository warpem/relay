using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public class ExecutionCoordinatorTests
{
    private static readonly ExecutionQueuePolicy LocalQueue =
        new(-1, ExecutionBackendKind.Local, new ResourceVector(4, 32, 2));

    private static JobAddress Job(int id) => new(1, 1, id);

    [Fact]
    public void OneJobCannotHaveTwoAttempts()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        coordinator.RequestRun(Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.RequestRun(Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true));
    }

    [Fact]
    public void PlanningDerivesStableEffectsWithoutAppendingHistory()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        int historyCount = attempt.History.Count;

        var first = Assert.Single(coordinator.PlanEffects());
        var second = Assert.Single(coordinator.PlanEffects());

        Assert.Equal(first, second);
        Assert.Equal(historyCount, attempt.History.Count);
    }

    [Fact]
    public void DependencyBlockedWorkDoesNotJoinFifoUntilItBecomesEligible()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);

        var blocked = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: false);
        var ready = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        Assert.Null(blocked.EnqueueSequence);
        Assert.Equal(1, ready.EnqueueSequence);
        Assert.Contains(coordinator.PlanEffects(), effect =>
            effect is PrepareExecution { AttemptId: var id } && id == ready.Id);

        coordinator.DependenciesSatisfied(blocked.Id);
        var effects = coordinator.PlanEffects();

        Assert.Equal(2, blocked.EnqueueSequence);
        Assert.Contains(effects, effect =>
            effect is PrepareExecution { AttemptId: var id } && id == blocked.Id);
    }

    [Fact]
    public void DependencyCheckFailureIsTerminalAndDoesNotRetry()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: false);

        coordinator.DependencyCheckFailed(attempt.Id, "broken dependency graph");
        Assert.Equal(ExecutionPhase.Failed, attempt.Phase);
        Assert.Null(coordinator.CurrentAttempt(attempt.Job));
        Assert.Contains(attempt.History, entry => entry.Detail == "broken dependency graph");

        coordinator.DependencyCheckFailed(attempt.Id, "again");
        Assert.Empty(coordinator.PlanEffects());
    }

    [Fact]
    public void StrictFifoDoesNotFillAGapBehindAnOlderAttempt()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var holder = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(3, 1, 0), dependenciesReady: true);
        var oldLarge = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(2, 1, 0), dependenciesReady: true);
        var newSmall = coordinator.RequestRun(
            Job(3), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        coordinator.PreparationCompleted(holder.Id);
        Assert.Single(coordinator.PlanEffects(), effect => effect is StartExecution);
        coordinator.StartCompleted(holder.Id, new BackendReceipt("holder"), isRunning: true);

        coordinator.PreparationCompleted(oldLarge.Id);
        coordinator.PreparationCompleted(newSmall.Id);

        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(ExecutionPhase.Queued, oldLarge.Phase);
        Assert.Equal(ExecutionPhase.Queued, newSmall.Phase);
    }

    [Fact]
    public void AnOlderPreparingAttemptBlocksLaterPreparedWork()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        var second = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        coordinator.PreparationCompleted(second.Id);
        Assert.DoesNotContain(coordinator.PlanEffects(), effect => effect is StartExecution);
        Assert.Equal(ExecutionPhase.Queued, second.Phase);

        coordinator.PreparationCompleted(first.Id);
        var effects = coordinator.PlanEffects();

        Assert.Equal(2, effects.Count(effect => effect is StartExecution));
        Assert.Equal(ExecutionPhase.Starting, first.Phase);
        Assert.Equal(ExecutionPhase.Starting, second.Phase);
    }

    [Fact]
    public void IndeterminateObservationsDoNotChangePhaseOrAppendHistory()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
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
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: false);

        coordinator.RequestCancel(attempt.Id);

        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(ExecutionPhase.Canceled, attempt.Phase);
    }

    [Fact]
    public void CancelDuringPreparationWaitsForPreparationToSettle()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);

        coordinator.RequestCancel(attempt.Id);

        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(ExecutionPhase.Cancelling, attempt.Phase);

        coordinator.PreparationCompleted(attempt.Id);

        Assert.Equal(ExecutionPhase.Canceled, attempt.Phase);
    }

    [Fact]
    public void CancelDuringStartCancelsTheReceiptWhenItArrives()
    {
        var externalQueue = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([externalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), 4, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();

        coordinator.RequestCancel(attempt.Id);
        Assert.Empty(coordinator.PlanEffects());
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("late-receipt"), isRunning: true);
        var effects = coordinator.PlanEffects();

        var cancel = Assert.Single(effects);
        Assert.Equal("late-receipt", Assert.IsType<CancelExecution>(cancel).Receipt.Id);
        Assert.Equal(ExecutionPhase.Cancelling, attempt.Phase);

        coordinator.CancelCompleted(attempt.Id, new BackendObservation(
            BackendObservationKind.Indeterminate,
            "cancellation accepted"));

        Assert.Equal(ExecutionPhase.Stopping, attempt.Phase);
        Assert.Empty(coordinator.PlanEffects());

        var restored = new ExecutionCoordinator([externalQueue]);
        restored.Restore(coordinator.CreateSnapshot());
        restored.Recover();

        Assert.Equal(ExecutionPhase.Stopping, Assert.Single(restored.Attempts).Phase);
        Assert.Empty(restored.PlanEffects());
    }

    [Fact]
    public void ManagedStartIsActivatedOnlyAfterItsReceiptIsRecorded()
    {
        var managed = new ExecutionQueuePolicy(
            3,
            ExecutionBackendKind.Managed,
            new ResourceVector(4, 16, 1));
        var coordinator = new ExecutionCoordinator([managed]);
        var attempt = coordinator.RequestRun(
            Job(1), 3, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();

        coordinator.StartCompleted(
            attempt.Id,
            new BackendReceipt("runner"),
            isRunning: false);
        var effects = coordinator.PlanEffects();

        Assert.Equal(ExecutionPhase.Starting, attempt.Phase);
        Assert.Equal("runner", attempt.Receipt.Id);
        Assert.Single(effects, effect => effect is ActivateExecution);

        coordinator.ActivationCompleted(attempt.Id);

        Assert.Equal(ExecutionPhase.Running, attempt.Phase);
    }

    [Fact]
    public void ExternalSubmissionsAreIssuedOneAtATimeInFifoOrder()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([external]);
        var first = coordinator.RequestRun(
            Job(1), 4, ResourceVector.None, dependenciesReady: true);
        var second = coordinator.RequestRun(
            Job(2), 4, ResourceVector.None, dependenciesReady: true);

        coordinator.PreparationCompleted(first.Id);
        var firstEffects = coordinator.PlanEffects();
        Assert.Single(firstEffects, effect => effect is StartExecution { AttemptId: var id } && id == first.Id);
        coordinator.PreparationCompleted(second.Id);
        Assert.DoesNotContain(coordinator.PlanEffects(), effect =>
            effect is StartExecution { AttemptId: var id } && id == second.Id);

        coordinator.StartCompleted(first.Id, new BackendReceipt("first"), isRunning: false);
        var secondEffects = coordinator.PlanEffects();

        Assert.Single(secondEffects, effect => effect is StartExecution { AttemptId: var id } && id == second.Id);
        Assert.Equal(ExecutionPhase.Starting, second.Phase);
    }

    [Fact]
    public void RerunGetsANewIdentityAndIgnoresLateResultsFromTheOldAttempt()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.PreparationFailed(first.Id, "bad input");
        coordinator.ForgetTerminalAttempts([first.Id]);

        var second = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.StartCompleted(first.Id, new BackendReceipt("stale"), isRunning: true);

        Assert.NotEqual(first.Id, second.Id);
        Assert.DoesNotContain(coordinator.PlanEffects(), effect => effect.AttemptId == first.Id);
        Assert.Equal(ExecutionPhase.Preparing, second.Phase);
    }

    [Fact]
    public void TerminalAttemptBlocksNewWorkUntilItIsForgotten()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.PreparationFailed(attempt.Id, "bad input");

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.RequestRun(Job(1), -1, ResourceVector.None, dependenciesReady: true));
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.RequestFinalization(Job(1), -1));

        coordinator.ForgetTerminalAttempts([attempt.Id]);

        Assert.NotNull(coordinator.RequestRun(
            Job(1), -1, ResourceVector.None, dependenciesReady: true));
    }

    [Fact]
    public void TerminalObservationReleasesCapacityBeforeFinalization()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(4, 1, 2), dependenciesReady: true);
        var second = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(4, 1, 2), dependenciesReady: true);
        coordinator.PreparationCompleted(first.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(first.Id, new BackendReceipt("first"), isRunning: true);
        coordinator.PreparationCompleted(second.Id);

        coordinator.Observe(first.Id, new BackendObservation(BackendObservationKind.Succeeded));
        var effects = coordinator.PlanEffects();

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
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
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
            Job(1), -1, new ResourceVector(4, 1, 0), dependenciesReady: true);
        var queued = coordinator.RequestRun(
            Job(2), -1, new ResourceVector(4, 1, 0), dependenciesReady: true);
        coordinator.PreparationCompleted(running.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(running.Id, new BackendReceipt("running"), isRunning: true);
        coordinator.PreparationCompleted(queued.Id);

        coordinator.InterruptOwnerBoundAttempts();

        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(ExecutionPhase.Interrupted, running.Phase);
        Assert.Equal(ExecutionPhase.Interrupted, queued.Phase);
    }

    [Fact]
    public void SnapshotRoundTripPreservesIdentityOrderingReceiptsAndHistory()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([LocalQueue, external]);
        var attempt = original.RequestRun(
            Job(1), 4, new ResourceVector(8, 64, 1), dependenciesReady: true);
        original.PreparationCompleted(attempt.Id);
        original.PlanEffects();
        original.StartCompleted(attempt.Id, new BackendReceipt("scheduler-42"), isRunning: false);
        original.Observe(attempt.Id, new BackendObservation(
            BackendObservationKind.Indeterminate, "accounting delayed"));

        var restored = new ExecutionCoordinator([LocalQueue, external]);
        restored.Restore(original.CreateSnapshot());

        var copy = Assert.Single(restored.Attempts);
        Assert.Equal(attempt.Id, copy.Id);
        Assert.Equal(attempt.EnqueueSequence, copy.EnqueueSequence);
        Assert.Equal("scheduler-42", copy.Receipt.Id);
        Assert.Equal(ExecutionPhase.Pending, copy.Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, copy.Health);
        Assert.Equal(attempt.History, copy.History);

        original.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        original.FinalizationCompleted(attempt.Id);

        var rerun = restored.RequestRun(
            Job(2), 4, new ResourceVector(1, 1, 0), dependenciesReady: true);
        Assert.True(rerun.EnqueueSequence > copy.EnqueueSequence);
    }

    [Fact]
    public void RestoreRejectsTwoActiveAttemptsForOneJob()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var first = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true);
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
            new WorkerGroupRequest(2, DesiredCount: 2, SubmissionLimit: 4));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();

        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var effects = coordinator.PlanEffects();
        var starts = effects.OfType<StartWorker>().ToArray();

        Assert.Equal(2, starts.Length);
        Assert.Equal(2, attempt.WorkerGroup.TotalSubmissions);

        coordinator.WorkerStarted(
            attempt.Id, starts[0].OperationId, new BackendReceipt("worker-1"), isRunning: true);
        coordinator.WorkerStarted(
            attempt.Id, starts[1].OperationId, new BackendReceipt("worker-2"), isRunning: false);

        coordinator.ObserveWorkers(attempt.Id,
        [
            new WorkerObservation("worker-1", BackendObservationKind.Succeeded),
            new WorkerObservation("worker-2", BackendObservationKind.Running)
        ]);
        effects = coordinator.PlanEffects();

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
            new WorkerGroupRequest(2, DesiredCount: 2, SubmissionLimit: 6));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var starts = coordinator.PlanEffects()
            .OfType<StartWorker>()
            .ToArray();
        coordinator.WorkerStarted(
            attempt.Id, starts[0].OperationId, new BackendReceipt("worker-1"), isRunning: true);
        coordinator.WorkerStarted(
            attempt.Id, starts[1].OperationId, new BackendReceipt("worker-2"), isRunning: true);

        coordinator.ResizeWorkerGroup(attempt.Id, 1);
        var shrink = coordinator.PlanEffects();
        var cancel = Assert.Single(shrink);
        Assert.Single(Assert.IsType<CancelWorkers>(cancel).Receipts);

        coordinator.ObserveWorkers(attempt.Id,
            [new WorkerObservation("worker-2", BackendObservationKind.Canceled)]);
        coordinator.ResizeWorkerGroup(attempt.Id, 3);
        var grow = coordinator.PlanEffects();

        Assert.Equal(2, grow.Count(effect => effect is StartWorker));
        Assert.Equal(3, attempt.WorkerGroup.DesiredCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkerPoolResizeRequiresAPositiveTarget(int desiredCount)
    {
        var (coordinator, attempt) = CreateRunningPool(2, 4);

        var error = Assert.Throws<InvalidOperationException>(() =>
            coordinator.ResizeWorkerGroup(attempt.Id, desiredCount));

        Assert.Contains("at least 1", error.Message);
        Assert.Equal(2, attempt.WorkerGroup.DesiredCount);
    }

    [Fact]
    public void WorkerPoolResizeRejectsInactiveAndNonPooledAttempts()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        Assert.Throws<InvalidOperationException>(() => coordinator.ResizeWorkerGroup(Guid.NewGuid(), 1));
        var attempt = coordinator.RequestRun(Job(1), -1, ResourceVector.None, true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            coordinator.ResizeWorkerGroup(attempt.Id, 1));

        Assert.Contains("does not have a worker pool", error.Message);
    }

    [Fact]
    public void WorkerPoolResizeRejectsEveryNonRunningPhase()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(Job(1), -1, ResourceVector.None, false,
            new WorkerGroupRequest(2, 1, 2));

        void AssertRejected()
        {
            Assert.Throws<InvalidOperationException>(() => coordinator.ResizeWorkerGroup(attempt.Id, 2));
            Assert.Equal(1, attempt.WorkerGroup.DesiredCount);
        }

        AssertRejected();
        coordinator.DependenciesSatisfied(attempt.Id);
        AssertRejected();
        coordinator.PreparationCompleted(attempt.Id);
        AssertRejected();
        coordinator.PlanEffects();
        AssertRejected();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), false);
        AssertRejected();
        coordinator.RequestCancel(attempt.Id);
        AssertRejected();
        coordinator.CancelCompleted(attempt.Id, new BackendObservation(BackendObservationKind.Indeterminate));
        AssertRejected();
        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Canceled));
        AssertRejected();
        coordinator.FinalizationCompleted(attempt.Id);
        AssertRejected();
    }

    [Fact]
    public void WorkerPoolIncreaseCannotReuseRetiringWorkersOrResetTheSubmissionBudget()
    {
        var (coordinator, attempt) = CreateRunningPool(3, 4);
        Assert.Throws<InvalidOperationException>(() => coordinator.ResizeWorkerGroup(attempt.Id, 5));
        coordinator.ResizeWorkerGroup(attempt.Id, 2);
        var cancel = Assert.Single(coordinator.PlanEffects().OfType<CancelWorkers>());
        coordinator.WorkerCancellationCompleted(attempt.Id, cancel.Receipts.Select(receipt =>
            new WorkerObservation(receipt.Id, BackendObservationKind.Indeterminate)).ToArray());

        var error = Assert.Throws<InvalidOperationException>(() =>
            coordinator.ResizeWorkerGroup(attempt.Id, 4));

        Assert.Contains("cannot exceed 3", error.Message);
        Assert.Contains("remaining lifetime submission budget is 1", error.Message);
        Assert.Equal(2, attempt.WorkerGroup.DesiredCount);
        Assert.Equal(4, attempt.WorkerGroup.SubmissionLimit);
        Assert.Equal(3, attempt.WorkerGroup.TotalSubmissions);
        coordinator.ResizeWorkerGroup(attempt.Id, 3);
        Assert.Equal(3, attempt.WorkerGroup.DesiredCount);
    }

    [Fact]
    public void ExhaustedWorkerPoolsCanStillBeDecreasedAndSetToTheSameTarget()
    {
        var (coordinator, attempt) = CreateRunningPool(4, 4);
        coordinator.ObserveWorkers(attempt.Id, attempt.WorkerGroup.Workers.Skip(1).Select(worker =>
            new WorkerObservation(worker.Receipt.Id, BackendObservationKind.Failed)).ToArray());
        Assert.Equal(1, attempt.WorkerGroup.AliveCount);

        coordinator.ResizeWorkerGroup(attempt.Id, 4);
        coordinator.ResizeWorkerGroup(attempt.Id, 3);
        coordinator.ResizeWorkerGroup(attempt.Id, 3);

        Assert.Equal(3, attempt.WorkerGroup.DesiredCount);
        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(4, attempt.WorkerGroup.SubmissionLimit);
        Assert.Equal(4, attempt.WorkerGroup.TotalSubmissions);
        Assert.Throws<InvalidOperationException>(() => coordinator.ResizeWorkerGroup(attempt.Id, 4));
    }

    [Fact]
    public void AcceptedWorkerCancellationWaitsForTerminalEvidenceAcrossRestart()
    {
        var managerQueue = new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler);
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([managerQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), 1, ResourceVector.None, dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 3, SubmissionLimit: 3));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        foreach (var start in coordinator.PlanEffects().OfType<StartWorker>())
            coordinator.WorkerStarted(
                attempt.Id, start.OperationId, new BackendReceipt(start.OperationId.ToString()), true);

        coordinator.ResizeWorkerGroup(attempt.Id, 1);
        var cancel = Assert.Single(coordinator.PlanEffects().OfType<CancelWorkers>());
        Assert.Equal(2, cancel.Receipts.Count);
        coordinator.WorkerCancellationCompleted(attempt.Id, cancel.Receipts
            .Select(receipt => new WorkerObservation(receipt.Id, BackendObservationKind.Indeterminate))
            .ToArray());

        var restored = new ExecutionCoordinator([managerQueue, workerQueue]);
        restored.Restore(coordinator.CreateSnapshot());
        restored.Recover();
        Assert.Empty(restored.PlanEffects());
        var workers = Assert.Single(restored.Attempts).WorkerGroup;
        Assert.Equal(3, workers.AliveCount);
        Assert.Equal(2, workers.Workers.Count(worker => worker.Phase == WorkerPhase.Stopping));

        restored.ObserveWorkers(attempt.Id, cancel.Receipts
            .Select(receipt => new WorkerObservation(receipt.Id, BackendObservationKind.Canceled))
            .ToArray());
        Assert.Empty(restored.PlanEffects());
        Assert.Equal(1, workers.AliveCount);
    }

    [Theory]
    [InlineData(BackendObservationKind.Pending)]
    [InlineData(BackendObservationKind.Running)]
    public void LateWorkerObservationsCannotResurrectAnEndedWorker(BackendObservationKind staleKind)
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, ResourceVector.None, dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 1));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var start = Assert.Single(coordinator.PlanEffects().OfType<StartWorker>());
        coordinator.WorkerStarted(attempt.Id, start.OperationId, new BackendReceipt("worker"), true);

        coordinator.ObserveWorkers(attempt.Id,
            [new WorkerObservation("worker", BackendObservationKind.Canceled)]);
        coordinator.ObserveWorkers(attempt.Id,
            [new WorkerObservation("worker", staleKind)]);

        Assert.Equal(0, attempt.WorkerGroup.AliveCount);
        Assert.Equal(WorkerPhase.Ended, Assert.Single(attempt.WorkerGroup.Workers).Phase);
    }

    [Fact]
    public void ManagerFinalizationWaitsForWorkerCleanup()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 2));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var start = Assert.Single(coordinator.PlanEffects());
        var workerStart = Assert.IsType<StartWorker>(start);
        coordinator.WorkerStarted(
            attempt.Id, workerStart.OperationId, new BackendReceipt("worker"), isRunning: true);

        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        var completion = coordinator.PlanEffects();

        var cancel = Assert.Single(completion.OfType<CancelWorkers>());
        Assert.Equal("worker", Assert.Single(cancel.Receipts).Id);
        Assert.DoesNotContain(completion, effect => effect is FinalizeExecution);

        coordinator.ObserveWorkers(attempt.Id,
            [new WorkerObservation("worker", BackendObservationKind.Canceled)]);
        var cleanup = coordinator.PlanEffects();

        Assert.Single(cleanup, effect => effect is FinalizeExecution);
    }

    [Fact]
    public void UntraceableWorkerPreventsSuccessfulParentCompletion()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 2));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var workerStart = Assert.IsType<StartWorker>(Assert.Single(coordinator.PlanEffects()));
        coordinator.WorkerStartIndeterminate(
            attempt.Id, workerStart.OperationId, "submission timed out");

        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Running));
        Assert.Equal(ExecutionHealth.Indeterminate, attempt.Health);
        Assert.Contains("worker submission", attempt.HealthDetail);

        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        var completion = coordinator.PlanEffects();
        var finalization = Assert.IsType<FinalizeExecution>(Assert.Single(completion));

        Assert.Equal(ExecutionOutcome.Interrupted, finalization.Outcome);
        Assert.Equal(ExecutionHealth.Indeterminate, attempt.Health);
        Assert.Contains("cannot prove", attempt.HealthDetail);

        coordinator.FinalizationCompleted(attempt.Id);

        Assert.Equal(ExecutionPhase.Interrupted, attempt.Phase);
        Assert.Contains(attempt.History, entry => entry.Detail == attempt.HealthDetail);
    }

    [Fact]
    public void UnknownWorkerStartAfterParentCompletionInterruptsTheParent()
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, new ResourceVector(1, 1, 0), dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 2));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var workerStart = Assert.IsType<StartWorker>(Assert.Single(coordinator.PlanEffects()));

        coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        Assert.DoesNotContain(coordinator.PlanEffects(), effect => effect is FinalizeExecution);
        coordinator.WorkerStartIndeterminate(
            attempt.Id, workerStart.OperationId, "submission timed out");
        var effects = coordinator.PlanEffects();

        var finalization = Assert.IsType<FinalizeExecution>(Assert.Single(effects));
        Assert.Equal(ExecutionOutcome.Interrupted, finalization.Outcome);
        coordinator.FinalizationCompleted(attempt.Id);
        Assert.Equal(ExecutionPhase.Interrupted, attempt.Phase);
    }

    [Fact]
    public void RecoveryMakesReceiptlessWorkerStartsExplicitlyUntraceable()
    {
        var managerQueue = new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler);
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([managerQueue, workerQueue]);
        var attempt = original.RequestRun(
            Job(1), 1, ResourceVector.None, dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 2));
        original.PreparationCompleted(attempt.Id);
        original.PlanEffects();
        original.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        Assert.IsType<StartWorker>(Assert.Single(original.PlanEffects()));

        var restored = new ExecutionCoordinator([managerQueue, workerQueue]);
        restored.Restore(original.CreateSnapshot());
        restored.Recover();
        var copy = Assert.Single(restored.Attempts);

        Assert.Equal(WorkerPhase.Indeterminate, Assert.Single(copy.WorkerGroup.Workers).Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, copy.Health);

        restored.Observe(copy.Id, new BackendObservation(BackendObservationKind.Succeeded));
        var completion = restored.PlanEffects();
        Assert.Equal(
            ExecutionOutcome.Interrupted,
            Assert.IsType<FinalizeExecution>(Assert.Single(completion)).Outcome);
    }

    [Fact]
    public void RecoveryResumesWorkerCancellationBeforeFinalization()
    {
        var managerQueue = new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler);
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([managerQueue, workerQueue]);
        var attempt = original.RequestRun(
            Job(1), 1, ResourceVector.None, dependenciesReady: true,
            new WorkerGroupRequest(2, DesiredCount: 1, SubmissionLimit: 2));
        original.PreparationCompleted(attempt.Id);
        original.PlanEffects();
        original.StartCompleted(attempt.Id, new BackendReceipt("manager"), isRunning: true);
        var workerStart = Assert.IsType<StartWorker>(Assert.Single(original.PlanEffects()));
        original.WorkerStarted(
            attempt.Id, workerStart.OperationId, new BackendReceipt("worker"), isRunning: true);
        original.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        Assert.Single(original.PlanEffects(), effect => effect is CancelWorkers);

        var restored = new ExecutionCoordinator([managerQueue, workerQueue]);
        restored.Restore(original.CreateSnapshot());
        restored.Recover();
        var effects = restored.PlanEffects();

        Assert.Single(effects, effect => effect is CancelWorkers);
        Assert.DoesNotContain(effects, effect => effect is FinalizeExecution);
        Assert.Equal(ExecutionPhase.Finalizing, Assert.Single(restored.Attempts).Phase);
    }

    [Fact]
    public void BackendFailureDetailSurvivesFinalization()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            Job(1), -1, ResourceVector.None, dependenciesReady: true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("local"), isRunning: true);
        coordinator.Observe(
            attempt.Id, new BackendObservation(BackendObservationKind.Failed, "exit code 17"));

        coordinator.FinalizationCompleted(attempt.Id);

        Assert.Equal(ExecutionPhase.Failed, attempt.Phase);
        Assert.Equal("exit code 17", attempt.History.Last().Detail);
    }

    [Fact]
    public void FinalizeOnlyAttemptUsesTheSameLifecycleAuthority()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);

        var requested = coordinator.RequestFinalization(Job(1), -1);

        Assert.Equal(ExecutionPurpose.FinalizeOnly, requested.Purpose);
        Assert.Equal(ExecutionPhase.Finalizing, requested.Phase);
        Assert.Single(coordinator.PlanEffects(), effect => effect is FinalizeExecution);

        coordinator.FinalizationCompleted(requested.Id);

        Assert.Equal(ExecutionPhase.Succeeded, requested.Phase);
    }

    [Fact]
    public void RecoveryInterruptsAnAmbiguousExternalStartWithoutResubmittingIt()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([external]);
        var attempt = original.RequestRun(
            Job(1), 4, new ResourceVector(1, 1, 0), dependenciesReady: true);
        original.PreparationCompleted(attempt.Id);
        original.PlanEffects();

        var restored = new ExecutionCoordinator([external]);
        restored.Restore(original.CreateSnapshot());
        restored.Recover();
        var effects = restored.PlanEffects();
        var copy = Assert.Single(restored.Attempts);

        Assert.Empty(effects);
        Assert.Equal(ExecutionPhase.Interrupted, copy.Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, copy.Health);
        Assert.Contains(copy.History, entry => entry.Detail == copy.HealthDetail);
    }

    [Fact]
    public void RecoveryOfAcceptedExternalStartAdvancesFifo()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var original = new ExecutionCoordinator([external]);
        var first = original.RequestRun(
            Job(1), 4, ResourceVector.None, dependenciesReady: true);
        var second = original.RequestRun(
            Job(2), 4, ResourceVector.None, dependenciesReady: true);
        original.PreparationCompleted(first.Id);
        original.PreparationCompleted(second.Id);
        original.PlanEffects();
        var snapshot = original.CreateSnapshot();
        snapshot = snapshot with
        {
            Attempts = snapshot.Attempts.Select(attempt => attempt.Id == first.Id
                    ? attempt with { Receipt = new BackendReceipt("scheduler-42") }
                    : attempt)
                .ToArray()
        };

        var restored = new ExecutionCoordinator([external]);
        restored.Restore(snapshot);
        restored.Recover();
        var effects = restored.PlanEffects();

        Assert.Equal(ExecutionPhase.Pending, restored.CurrentAttempt(first.Job).Phase);
        Assert.Single(effects, effect =>
            effect is StartExecution { AttemptId: var id } && id == second.Id);
    }

    [Fact]
    public void UnknownSubmissionOutcomeInterruptsTheAttemptAndAdvancesFifo()
    {
        var external = new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([external]);
        var first = coordinator.RequestRun(
            Job(1), 4, ResourceVector.None, dependenciesReady: true);
        var second = coordinator.RequestRun(
            Job(2), 4, ResourceVector.None, dependenciesReady: true);
        coordinator.PreparationCompleted(first.Id);
        coordinator.PreparationCompleted(second.Id);
        coordinator.PlanEffects();

        coordinator.StartIndeterminate(first.Id, "submission timed out");
        var effects = coordinator.PlanEffects();

        Assert.Equal(ExecutionPhase.Interrupted, first.Phase);
        Assert.Equal(ExecutionHealth.Indeterminate, first.Health);
        Assert.Single(effects, effect =>
            effect is StartExecution { AttemptId: var id } && id == second.Id);
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
            Job(1), 4, new ResourceVector(1, 1, 0), dependenciesReady: true);
        original.PreparationCompleted(attempt.Id);
        original.PlanEffects();
        original.StartCompleted(attempt.Id, new BackendReceipt("42"), isRunning: false);

        var restored = new ExecutionCoordinator([]);
        restored.Restore(original.CreateSnapshot());

        var copy = Assert.Single(restored.Attempts);
        Assert.Equal("42", copy.Receipt.Id);
        restored.Recover();
        Assert.Empty(restored.PlanEffects());
    }

    private static (ExecutionCoordinator Coordinator, ExecutionAttempt Attempt) CreateRunningPool(
        int desiredCount, int submissionLimit)
    {
        var workerQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.ExternalScheduler);
        var coordinator = new ExecutionCoordinator([LocalQueue, workerQueue]);
        var attempt = coordinator.RequestRun(Job(1), -1, ResourceVector.None, true,
            new WorkerGroupRequest(2, desiredCount, submissionLimit));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), true);
        foreach (var start in coordinator.PlanEffects().OfType<StartWorker>())
            coordinator.WorkerStarted(attempt.Id, start.OperationId,
                new BackendReceipt(start.OperationId.ToString()), true);
        return (coordinator, attempt);
    }
}
