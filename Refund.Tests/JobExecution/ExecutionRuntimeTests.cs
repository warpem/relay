using System.Collections.Concurrent;
using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public class ExecutionRuntimeTests
{
    private static readonly ExecutionQueuePolicy LocalQueue =
        new(-1, ExecutionBackendKind.Local, new ResourceVector(1, 8, 0));

    [Fact]
    public async Task PersistsAndProjectsBeforeStartingAnEffect()
    {
        var events = new ConcurrentQueue<string>();
        var operations = new FakeOperations(events) { HoldPreparation = true };
        var store = new RecordingStateStore(events);
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            store,
            attempt =>
            {
                events.Enqueue($"project:{attempt.Phase}");
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        events.Clear();

        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, new ResourceVector(1, 1, 0), true);
        await WaitUntilAsync(() => operations.PrepareCalls == 1);

        Assert.Equal(
            ["save", "project:Preparing", "prepare"],
            events.Take(3));

        operations.ReleasePreparation();
    }

    [Fact]
    public async Task FailedPersistenceDoesNotLaunchAndTheNextTickRetriesTheCommit()
    {
        var operations = new FakeOperations();
        var store = new RecordingStateStore { FailNextSave = false };
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            store,
            _ => Task.CompletedTask);
        await runtime.InitializeAsync();
        store.FailNextSave = true;

        await Assert.ThrowsAsync<IOException>(() => runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, new ResourceVector(1, 1, 0), true));

        Assert.Equal(0, operations.PrepareCalls);

        await runtime.TickAsync();
        await WaitUntilAsync(() => operations.PrepareCalls == 1);

        Assert.True(store.SaveCalls >= 3);
    }

    [Fact]
    public async Task RepeatedReconciliationDoesNotDuplicateAnInFlightEffect()
    {
        var operations = new FakeOperations { HoldPreparation = true };
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            new RecordingStateStore(),
            _ => Task.CompletedTask);
        await runtime.InitializeAsync();

        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, new ResourceVector(1, 1, 0), true);
        await WaitUntilAsync(() => operations.PrepareCalls == 1);

        await runtime.TickAsync();
        await runtime.TickAsync();

        Assert.Equal(1, operations.PrepareCalls);
        operations.ReleasePreparation();
    }

    [Fact]
    public async Task CancelDuringPreparationSettlesAsCanceled()
    {
        var operations = new FakeOperations { HoldPreparation = true };
        var projected = new ConcurrentQueue<ExecutionPhase>();
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            new RecordingStateStore(),
            attempt =>
            {
                projected.Enqueue(attempt.Phase);
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        var address = new JobAddress(1, 1, 1);
        await runtime.RequestRunAsync(
            address, -1, new ResourceVector(1, 1, 0), true);
        await WaitUntilAsync(() => operations.PrepareCalls == 1);

        await runtime.RequestCancelAsync(address);
        await WaitUntilAsync(() => projected.Contains(ExecutionPhase.Canceled));

        Assert.Contains(ExecutionPhase.Cancelling, projected);
        Assert.Empty(runtime.Attempts);
        Assert.Equal(0, operations.StartCalls);
    }

    [Fact]
    public async Task CommitFailureAfterManagedHandshakeDoesNotMisclassifyTheStart()
    {
        var managedQueue = LocalQueue with { BackendKind = ExecutionBackendKind.Managed };
        var store = new RecordingStateStore();
        var operations = new FakeOperations
        {
            StartResult = new BackendStartResult(
                new BackendReceipt("runner"),
                IsRunning: false),
            OnStart = () => store.FailNextSave = true
        };
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([managedQueue]),
            operations,
            store,
            _ => Task.CompletedTask);
        await runtime.InitializeAsync();
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, new ResourceVector(1, 1, 0), true);
        await WaitUntilAsync(() => operations.StartCalls == 1);
        await WaitUntilAsync(() => store.FailNextSave == false);

        await runtime.TickAsync();
        await WaitUntilAsync(() => operations.ActivateCalls == 1);
        await WaitUntilAsync(() => runtime.Attempts.Single().Phase == ExecutionPhase.Running);

        Assert.Equal(1, operations.StartCalls);
        Assert.Equal(ExecutionPhase.Running, runtime.Attempts.Single().Phase);
    }

    [Fact]
    public async Task JsonStoreRoundTripsTheCoordinatorSnapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(directory, "execution.json");
        try
        {
            var coordinator = new ExecutionCoordinator([LocalQueue]);
            var attempt = coordinator.RequestRun(
                new JobAddress(2, 3, 4), -1, new ResourceVector(1, 2, 0), true);
            var store = new JsonExecutionStateStore(path);

            await store.SaveAsync(coordinator.CreateSnapshot(), CancellationToken.None);
            var loaded = await new JsonExecutionStateStore(path).LoadAsync(CancellationToken.None);

            var restored = Assert.Single(loaded.Attempts);
            Assert.Equal(attempt.Id, restored.Id);
            Assert.Equal(attempt.History, restored.History);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnchangedObservationsAreNotProjectedAgain()
    {
        int projectionCount = 0;
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            new FakeOperations(),
            new RecordingStateStore(),
            _ =>
            {
                projectionCount++;
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, new ResourceVector(1, 1, 0), true);
        await WaitUntilAsync(() => runtime.Attempts.Single().Phase == ExecutionPhase.Running);
        await WaitUntilAsync(() => projectionCount >= 3);
        int afterStart = projectionCount;

        await runtime.TickAsync();
        await runtime.TickAsync();

        Assert.Equal(afterStart, projectionCount);
    }

    [Fact]
    public async Task DependencyCheckFailureSettlesInsteadOfRetryingForever()
    {
        var operations = new FakeOperations
        {
            DependenciesReadyHandler = _ => throw new InvalidOperationException("invalid graph")
        };
        var projected = new ConcurrentQueue<ExecutionAttemptSnapshot>();
        var store = new RecordingStateStore();
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            store,
            attempt =>
            {
                projected.Enqueue(attempt);
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, new ResourceVector(1, 1, 0), false);

        await runtime.TickAsync();
        await runtime.TickAsync();

        var attempt = projected.Last(item => item.Phase == ExecutionPhase.Failed);
        Assert.Equal(ExecutionPhase.Failed, attempt.Phase);
        Assert.Single(attempt.History, entry =>
            entry.Detail == "Dependency readiness check failed: invalid graph");
        Assert.Empty(runtime.Attempts);
        Assert.Empty(store.Snapshot.Attempts);
        Assert.Equal(0, operations.PrepareCalls);
    }

    [Fact]
    public async Task TerminalAttemptRemainsDurableUntilProjectionSucceeds()
    {
        var operations = new FakeOperations
        {
            DependenciesReadyHandler = _ => throw new InvalidOperationException("invalid graph")
        };
        var store = new RecordingStateStore();
        bool rejectTerminalProjection = true;
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            store,
            attempt =>
            {
                if (attempt.Phase.IsTerminal() && rejectTerminalProjection)
                    throw new IOException("space unavailable");
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, ResourceVector.None, false);

        await runtime.TickAsync();

        Assert.Equal(ExecutionPhase.Failed, Assert.Single(runtime.Attempts).Phase);
        Assert.Equal(ExecutionPhase.Failed, Assert.Single(store.Snapshot.Attempts).Phase);

        rejectTerminalProjection = false;
        await runtime.TickAsync();

        Assert.Empty(runtime.Attempts);
        Assert.Empty(store.Snapshot.Attempts);
    }

    [Fact]
    public async Task InitializationProjectsAndPrunesStoredTerminalAttempts()
    {
        var coordinator = new ExecutionCoordinator([LocalQueue]);
        var attempt = coordinator.RequestRun(
            new JobAddress(1, 1, 1), -1, ResourceVector.None, true);
        coordinator.PreparationFailed(attempt.Id, "invalid input");
        var store = new RecordingStateStore();
        await store.SaveAsync(coordinator.CreateSnapshot(), CancellationToken.None);
        var projected = new ConcurrentQueue<ExecutionPhase>();
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            new FakeOperations(),
            store,
            snapshot =>
            {
                projected.Enqueue(snapshot.Phase);
                return Task.CompletedTask;
            });

        await runtime.InitializeAsync();

        Assert.Contains(ExecutionPhase.Failed, projected);
        Assert.Empty(runtime.Attempts);
        Assert.Empty(store.Snapshot.Attempts);
    }

    [Fact]
    public async Task ActiveAttemptSupersedesStoredTerminalHistoryForTheSameAddress()
    {
        var externalQueue = new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler);
        var address = new JobAddress(1, 1, 1);
        var coordinator = new ExecutionCoordinator([externalQueue]);
        var oldAttempt = coordinator.RequestRun(address, 1, ResourceVector.None, true);
        coordinator.PreparationFailed(oldAttempt.Id, "old failure");
        var activeAttempt = coordinator.RequestRun(address, 1, ResourceVector.None, false);
        var store = new RecordingStateStore();
        await store.SaveAsync(coordinator.CreateSnapshot(), CancellationToken.None);
        var projected = new ConcurrentQueue<Guid>();
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([externalQueue]),
            new FakeOperations(),
            store,
            snapshot =>
            {
                projected.Enqueue(snapshot.Id);
                return Task.CompletedTask;
            });

        await runtime.InitializeAsync();

        Assert.Equal(activeAttempt.Id, Assert.Single(projected));
        Assert.Equal(activeAttempt.Id, Assert.Single(runtime.Attempts).Id);
        Assert.Equal(activeAttempt.Id, Assert.Single(store.Snapshot.Attempts).Id);
    }

    [Fact]
    public async Task ProjectionFailureDoesNotWithholdExecutionAndIsRetried()
    {
        var firstQueue = new ExecutionQueuePolicy(1, ExecutionBackendKind.Local);
        var secondQueue = new ExecutionQueuePolicy(2, ExecutionBackendKind.Local);
        var operations = new FakeOperations();
        bool failFirstProjection = true;
        int firstProjectionCalls = 0;
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([firstQueue, secondQueue]),
            operations,
            new RecordingStateStore(),
            attempt =>
            {
                if (attempt.Job.JobId == 1)
                {
                    Interlocked.Increment(ref firstProjectionCalls);
                    if (failFirstProjection)
                        throw new IOException("projection unavailable");
                }
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), 1, ResourceVector.None, true);
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 2), 2, ResourceVector.None, true);

        await WaitUntilAsync(() => operations.PreparedJobs.Count == 2);
        Assert.Contains(new JobAddress(1, 1, 1), operations.PreparedJobs);
        Assert.Contains(new JobAddress(1, 1, 2), operations.PreparedJobs);

        int failedProjectionCalls = Volatile.Read(ref firstProjectionCalls);
        failFirstProjection = false;
        await runtime.TickAsync();
        Assert.True(Volatile.Read(ref firstProjectionCalls) > failedProjectionCalls);
    }

    [Fact]
    public async Task AcceptedCancellationIsNotSentAgainWhileWaitingForTerminalEvidence()
    {
        var externalQueue = new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler);
        var operations = new FakeOperations
        {
            StartResult = new BackendStartResult(new BackendReceipt("scheduler-1"), IsRunning: false),
            CancelResult = new BackendObservation(
                BackendObservationKind.Indeterminate,
                "cancellation accepted"),
            ObserveResult = new BackendObservation(BackendObservationKind.Indeterminate)
        };
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([externalQueue]),
            operations,
            new RecordingStateStore(),
            _ => Task.CompletedTask);
        await runtime.InitializeAsync();
        var address = new JobAddress(1, 1, 1);
        await runtime.RequestRunAsync(address, 1, ResourceVector.None, true);
        await WaitUntilAsync(() => runtime.Attempts.Single().Phase == ExecutionPhase.Pending);

        await runtime.RequestCancelAsync(address);
        await WaitUntilAsync(() => runtime.Attempts.Single().Phase == ExecutionPhase.Stopping);
        await runtime.TickAsync();
        await runtime.TickAsync();

        Assert.Equal(1, operations.CancelCalls);
    }

    [Fact]
    public async Task ShutdownClosesAdmissionAndIsIdempotent()
    {
        var operations = new FakeOperations();
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            new RecordingStateStore(),
            _ => Task.CompletedTask);
        await runtime.InitializeAsync();

        await Task.WhenAll(runtime.ShutdownAsync(), runtime.ShutdownAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, ResourceVector.None, true));
        Assert.Equal(1, operations.ShutdownCalls);
    }

    [Fact]
    public async Task FinalizationWaitsForOutstandingProgressTracking()
    {
        var operations = new FakeOperations { HoldMaintenance = true };
        var projected = new ConcurrentQueue<ExecutionPhase>();
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
            operations,
            new RecordingStateStore(),
            attempt =>
            {
                projected.Enqueue(attempt.Phase);
                return Task.CompletedTask;
            });
        await runtime.InitializeAsync();
        await runtime.RequestRunAsync(
            new JobAddress(1, 1, 1), -1, ResourceVector.None, true);
        await WaitUntilAsync(() => runtime.Attempts.Single().Phase == ExecutionPhase.Running);

        await runtime.TickAsync();
        await operations.MaintenanceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        operations.ObserveResult = new BackendObservation(BackendObservationKind.Succeeded);

        await runtime.TickAsync();
        Assert.Equal(0, operations.FinalizeCalls);

        operations.ReleaseMaintenance();
        await WaitUntilAsync(() => projected.Contains(ExecutionPhase.Succeeded));
        Assert.Empty(runtime.Attempts);
        Assert.Equal(1, operations.FinalizeCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordingStateStore : IExecutionStateStore
    {
        private readonly ConcurrentQueue<string> _events;

        public RecordingStateStore(ConcurrentQueue<string> events = null)
        {
            _events = events;
        }

        public bool FailNextSave { get; set; }
        public int SaveCalls { get; private set; }
        public ExecutionCoordinatorSnapshot Snapshot { get; private set; }

        public Task<ExecutionCoordinatorSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task SaveAsync(
            ExecutionCoordinatorSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            _events?.Enqueue("save");
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("disk full");
            }

            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOperations : IExecutionOperations
    {
        private readonly ConcurrentQueue<string> _events;
        private readonly TaskCompletionSource _preparation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _maintenance =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeOperations(ConcurrentQueue<string> events = null)
        {
            _events = events;
        }

        public bool HoldPreparation { get; set; }
        public int PrepareCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int ShutdownCalls { get; private set; }
        public int FinalizeCalls { get; private set; }
        public bool HoldMaintenance { get; set; }
        public Action OnStart { get; init; }
        public BackendStartResult StartResult { get; init; }
        public Func<ExecutionAttemptSnapshot, bool> DependenciesReadyHandler { get; init; }
        public ConcurrentQueue<JobAddress> PreparedJobs { get; } = new();
        public TaskCompletionSource MaintenanceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BackendObservation ObserveResult { get; set; } =
            new(BackendObservationKind.Running);
        public BackendObservation CancelResult { get; init; } =
            new(BackendObservationKind.Canceled);

        public bool DependenciesReady(ExecutionAttemptSnapshot attempt) =>
            DependenciesReadyHandler?.Invoke(attempt) ?? true;

        public async Task PrepareAsync(
            ExecutionAttemptSnapshot attempt,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            PreparedJobs.Enqueue(attempt.Job);
            _events?.Enqueue("prepare");
            if (HoldPreparation)
                await _preparation.Task.WaitAsync(cancellationToken);
        }

        public void ReleasePreparation() => _preparation.TrySetResult();

        public Task<BackendStartResult> StartAsync(
            ExecutionAttemptSnapshot attempt,
            IReadOnlyList<int> gpuIndices,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            OnStart?.Invoke();
            return Task.FromResult(StartResult ?? new BackendStartResult(
                new BackendReceipt(attempt.Id.ToString()), true));
        }

        public Task<BackendObservation> ObserveAsync(
            ExecutionAttemptSnapshot attempt,
            CancellationToken cancellationToken) =>
            Task.FromResult(ObserveResult);

        public Task ActivateAsync(
            ExecutionAttemptSnapshot attempt,
            CancellationToken cancellationToken)
        {
            ActivateCalls++;
            return Task.CompletedTask;
        }

        public Task<BackendObservation> CancelAsync(
            ExecutionAttemptSnapshot attempt,
            BackendReceipt receipt,
            CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.FromResult(CancelResult);
        }

        public Task FinalizeAsync(
            ExecutionAttemptSnapshot attempt,
            ExecutionOutcome outcome,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            return Task.CompletedTask;
        }

        public Task<BackendStartResult> StartWorkerAsync(
            ExecutionAttemptSnapshot attempt,
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BackendStartResult(
                new BackendReceipt(operationId.ToString()), false));

        public Task<IReadOnlyList<WorkerObservation>> ObserveWorkersAsync(
            ExecutionAttemptSnapshot attempt,
            IReadOnlyList<BackendReceipt> receipts,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerObservation>>([]);

        public Task<IReadOnlyList<WorkerObservation>> CancelWorkersAsync(
            ExecutionAttemptSnapshot attempt,
            IReadOnlyList<BackendReceipt> receipts,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerObservation>>(
                receipts.Select(receipt => new WorkerObservation(
                    receipt.Id, BackendObservationKind.Canceled)).ToArray());

        public async Task TrackProgressAsync(
            ExecutionAttemptSnapshot attempt,
            CancellationToken cancellationToken)
        {
            MaintenanceStarted.TrySetResult();
            if (HoldMaintenance)
                await _maintenance.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseMaintenance() => _maintenance.TrySetResult();

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCalls++;
            return Task.CompletedTask;
        }
    }
}
