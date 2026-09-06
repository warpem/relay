using System.Collections.Concurrent;
using Serilog;

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
    private readonly Dictionary<Guid, ProjectionFingerprint> _projected = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<EffectKey, ExecutionEffect> _pendingEffects = new();
    private readonly ILogger _logger = Log.ForContext<ExecutionRuntime>();
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
            bool dependenciesReady;
            try
            {
                dependenciesReady = _operations.DependenciesReady(attempt);
            }
            catch (Exception exception)
            {
                await ApplyEffectsAsync(
                    () => _coordinator.DependencyCheckFailed(
                        attempt.Id,
                        $"Dependency readiness check failed: {exception.Message}"),
                    cancellationToken);
                return;
            }

            if (dependenciesReady)
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
            if (_pendingEffects.Count == 0 && !HasPendingProjections())
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
            if (effects.Count > 0)
                effects = await CommitAsync(effects, cancellationToken);
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
        foreach (var effect in effects)
            _pendingEffects.TryAdd(EffectKey.For(effect), effect);

        var snapshot = _coordinator.CreateSnapshot();
        await _stateStore.SaveAsync(snapshot, cancellationToken);
        Volatile.Write(ref _latestSnapshot, snapshot);

        var latestAttempts = LatestAttempts(snapshot);
        var latestIds = latestAttempts.Select(attempt => attempt.Id).ToHashSet();
        foreach (var stale in _projected.Keys.Where(id => !latestIds.Contains(id)).ToArray())
            _projected.Remove(stale);

        foreach (var attempt in latestAttempts)
        {
            var fingerprint = ProjectionFingerprint.For(attempt);
            if (_projected.TryGetValue(attempt.Id, out var current) && current == fingerprint)
                continue;

            try
            {
                await _projectAttempt(attempt);
                _projected[attempt.Id] = fingerprint;
            }
            catch (Exception exception)
            {
                _logger.Error(
                    exception,
                    "Could not project execution state for attempt {AttemptId}",
                    attempt.Id);
            }
        }

        var projectedIds = latestAttempts
            .Where(attempt =>
                _projected.TryGetValue(attempt.Id, out var fingerprint) &&
                fingerprint == ProjectionFingerprint.For(attempt))
            .Select(attempt => attempt.Id)
            .ToHashSet();
        var committedEffects = _pendingEffects
            .Where(pair => projectedIds.Contains(pair.Value.AttemptId))
            .Select(pair => pair.Value)
            .ToArray();
        foreach (var effect in committedEffects)
            _pendingEffects.Remove(EffectKey.For(effect));
        return committedEffects;
    }

    private bool HasPendingProjections()
    {
        foreach (var attempt in LatestAttempts(Volatile.Read(ref _latestSnapshot)))
            if (!_projected.TryGetValue(attempt.Id, out var fingerprint) ||
                fingerprint != ProjectionFingerprint.For(attempt))
                return true;

        return false;
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
        catch (Exception exception)
        {
            _logger.Error(
                exception,
                "Execution effect {EffectType} failed for attempt {AttemptId}",
                effect.GetType().Name,
                effect.AttemptId);
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
            case ActivateExecution activate:
                await ActivateAsync(activate, cancellationToken);
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
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.PreparationFailed(attemptId, exception.Message),
                CancellationToken.None);
            return;
        }
        finally
        {
            _preparations.TryRemove(attemptId, out _);
        }

        await ApplyEffectsAsync(
            () => _coordinator.PreparationCompleted(attemptId), CancellationToken.None);
    }

    private async Task StartAsync(StartExecution effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        BackendStartResult started;
        try
        {
            started = await _operations.StartAsync(
                attempt, effect.GpuIndices, cancellationToken);
        }
        catch (IndeterminateBackendStartException exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.StartIndeterminate(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.StartFailed(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }

        await ApplyEffectsAsync(
            () => _coordinator.StartCompleted(
                effect.AttemptId,
                started.Receipt,
                started.IsRunning,
                started.RequiresActivation),
            CancellationToken.None);
    }

    private async Task ActivateAsync(
        ActivateExecution effect,
        CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        try
        {
            await _operations.ActivateAsync(attempt, cancellationToken);
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.ActivationFailed(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }

        await ApplyEffectsAsync(
            () => _coordinator.ActivationCompleted(effect.AttemptId),
            CancellationToken.None);
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

        BackendObservation observation;
        try
        {
            observation = await _operations.CancelAsync(
                attempt, effect.Receipt, cancellationToken);
        }
        catch (Exception exception)
        {
            observation = new BackendObservation(
                BackendObservationKind.Indeterminate,
                exception.Message);
        }

        await ApplyEffectsAsync(
            () => _coordinator.Observe(effect.AttemptId, observation),
            CancellationToken.None);
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

        BackendStartResult started;
        try
        {
            started = await _operations.StartWorkerAsync(
                attempt, effect.OperationId, cancellationToken);
        }
        catch (IndeterminateBackendStartException exception)
        {
            _logger.Warning(
                exception,
                "Worker submission outcome is unknown for attempt {AttemptId}",
                effect.AttemptId);
            await ApplyEffectsAsync(
                () => _coordinator.WorkerStartIndeterminate(
                    effect.AttemptId, effect.OperationId, exception.Message),
                CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            await ApplyEffectsAsync(
                () => _coordinator.WorkerStartFailed(
                    effect.AttemptId, effect.OperationId, exception.Message),
                CancellationToken.None);
            return;
        }

        await ApplyEffectsAsync(
            () => _coordinator.WorkerStarted(
                effect.AttemptId, effect.OperationId, started.Receipt, started.IsRunning),
            CancellationToken.None);
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
        catch (Exception exception)
        {
            _logger.Warning(
                exception,
                "Could not cancel workers for attempt {AttemptId}",
                effect.AttemptId);
        }
    }

    private async Task ObserveAttemptAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        BackendObservation observation;
        try
        {
            observation = await _operations.ObserveAsync(attempt, cancellationToken);
        }
        catch (Exception exception)
        {
            observation = new BackendObservation(
                BackendObservationKind.Unreachable,
                exception.Message);
        }

        await ApplyEffectsAsync(
            () => _coordinator.Observe(attempt.Id, observation), cancellationToken);
    }

    private async Task ObserveWorkersAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<BackendReceipt> receipts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkerObservation> observations;
        try
        {
            observations = await _operations.ObserveWorkersAsync(
                attempt, receipts, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.Warning(
                exception,
                "Could not observe workers for attempt {AttemptId}",
                attempt.Id);
            return;
        }

        await ApplyEffectsAsync(
            () => _coordinator.ObserveWorkers(attempt.Id, observations), cancellationToken);
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
        catch (Exception exception)
        {
            _logger.Warning(
                exception,
                "Could not track progress for attempt {AttemptId}",
                attempt.Id);
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

    private readonly record struct ProjectionFingerprint(
        ExecutionPhase Phase,
        string ReceiptId,
        int WorkersAlive,
        int WorkersRunning,
        int WorkerSubmissions)
    {
        public static ProjectionFingerprint For(ExecutionAttemptSnapshot attempt) => new(
            attempt.Phase,
            attempt.Receipt?.Id,
            attempt.WorkerGroup?.Workers.Count(worker => worker.Phase != WorkerPhase.Ended) ?? 0,
            attempt.WorkerGroup?.Workers.Count(worker => worker.Phase == WorkerPhase.Running) ?? 0,
            attempt.WorkerGroup?.TotalSubmissions ?? 0);
    }
}
