using System.Collections.Concurrent;

namespace Refund.JobExecution;

public sealed class ExecutionRuntime : IAsyncDisposable
{
    private readonly ExecutionCoordinator _coordinator;
    private readonly IExecutionOperations _operations;
    private readonly IExecutionStateStore _stateStore;
    private readonly Func<ExecutionAttemptSnapshot, Task> _projectAttempt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly ConcurrentDictionary<EffectKey, byte> _runningEffects = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _preparations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ExecutionEffect> _uncommittedEffects = new();
    private ExecutionCoordinatorSnapshot _latestSnapshot;
    private bool _initialized;
    private bool _stopping;

    public ExecutionRuntime(
        ExecutionCoordinator coordinator,
        IExecutionOperations operations,
        IExecutionStateStore stateStore,
        Func<ExecutionAttemptSnapshot, Task> projectAttempt)
    {
        _coordinator = coordinator;
        _operations = operations;
        _stateStore = stateStore;
        _projectAttempt = projectAttempt;
        _latestSnapshot = coordinator.CreateSnapshot();
    }

    public IReadOnlyList<ExecutionAttemptSnapshot> Attempts =>
        Volatile.Read(ref _latestSnapshot).Attempts;

    public IReadOnlyList<ExecutionAttemptSnapshot> ActiveAttempts(int queueId) => Attempts
        .Where(attempt => attempt.QueueId == queueId && !attempt.Phase.IsTerminal())
        .OrderBy(attempt => attempt.EnqueueSequence ?? long.MinValue)
        .ThenBy(attempt => attempt.CreatedAt)
        .ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExecutionEffect> effects;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            var snapshot = await _stateStore.LoadAsync(cancellationToken);
            if (snapshot != null)
                _coordinator.Restore(snapshot);

