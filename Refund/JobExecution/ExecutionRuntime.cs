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
    private readonly ConcurrentDictionary<EffectKey, TaskCompletionSource> _runningEffects = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _preparations = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _maintenanceGates = new();
    private readonly Dictionary<Guid, ProjectionFingerprint> _projected = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _shutdownSync = new();
    private readonly ILogger _logger = Log.ForContext<ExecutionRuntime>();
    private ExecutionCoordinatorSnapshot _latestSnapshot;
    private Task _shutdownTask;
    private bool _initialized;
    private volatile bool _stopping;

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

            _coordinator.Recover();
            effects = await CommitAsync(cancellationToken);
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
                job, queueId, resources, dependenciesReady, workerGroup).Id,
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
            () => _coordinator.RequestFinalization(job, queueId).Id,
            cancellationToken);
        return FindAttempt(attemptId);
    }

    public async Task RequestCancelAsync(
        JobAddress job,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        Guid? attemptId = await MutateAsync(
            () =>
            {
                var attempt = _coordinator.CurrentAttempt(job);
                if (attempt != null)
                    _coordinator.RequestCancel(attempt.Id);
                return attempt?.Id;
            },
            cancellationToken);

        if (attemptId.HasValue && _preparations.TryGetValue(attemptId.Value, out var preparation))
            preparation.Cancel();
    }

    public async Task ResizeWorkerGroupAsync(
        JobAddress job,
        int desiredCount,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await ApplyAsync(
            () =>
            {
                var attempt = _coordinator.CurrentAttempt(job)
                    ?? throw new InvalidOperationException($"Job {job} has no active execution attempt.");
                _coordinator.ResizeWorkerGroup(attempt.Id, desiredCount);
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
            ThrowIfUnavailable();
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
            ThrowIfUnavailable();
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
            await SynchronizeAsync(cancellationToken);

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

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_shutdownSync)
            return _shutdownTask ??= ShutdownCoreAsync(cancellationToken);
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopping = true;
            _lifetime.Cancel();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var cancellation in _preparations.Values)
            cancellation.Cancel();

        await _operations.ShutdownAsync(cancellationToken);
        try
        {
            await Task.WhenAll(_runningEffects.Values.Select(completion => completion.Task))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.Warning("Execution effects are still settling after the shutdown grace period");
        }

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
        if (_runningEffects.IsEmpty)
            _lifetime.Dispose();
        // A backend result can still arrive after bounded shutdown. Keep the purely
        // managed gates usable so its receipt can be persisted; no wait handles are created.
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
                await ApplyAsync(
                    () => _coordinator.DependencyCheckFailed(
                        attempt.Id,
                        $"Dependency readiness check failed: {exception.Message}"),
                    cancellationToken);
                return;
            }

            if (dependenciesReady)
                await ApplyAsync(
                    () => _coordinator.DependenciesSatisfied(attempt.Id), cancellationToken);
            return;
        }

        if (attempt.Phase == ExecutionPhase.Cancelling &&
            attempt.Receipt == null &&
            _preparations.TryGetValue(attempt.Id, out var preparation))
            preparation.Cancel();

        if (attempt.Receipt != null &&
            attempt.Phase is ExecutionPhase.Pending
                or ExecutionPhase.Running
                or ExecutionPhase.Cancelling
                or ExecutionPhase.Stopping)
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

    private async Task<TResult> MutateAsync<TResult>(
        Func<TResult> command,
        CancellationToken cancellationToken,
        bool backendResult = false)
    {
        IReadOnlyList<ExecutionEffect> committedEffects;
        TResult commandResult;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!backendResult)
                ThrowIfUnavailable();
            commandResult = command();
            committedEffects = await CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(committedEffects);
        return commandResult;
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionEffect> effects;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            effects = await CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Dispatch(effects);
    }

    private async Task ApplyAsync(
        Action command,
        CancellationToken cancellationToken)
    {
        await MutateAsync(
            () =>
            {
                command();
                return true;
            },
            cancellationToken);
    }

    private async Task ApplyResultAsync(Action result, CancellationToken cancellationToken)
    {
        await MutateAsync(
            () =>
            {
                result();
                return true;
            },
            cancellationToken,
            backendResult: true);
    }

    private async Task<IReadOnlyList<ExecutionEffect>> CommitAsync(
        CancellationToken cancellationToken)
    {
        var effects = _stopping ? Array.Empty<ExecutionEffect>() : _coordinator.PlanEffects();
        var snapshot = _coordinator.CreateSnapshot();
        await _stateStore.SaveAsync(snapshot, cancellationToken);
        Volatile.Write(ref _latestSnapshot, snapshot);

        var projectionAttempts = ProjectionAttempts(snapshot);
        var projectionIds = projectionAttempts.Select(attempt => attempt.Id).ToHashSet();
        foreach (var stale in _projected.Keys.Where(id => !projectionIds.Contains(id)).ToArray())
            _projected.Remove(stale);

        foreach (var attempt in projectionAttempts)
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

        var terminalAttempts = snapshot.Attempts
            .Where(attempt => attempt.Phase.IsTerminal())
            .Where(attempt =>
                !projectionIds.Contains(attempt.Id) ||
                _projected.TryGetValue(attempt.Id, out var projected) &&
                projected == ProjectionFingerprint.For(attempt))
            .Select(attempt => attempt.Id)
            .ToArray();
        if (terminalAttempts.Length > 0)
        {
            _coordinator.ForgetTerminalAttempts(terminalAttempts);
            snapshot = _coordinator.CreateSnapshot();
            await _stateStore.SaveAsync(snapshot, cancellationToken);
            Volatile.Write(ref _latestSnapshot, snapshot);
            foreach (var attemptId in terminalAttempts)
            {
                _projected.Remove(attemptId);
                _maintenanceGates.TryRemove(attemptId, out var maintenanceGate);
                maintenanceGate?.Dispose();
            }
        }

        // Claim the complete batch before releasing the coordinator gate. An effect may
        // finish synchronously and trigger another commit before its siblings dispatch.
        return effects.Where(effect => _runningEffects.TryAdd(
            EffectKey.For(effect), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
            .ToArray();
    }

    private static IReadOnlyList<ExecutionAttemptSnapshot> ProjectionAttempts(
        ExecutionCoordinatorSnapshot snapshot) => snapshot.Attempts
        .GroupBy(attempt => attempt.Job)
        .Select(group => group.SingleOrDefault(attempt => !attempt.Phase.IsTerminal()) ?? group
            .OrderBy(attempt => attempt.CreatedAt)
            .ThenBy(attempt => attempt.Id)
            .Last())
        .ToArray();

    private void Dispatch(IEnumerable<ExecutionEffect> effects)
    {
        foreach (var effect in effects)
        {
            var key = EffectKey.For(effect);
            if (_stopping)
            {
                ReleaseEffect(key);
                continue;
            }
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
            try
            {
                await ExecuteEffectAsync(effect, cancellationToken);
            }
            finally
            {
                ReleaseEffect(key);
            }

            // A resize may select more receipts while the previous batch owns its key.
            // Only new receipts trigger follow-up, so a failed command cannot busy-loop.
            if (!_stopping && effect is CancelWorkers canceled &&
                FindAttempt(effect.AttemptId)?.WorkerGroup?.Workers.Any(worker =>
                    worker.Phase == WorkerPhase.Cancelling && worker.Receipt != null &&
                    !canceled.Receipts.Contains(worker.Receipt)) == true)
                await SynchronizeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.Error(
                exception,
                "Execution effect {EffectType} failed for attempt {AttemptId}",
                effect.GetType().Name,
                effect.AttemptId);
        }
    }

    private void ReleaseEffect(EffectKey key)
    {
        if (_runningEffects.TryRemove(key, out var completion))
            completion.TrySetResult();
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
        catch (OperationCanceledException) when (_stopping)
        {
            return;
        }
        catch (Exception exception)
        {
            await ApplyResultAsync(
                () => _coordinator.PreparationFailed(attemptId, exception.Message),
                CancellationToken.None);
            return;
        }
        finally
        {
            _preparations.TryRemove(attemptId, out _);
        }

        await ApplyResultAsync(
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
        catch (OperationCanceledException) when (_stopping)
        {
            return;
        }
        catch (IndeterminateBackendStartException exception)
        {
            await ApplyResultAsync(
                () => _coordinator.StartIndeterminate(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            await ApplyResultAsync(
                () => _coordinator.StartFailed(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }

        await ApplyResultAsync(
            () => _coordinator.StartCompleted(
                effect.AttemptId,
                started.Receipt,
                started.IsRunning),
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
        catch (OperationCanceledException) when (_stopping)
        {
            return;
        }
        catch (IndeterminateBackendStartException exception)
        {
            await ApplyResultAsync(
                () => _coordinator.ActivationIndeterminate(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            await ApplyResultAsync(
                () => _coordinator.ActivationFailed(effect.AttemptId, exception.Message),
                CancellationToken.None);
            return;
        }

        await ApplyResultAsync(
            () => _coordinator.ActivationCompleted(effect.AttemptId),
            CancellationToken.None);
    }

    private async Task CancelAsync(CancelExecution effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        try
        {
            var observation = await _operations.CancelAsync(
                attempt, effect.Receipt, cancellationToken);
            await ApplyResultAsync(
                () => _coordinator.CancelCompleted(effect.AttemptId, observation),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await ApplyResultAsync(
                () => _coordinator.Observe(
                    effect.AttemptId,
                    new BackendObservation(BackendObservationKind.Unreachable, exception.Message)),
                CancellationToken.None);
        }
    }

    private async Task FinalizeAsync(FinalizeExecution effect, CancellationToken cancellationToken)
    {
        var attempt = FindAttempt(effect.AttemptId);
        if (attempt == null)
            return;

        var maintenanceGate = _maintenanceGates.GetOrAdd(
            effect.AttemptId,
            _ => new SemaphoreSlim(1, 1));
        string failure = null;
        await maintenanceGate.WaitAsync(CancellationToken.None);
        try
        {
            await _operations.FinalizeAsync(attempt, effect.Outcome, cancellationToken);
        }
        catch (OperationCanceledException) when (_stopping)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        finally
        {
            maintenanceGate.Release();
            _maintenanceGates.TryRemove(effect.AttemptId, out _);
        }

        await ApplyResultAsync(
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
            await ApplyResultAsync(
                () => _coordinator.WorkerStartIndeterminate(
                    effect.AttemptId, effect.OperationId, exception.Message),
                CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            await ApplyResultAsync(
                () => _coordinator.WorkerStartFailed(
                    effect.AttemptId, effect.OperationId, exception.Message),
                CancellationToken.None);
            return;
        }

        await ApplyResultAsync(
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
            await ApplyResultAsync(
                () => _coordinator.WorkerCancellationCompleted(effect.AttemptId, observations),
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

        await ApplyResultAsync(
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

        await ApplyResultAsync(
            () => _coordinator.ObserveWorkers(attempt.Id, observations), cancellationToken);
    }

    private async Task RunMaintenanceAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        var maintenanceGate = _maintenanceGates.GetOrAdd(
            attempt.Id,
            _ => new SemaphoreSlim(1, 1));
        if (!maintenanceGate.Wait(0))
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
            maintenanceGate.Release();
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
        Guid OperationId)
    {
        public static EffectKey For(ExecutionEffect effect) => effect switch
        {
            StartWorker worker => new(
                nameof(StartWorker), worker.AttemptId, worker.OperationId),
            _ => new(effect.GetType().Name, effect.AttemptId, Guid.Empty)
        };
    }

    private readonly record struct ProjectionFingerprint(
        ExecutionPhase Phase,
        ExecutionHealth Health,
        string HealthDetail,
        string ReceiptId,
        int? WorkerTarget,
        int WorkersAlive,
        int WorkersRunning,
        int WorkersStopping,
        int WorkerSubmissions)
    {
        public static ProjectionFingerprint For(ExecutionAttemptSnapshot attempt) => new(
            attempt.Phase,
            attempt.Health,
            attempt.HealthDetail,
            attempt.Receipt?.Id,
            attempt.WorkerGroup?.DesiredCount,
            attempt.WorkerGroup?.Workers.Count(worker => worker.Phase != WorkerPhase.Ended) ?? 0,
            attempt.WorkerGroup?.Workers.Count(worker => worker.Phase == WorkerPhase.Running) ?? 0,
            attempt.WorkerGroup?.Workers.Count(worker =>
                worker.Phase is WorkerPhase.Cancelling or WorkerPhase.Stopping) ?? 0,
            attempt.WorkerGroup?.TotalSubmissions ?? 0);
    }
}
