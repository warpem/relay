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
        await WaitUntilAsync(() => runtime.Attempts.Single().Phase == ExecutionPhase.Canceled);

        Assert.Contains(ExecutionPhase.Cancelling, projected);
        Assert.Equal(ExecutionPhase.Canceled, runtime.Attempts.Single().Phase);
        Assert.Equal(0, operations.StartCalls);
    }

    [Fact]
    public async Task CommitFailureAfterManagedHandshakeDoesNotMisclassifyTheStart()
    {
        var store = new RecordingStateStore();
        var operations = new FakeOperations
        {
            StartResult = new BackendStartResult(
                new BackendReceipt("runner"),
                IsRunning: false,
                RequiresActivation: true),
            OnStart = () => store.FailNextSave = true
        };
        await using var runtime = new ExecutionRuntime(
            new ExecutionCoordinator([LocalQueue]),
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
                new JobAddress(2, 3, 4), -1, new ResourceVector(1, 2, 0), true).Attempt;
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

        public FakeOperations(ConcurrentQueue<string> events = null)
        {
            _events = events;
        }

        public bool HoldPreparation { get; set; }
        public int PrepareCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public Action OnStart { get; init; }
        public BackendStartResult StartResult { get; init; }

        public bool DependenciesReady(ExecutionAttemptSnapshot attempt) => true;

        public async Task PrepareAsync(
            ExecutionAttemptSnapshot attempt,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
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
            Task.FromResult(new BackendObservation(BackendObservationKind.Running));

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
            CancellationToken cancellationToken) =>
            Task.FromResult(new BackendObservation(BackendObservationKind.Canceled));

        public Task FinalizeAsync(
            ExecutionAttemptSnapshot attempt,
            ExecutionOutcome outcome,
            CancellationToken cancellationToken) => Task.CompletedTask;

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

        public Task TrackProgressAsync(
            ExecutionAttemptSnapshot attempt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