            effects = await CommitAsync(_coordinator.Recover(), cancellationToken);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(effects);
    }

    public async Task<ExecutionAttemptSnapshot> RequestRunAsync(
        JobAddress job,
        int queueId,
        ResourceVector resources,
        bool dependenciesReady,
        WorkerGroupRequest workerGroup = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        Guid attemptId = await MutateAsync(
            () => _coordinator.RequestRun(
                job, queueId, resources, dependenciesReady, workerGroup),
            result => result.Effects,
            result => result.Attempt.Id,
            cancellationToken);
        return FindAttempt(attemptId);
    }

    public async Task<ExecutionAttemptSnapshot> RequestFinalizationAsync(
        JobAddress job,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        Guid attemptId = await MutateAsync(
            () => _coordinator.RequestFinalization(job, queueId),
            result => result.Effects,
            result => result.Attempt.Id,
            cancellationToken);
        return FindAttempt(attemptId);
    }

    public async Task RequestCancelAsync(
        JobAddress job,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await ApplyEffectsAsync(
            () =>
            {
                var attempt = _coordinator.CurrentAttempt(job);
                return attempt == null
                    ? Array.Empty<ExecutionEffect>()
                    : _coordinator.RequestCancel(attempt.Id);
            },
            cancellationToken);
    }

    public async Task ResizeWorkerGroupAsync(
        JobAddress job,
        int desiredCount,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await ApplyEffectsAsync(
            () =>
            {
                var attempt = _coordinator.CurrentAttempt(job);
                return attempt == null
                    ? Array.Empty<ExecutionEffect>()
                    : _coordinator.ResizeWorkerGroup(attempt.Id, desiredCount);
            },
            cancellationToken);
    }

    public async Task UpsertQueueAsync(
        ExecutionQueuePolicy queue,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _coordinator.UpsertQueue(queue);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveQueueAsync(
        int queueId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _coordinator.RemoveQueue(queueId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (!await _tickGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            await FlushPendingAsync(cancellationToken);
            await DispatchReconciliationAsync(cancellationToken);

            var tasks = Attempts
                .Where(attempt => !attempt.Phase.IsTerminal())
                .Select(attempt => TickAttemptAsync(attempt, cancellationToken));
            await Task.WhenAll(tasks);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping)
            return;

        _stopping = true;
        foreach (var cancellation in _preparations.Values)
            cancellation.Cancel();

        await _operations.ShutdownAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _coordinator.CreateSnapshot();
            await _stateStore.SaveAsync(snapshot, cancellationToken);
            Volatile.Write(ref _latestSnapshot, snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();
        _tickGate.Dispose();
        foreach (var cancellation in _preparations.Values)
            cancellation.Dispose();
    }

    private async Task TickAttemptAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.Phase == ExecutionPhase.WaitingForDependencies)
        {
            if (_operations.DependenciesReady(attempt))
                await ApplyEffectsAsync(
                    () => _coordinator.DependenciesSatisfied(attempt.Id), cancellationToken);
            return;
        }

        if (attempt.Receipt != null &&
            attempt.Phase is ExecutionPhase.Pending
                or ExecutionPhase.Running
                or ExecutionPhase.Cancelling)
            await ObserveAttemptAsync(attempt, cancellationToken);

        var current = FindAttempt(attempt.Id);
        if (current == null || current.Phase.IsTerminal())
            return;

        if (current.Phase == ExecutionPhase.Running)
            _ = RunMaintenanceAsync(current, cancellationToken);

        var workerReceipts = current.WorkerGroup?.Workers
            .Where(worker => worker.Receipt != null && worker.Phase != WorkerPhase.Ended)
            .Select(worker => worker.Receipt)
            .ToArray();
        if (workerReceipts is { Length: > 0 })
            await ObserveWorkersAsync(current, workerReceipts, cancellationToken);
    }

    private async Task<TResult> MutateAsync<TCommand, TResult>(
        Func<TCommand> command,
        Func<TCommand, IReadOnlyList<ExecutionEffect>> effects,
        Func<TCommand, TResult> result,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionEffect> committedEffects;
        TCommand commandResult;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            commandResult = command();
            committedEffects = await CommitAsync(effects(commandResult), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(committedEffects);
        return result(commandResult);
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionEffect> effects;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_uncommittedEffects.Count == 0)
                return;

            effects = await CommitAsync(Array.Empty<ExecutionEffect>(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(effects);
    }

    private async Task DispatchReconciliationAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionEffect> effects;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            effects = _coordinator.ReconcileActiveEffects();
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(effects);
    }

    private async Task ApplyEffectsAsync(
        Func<IReadOnlyList<ExecutionEffect>> command,
        CancellationToken cancellationToken)
    {
        await MutateAsync(command, effects => effects, _ => true, cancellationToken);
    }

    private async Task<IReadOnlyList<ExecutionEffect>> CommitAsync(
        IReadOnlyList<ExecutionEffect> effects,
        CancellationToken cancellationToken)
    {
        _uncommittedEffects.AddRange(effects);
        var snapshot = _coordinator.CreateSnapshot();
        await _stateStore.SaveAsync(snapshot, cancellationToken);
        Volatile.Write(ref _latestSnapshot, snapshot);

        foreach (var attempt in LatestAttempts(snapshot))
            await _projectAttempt(attempt);

        var committedEffects = _uncommittedEffects.ToArray();
        _uncommittedEffects.Clear();
        return committedEffects;
    }

    private static IReadOnlyList<ExecutionAttemptSnapshot> LatestAttempts(
        ExecutionCoordinatorSnapshot snapshot) => snapshot.Attempts
        .GroupBy(attempt => attempt.Job)
        .Select(group => group
            .OrderBy(attempt => attempt.CreatedAt)
            .ThenBy(attempt => attempt.Id)
            .Last())
        .ToArray();

    private void Dispatch(IEnumerable<ExecutionEffect> effects)
    {
        foreach (var effect in effects)
        {
            var key = EffectKey.For(effect);
            if (!_runningEffects.TryAdd(key, 0))
                continue;

            _ = ExecuteEffectAndReleaseAsync(effect, key, _lifetime.Token);
        }
    }

    private async Task ExecuteEffectAndReleaseAsync(
        ExecutionEffect effect,
        EffectKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteEffectAsync(effect, cancellationToken);
        }
        finally
        {
            _runningEffects.TryRemove(key, out _);
        }
    }

    private async Task ExecuteEffectAsync(
        ExecutionEffect effect,
        CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case PrepareExecution prepare:
                await PrepareAsync(prepare.AttemptId, cancellationToken);
                break;
            case StartExecution start:
                await StartAsync(start, cancellationToken);
                break;
            case CancelExecution cancel:
                await CancelAsync(cancel, cancellationToken);
                break;
            case FinalizeExecution finalize:
                await FinalizeAsync(finalize, cancellationToken);
                break;
            case StartWorker startWorker:
                await StartWorkerAsync(startWorker, cancellationToken);
                break;
            case CancelWorkers cancelWorkers:
                await CancelWorkersAsync(cancelWorkers, cancellationToken);
                break;
        }
    }

    private async Task PrepareAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(attemptId);
        if (attempt == null)
            return;

        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_preparations.TryAdd(attemptId, preparation))
            return;

        try
        {
            await _operations.PrepareAsync(attempt, preparation.Token);
            await ApplyEffectsAsync(
                () => _coordinator.PreparationCompleted(attemptId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.PreparationFailed(attemptId, exception.Message),
                CancellationToken.None);
        }
        finally
        {
            _preparations.TryRemove(attemptId, out _);
        }
    }

    private async Task StartAsync(StartExecution effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        try
        {
            var started = await _operations.StartAsync(
                attempt, effect.GpuIndices, cancellationToken);
            await ApplyEffectsAsync(
                () => _coordinator.StartCompleted(
                    effect.AttemptId, started.Receipt, started.IsRunning),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.StartFailed(effect.AttemptId, exception.Message),
                CancellationToken.None);
        }
    }

    private async Task CancelAsync(CancelExecution effect, CancellationToken cancellationToken)
    {
        if (effect.Receipt == null)
        {
            if (_preparations.TryGetValue(effect.AttemptId, out var preparation))
                preparation.Cancel();
            return;
        }

        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        try
        {
            var observation = await _operations.CancelAsync(
                attempt, effect.Receipt, cancellationToken);
            await ApplyEffectsAsync(
                () => _coordinator.Observe(effect.AttemptId, observation),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.Observe(effect.AttemptId, new BackendObservation(
                    BackendObservationKind.Indeterminate, exception.Message)),
                CancellationToken.None);
        }
    }

    private async Task FinalizeAsync(FinalizeExecution effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        string failure = null;
        try
        {
            await _operations.FinalizeAsync(attempt, effect.Outcome, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }

        await ApplyEffectsAsync(
            () => _coordinator.FinalizationCompleted(effect.AttemptId, failure),
            CancellationToken.None);
    }

    private async Task StartWorkerAsync(StartWorker effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        try
        {
            var started = await _operations.StartWorkerAsync(
                attempt, effect.OperationId, cancellationToken);
            await ApplyEffectsAsync(
                () => _coordinator.WorkerStarted(
                    effect.AttemptId, effect.OperationId, started.Receipt, started.IsRunning),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.WorkerStartFailed(
                    effect.AttemptId, effect.OperationId, exception.Message),
                CancellationToken.None);
        }
    }

    private async Task CancelWorkersAsync(CancelWorkers effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        try
        {
            var observations = await _operations.CancelWorkersAsync(
                attempt, effect.Receipts, cancellationToken);
            await ApplyEffectsAsync(
                () => _coordinator.ObserveWorkers(effect.AttemptId, observations),
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task ObserveAttemptAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            var observation = await _operations.ObserveAsync(attempt, cancellationToken);
            await ApplyEffectsAsync(
                () => _coordinator.Observe(attempt.Id, observation), cancellationToken);
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.Observe(attempt.Id, new BackendObservation(
                    BackendObservationKind.Unreachable, exception.Message)),
                cancellationToken);
        }
    }

    private async Task ObserveWorkersAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<BackendReceipt> receipts,
        CancellationToken cancellationToken)
    {
        try
        {
            var observations = await _operations.ObserveWorkersAsync(
                attempt, receipts, cancellationToken);
            await ApplyEffectsAsync(
                () => _coordinator.ObserveWorkers(attempt.Id, observations), cancellationToken);
        }
        catch
        {
        }
    }

    private async Task RunMaintenanceAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        var key = new EffectKey(nameof(RunMaintenanceAsync), attempt.Id, Guid.Empty, "");
        if (!_runningEffects.TryAdd(key, 0))
            return;

        try
        {
            await _operations.TrackProgressAsync(attempt, cancellationToken);
        }
        catch
        {
        }
        finally
        {
            _runningEffects.TryRemove(key, out _);
        }
    }

    private ExecutionAttemptSnapshot FindAttempt(Guid attemptId) =>
        Attempts.FirstOrDefault(attempt => attempt.Id == attemptId);

    private void ThrowIfUnavailable()
    {
        if (!_initialized)
            throw new InvalidOperationException("Execution runtime has not been initialized.");
        if (_stopping)
            throw new InvalidOperationException("Execution runtime is stopping.");
    }

    private readonly record struct EffectKey(
        string Kind,
        Guid AttemptId,
        Guid OperationId,
        string ReceiptIds)
    {
        public static EffectKey For(ExecutionEffect effect) => effect switch
        {
            StartWorker worker => new(
                nameof(StartWorker), worker.AttemptId, worker.OperationId, ""),
            CancelExecution cancel => new(
                nameof(CancelExecution), cancel.AttemptId, Guid.Empty, cancel.Receipt?.Id ?? ""),
            CancelWorkers workers => new(
                nameof(CancelWorkers), workers.AttemptId, Guid.Empty,
                string.Join("\n", workers.Receipts.Select(receipt => receipt.Id).Order())),
            _ => new(effect.GetType().Name, effect.AttemptId, Guid.Empty, "")
        };
    }
}
