namespace Refund.JobExecution;

/// <summary>
/// The single mutable authority for execution attempts. This class is deliberately synchronous:
/// callers serialize access to it, persist the returned state, and execute returned effects outside
/// the coordinator. No backend code runs here.
/// </summary>
public sealed class ExecutionCoordinator
{
    private readonly Dictionary<int, ExecutionQueuePolicy> _queues;
    private readonly Dictionary<Guid, ExecutionAttempt> _attempts = new();
    private readonly Dictionary<JobAddress, Guid> _currentAttempts = new();
    private readonly TimeProvider _timeProvider;
    private long _nextSequence;

    public ExecutionCoordinator(IEnumerable<ExecutionQueuePolicy> queues, TimeProvider timeProvider = null)
    {
        _queues = queues.ToDictionary(queue => queue.QueueId);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyCollection<ExecutionAttempt> Attempts => _attempts.Values;

    public void UpsertQueue(ExecutionQueuePolicy queue)
    {
        _queues[queue.QueueId] = queue;
    }

    public void RemoveQueue(int queueId)
    {
        if (_attempts.Values.Any(attempt => attempt.QueueId == queueId && !attempt.IsTerminal))
            throw new InvalidOperationException($"Queue {queueId} still has active attempts.");

        _queues.Remove(queueId);
    }

    public ExecutionCoordinatorSnapshot CreateSnapshot() => new(
        _nextSequence,
        _attempts.Values
            .OrderBy(attempt => attempt.CreatedAt)
            .Select(attempt => attempt.CreateSnapshot())
            .ToArray());

    public void Restore(ExecutionCoordinatorSnapshot snapshot)
    {
        if (_attempts.Count > 0)
            throw new InvalidOperationException("Execution state can only be restored into an empty coordinator.");

        _nextSequence = snapshot.NextSequence;

        foreach (var attemptSnapshot in snapshot.Attempts ?? Array.Empty<ExecutionAttemptSnapshot>())
        {
            if (!_queues.ContainsKey(attemptSnapshot.QueueId))
                _queues.Add(attemptSnapshot.QueueId, new ExecutionQueuePolicy(
                    attemptSnapshot.QueueId,
                    attemptSnapshot.BackendKind,
                    null,
                    attemptSnapshot.BackendConfiguration));

            var attempt = new ExecutionAttempt(attemptSnapshot);
            _attempts.Add(attempt.Id, attempt);

            if (!attempt.IsTerminal)
            {
                if (_currentAttempts.ContainsKey(attempt.Job))
                    throw new InvalidOperationException($"Job {attempt.Job} has multiple active attempts in the snapshot.");

                _currentAttempts.Add(attempt.Job, attempt.Id);
            }
        }

        long highestSequence = _attempts.Values
            .Where(attempt => attempt.EnqueueSequence.HasValue)
            .Select(attempt => attempt.EnqueueSequence.Value)
            .DefaultIfEmpty(0)
            .Max();
        _nextSequence = Math.Max(_nextSequence, highestSequence);
    }

    public ExecutionAttempt CurrentAttempt(JobAddress job)
    {
        return _currentAttempts.TryGetValue(job, out var attemptId)
            ? _attempts[attemptId]
            : null;
    }

    public (ExecutionAttempt Attempt, IReadOnlyList<ExecutionEffect> Effects) RequestRun(
        JobAddress job,
        int queueId,
        ResourceVector resourceRequest,
        bool dependenciesReady,
        WorkerGroupRequest workerGroup = null)
    {
        if (!_queues.TryGetValue(queueId, out var queue))
            throw new InvalidOperationException($"Queue {queueId} does not exist.");

        if (!resourceRequest.IsValid)
            throw new InvalidOperationException("Resource requests cannot contain negative values.");

        ExecutionQueuePolicy workerQueue = null;
        if (workerGroup != null)
        {
            if (!_queues.TryGetValue(workerGroup.QueueId, out workerQueue) ||
                workerQueue.BackendKind != ExecutionBackendKind.ExternalScheduler)
                throw new InvalidOperationException("Worker groups require an external scheduler queue.");
        }

        if (_currentAttempts.TryGetValue(job, out var currentId) && !_attempts[currentId].IsTerminal)
            throw new InvalidOperationException($"Job {job} already has an active attempt.");

        var phase = dependenciesReady
            ? ExecutionPhase.Preparing
            : ExecutionPhase.WaitingForDependencies;
        var attempt = new ExecutionAttempt(
            Guid.NewGuid(), job, queueId, queue.BackendKind,
            queue.BackendConfiguration, workerQueue?.BackendConfiguration,
            ExecutionPurpose.Run,
            resourceRequest, workerGroup, phase, Now());

        _attempts.Add(attempt.Id, attempt);
        _currentAttempts[job] = attempt.Id;

        if (!dependenciesReady)
            return (attempt, Array.Empty<ExecutionEffect>());

        AssignSequence(attempt);
        return (attempt, new ExecutionEffect[] { new PrepareExecution(attempt.Id) });
    }

    public (ExecutionAttempt Attempt, IReadOnlyList<ExecutionEffect> Effects) RequestFinalization(
        JobAddress job,
        int queueId)
    {
        if (!_queues.TryGetValue(queueId, out var queue))
            throw new InvalidOperationException($"Queue {queueId} does not exist.");

        if (_currentAttempts.TryGetValue(job, out var currentId) && !_attempts[currentId].IsTerminal)
            throw new InvalidOperationException($"Job {job} already has an active attempt.");

        var attempt = new ExecutionAttempt(
            Guid.NewGuid(), job, queueId, queue.BackendKind,
            queue.BackendConfiguration, null, ExecutionPurpose.FinalizeOnly,
            ResourceVector.None, null, ExecutionPhase.Finalizing, Now())
        {
            PendingOutcome = ExecutionOutcome.Succeeded,
            FinalizationIssued = true
        };

        _attempts.Add(attempt.Id, attempt);
        _currentAttempts[job] = attempt.Id;
        return (attempt, new ExecutionEffect[]
        {
            new FinalizeExecution(attempt.Id, ExecutionOutcome.Succeeded)
        });
    }

    public IReadOnlyList<ExecutionEffect> DependenciesSatisfied(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase != ExecutionPhase.WaitingForDependencies)
            return Array.Empty<ExecutionEffect>();

        AssignSequence(attempt);
        attempt.TransitionTo(ExecutionPhase.Preparing, Now());
        return new ExecutionEffect[] { new PrepareExecution(attempt.Id) };
    }

    public IReadOnlyList<ExecutionEffect> PreparationCompleted(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Preparing or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        if (attempt.Phase == ExecutionPhase.Cancelling)
        {
            attempt.TransitionTo(ExecutionPhase.Canceled, Now());
            _currentAttempts.Remove(attempt.Job);
            return ReconcileQueue(attempt.QueueId);
        }

        if (attempt.WorkerGroup != null)
            attempt.WorkerGroup.Prepared = true;
        attempt.TransitionTo(ExecutionPhase.Queued, Now());
        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> PreparationFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Preparing or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        var terminal = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionPhase.Canceled
            : ExecutionPhase.Failed;
        attempt.TransitionTo(terminal, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> StartCompleted(
        Guid attemptId,
        BackendReceipt receipt,
        bool isRunning,
        bool requiresActivation = false)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        attempt.Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        attempt.Health = ExecutionHealth.Healthy;
        attempt.HealthDetail = null;

        if (attempt.Phase == ExecutionPhase.Cancelling)
            return new ExecutionEffect[] { new CancelExecution(attempt.Id, receipt) };

        if (requiresActivation)
        {
            if (isRunning)
                throw new InvalidOperationException("An already-running backend cannot require activation.");
            return new ExecutionEffect[] { new ActivateExecution(attempt.Id) };
        }

        attempt.TransitionTo(isRunning ? ExecutionPhase.Running : ExecutionPhase.Pending, Now());

        if (isRunning)
            return ReconcileWorkers(attempt);

        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> ActivationCompleted(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.Receipt == null)
            return Array.Empty<ExecutionEffect>();

        if (attempt.Phase == ExecutionPhase.Cancelling)
            return new ExecutionEffect[] { new CancelExecution(attempt.Id, attempt.Receipt) };
        if (attempt.Phase != ExecutionPhase.Starting)
            return Array.Empty<ExecutionEffect>();

        attempt.TransitionTo(ExecutionPhase.Running, Now());
        return ReconcileWorkers(attempt);
    }

    public IReadOnlyList<ExecutionEffect> ActivationFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        Release(attempt);
        var terminal = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionPhase.Canceled
            : ExecutionPhase.Failed;
        attempt.TransitionTo(terminal, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> StartFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        Release(attempt);
        var terminal = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionPhase.Canceled
            : ExecutionPhase.Failed;
        attempt.TransitionTo(terminal, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> StartIndeterminate(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        Release(attempt);
        attempt.Health = ExecutionHealth.Indeterminate;
        attempt.HealthDetail = detail;
        attempt.TransitionTo(ExecutionPhase.Interrupted, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> Observe(Guid attemptId, BackendObservation observation)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.IsTerminal)
            return Array.Empty<ExecutionEffect>();

        switch (observation.Kind)
        {
            case BackendObservationKind.AbsentFromActiveView:
            case BackendObservationKind.Unreachable:
            case BackendObservationKind.Unparseable:
            case BackendObservationKind.Indeterminate:
                attempt.Health = ExecutionHealth.Indeterminate;
                attempt.HealthDetail = observation.Detail;
                return Array.Empty<ExecutionEffect>();

            case BackendObservationKind.Pending:
                attempt.Health = ExecutionHealth.Healthy;
                attempt.HealthDetail = null;
                if (attempt.Phase == ExecutionPhase.Starting)
                    attempt.TransitionTo(ExecutionPhase.Pending, Now());
                return Array.Empty<ExecutionEffect>();

            case BackendObservationKind.Running:
                attempt.Health = ExecutionHealth.Healthy;
                attempt.HealthDetail = null;
                bool enteredRunning = attempt.Phase is ExecutionPhase.Starting or ExecutionPhase.Pending;
                if (enteredRunning)
                    attempt.TransitionTo(ExecutionPhase.Running, Now());
                if (enteredRunning)
                    return ReconcileWorkers(attempt);
                return Array.Empty<ExecutionEffect>();

            case BackendObservationKind.Succeeded:
                return BeginFinalization(attempt, ExecutionOutcome.Succeeded, observation.Detail);

            case BackendObservationKind.Failed:
                return BeginFinalization(attempt, ExecutionOutcome.Failed, observation.Detail);

            case BackendObservationKind.Canceled:
                return BeginFinalization(attempt, ExecutionOutcome.Canceled, observation.Detail);

            default:
                throw new ArgumentOutOfRangeException(nameof(observation));
        }
    }

    public IReadOnlyList<ExecutionEffect> RequestCancel(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.IsTerminal ||
            attempt.Phase is ExecutionPhase.Cancelling or ExecutionPhase.Finalizing)
            return Array.Empty<ExecutionEffect>();

        if (attempt.Phase is ExecutionPhase.WaitingForDependencies or ExecutionPhase.Queued)
        {
            Release(attempt);
            attempt.TransitionTo(ExecutionPhase.Canceled, Now());
            _currentAttempts.Remove(attempt.Job);
            return ReconcileQueue(attempt.QueueId);
        }

        attempt.TransitionTo(ExecutionPhase.Cancelling, Now());
        return new ExecutionEffect[] { new CancelExecution(attempt.Id, attempt.Receipt) };
    }

    public IReadOnlyList<ExecutionEffect> FinalizationCompleted(Guid attemptId, string failure = null)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.Phase != ExecutionPhase.Finalizing)
            return Array.Empty<ExecutionEffect>();

        var phase = failure != null
            ? ExecutionPhase.Failed
            : attempt.PendingOutcome switch
            {
                ExecutionOutcome.Succeeded => ExecutionPhase.Succeeded,
                ExecutionOutcome.Canceled => ExecutionPhase.Canceled,
                ExecutionOutcome.Interrupted => ExecutionPhase.Interrupted,
                _ => ExecutionPhase.Failed
            };

        attempt.TransitionTo(phase, Now(), failure);
        _currentAttempts.Remove(attempt.Job);
        return ReconcileQueue(attempt.QueueId);
    }

    public IReadOnlyList<ExecutionEffect> InterruptOwnerBoundAttempts()
    {
        var interrupted = _attempts.Values.Where(attempt =>
                !attempt.IsTerminal && attempt.BackendKind != ExecutionBackendKind.ExternalScheduler)
            .ToList();
        var affectedQueues = new HashSet<int>();

        foreach (var attempt in interrupted)
        {
            Release(attempt);
            if (attempt.WorkerGroup?.AliveCount > 0)
            {
                attempt.PendingOutcome = ExecutionOutcome.Interrupted;
                attempt.TransitionTo(ExecutionPhase.Finalizing, Now());
            }
            else
            {
                attempt.TransitionTo(ExecutionPhase.Interrupted, Now());
                _currentAttempts.Remove(attempt.Job);
            }
            affectedQueues.Add(attempt.QueueId);
        }

        var effects = new List<ExecutionEffect>();
        foreach (var attempt in interrupted.Where(attempt => attempt.Phase == ExecutionPhase.Finalizing))
            effects.AddRange(ContinueFinalization(attempt));
        foreach (var queueId in affectedQueues)
            effects.AddRange(ReconcileQueue(queueId));

        return effects;
    }

    private IReadOnlyList<ExecutionEffect> BeginFinalization(
        ExecutionAttempt attempt,
        ExecutionOutcome observedOutcome,
        string detail)
    {
        if (attempt.Phase is not (ExecutionPhase.Starting
            or ExecutionPhase.Pending
            or ExecutionPhase.Running
            or ExecutionPhase.Cancelling))
            return Array.Empty<ExecutionEffect>();

        var outcome = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionOutcome.Canceled
            : observedOutcome;

        Release(attempt);
        attempt.PendingOutcome = outcome;
        attempt.Health = ExecutionHealth.Healthy;
        attempt.HealthDetail = null;
        attempt.TransitionTo(ExecutionPhase.Finalizing, Now(), detail);
        var effects = new List<ExecutionEffect>(ContinueFinalization(attempt));
        effects.AddRange(ReconcileQueue(attempt.QueueId));
        return effects;
    }

    public IReadOnlyList<ExecutionEffect> WorkerStarted(
        Guid attemptId,
        Guid operationId,
        BackendReceipt receipt,
        bool isRunning)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return Array.Empty<ExecutionEffect>();

        var worker = attempt.WorkerGroup.Workers.FirstOrDefault(item => item.OperationId == operationId);
        if (worker is not { Phase: WorkerPhase.Starting or WorkerPhase.Cancelling })
            return Array.Empty<ExecutionEffect>();

        worker.Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        if (worker.Phase == WorkerPhase.Cancelling || attempt.Phase == ExecutionPhase.Finalizing)
        {
            worker.Phase = WorkerPhase.Cancelling;
            return new ExecutionEffect[] { new CancelWorkers(attempt.Id, new[] { receipt }) };
        }

        worker.Phase = isRunning ? WorkerPhase.Running : WorkerPhase.Pending;
        return Array.Empty<ExecutionEffect>();
    }

    public IReadOnlyList<ExecutionEffect> WorkerStartFailed(
        Guid attemptId,
        Guid operationId,
        string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return Array.Empty<ExecutionEffect>();

        var worker = attempt.WorkerGroup.Workers.FirstOrDefault(item => item.OperationId == operationId);
        if (worker is not { Phase: WorkerPhase.Starting or WorkerPhase.Cancelling })
            return Array.Empty<ExecutionEffect>();

        worker.Phase = WorkerPhase.Ended;
        return attempt.Phase == ExecutionPhase.Finalizing
            ? ContinueFinalization(attempt)
            : ReconcileWorkers(attempt);
    }

    public IReadOnlyList<ExecutionEffect> WorkerStartIndeterminate(
        Guid attemptId,
        Guid operationId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return Array.Empty<ExecutionEffect>();

        var worker = attempt.WorkerGroup.Workers.FirstOrDefault(item => item.OperationId == operationId);
        if (worker is not { Phase: WorkerPhase.Starting or WorkerPhase.Cancelling })
            return Array.Empty<ExecutionEffect>();

        worker.Phase = attempt.Phase == ExecutionPhase.Finalizing
            ? WorkerPhase.Ended
            : WorkerPhase.Indeterminate;
        return attempt.Phase == ExecutionPhase.Finalizing
            ? ContinueFinalization(attempt)
            : Array.Empty<ExecutionEffect>();
    }

    public IReadOnlyList<ExecutionEffect> ObserveWorkers(
        Guid attemptId,
        IReadOnlyList<WorkerObservation> observations)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return Array.Empty<ExecutionEffect>();

        var byReceipt = observations
            .GroupBy(observation => observation.ReceiptId)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var worker in attempt.WorkerGroup.Workers.Where(worker => worker.Receipt != null))
        {
            if (!byReceipt.TryGetValue(worker.Receipt.Id, out var observation))
                continue;

            switch (observation.Kind)
            {
                case BackendObservationKind.Pending:
                    if (worker.Phase != WorkerPhase.Cancelling)
                        worker.Phase = WorkerPhase.Pending;
                    break;
                case BackendObservationKind.Running:
                    if (worker.Phase != WorkerPhase.Cancelling)
                        worker.Phase = WorkerPhase.Running;
                    break;
                case BackendObservationKind.Succeeded:
                case BackendObservationKind.Failed:
                case BackendObservationKind.Canceled:
                    worker.Phase = WorkerPhase.Ended;
                    break;
            }
        }

        return attempt.Phase == ExecutionPhase.Finalizing
            ? ContinueFinalization(attempt)
            : ReconcileWorkers(attempt);
    }

    public IReadOnlyList<ExecutionEffect> WorkersCanceled(
        Guid attemptId,
        IReadOnlyCollection<string> receiptIds)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return Array.Empty<ExecutionEffect>();

        var canceled = receiptIds.ToHashSet();
        foreach (var worker in attempt.WorkerGroup.Workers)
            if (worker.Receipt != null && canceled.Contains(worker.Receipt.Id))
                worker.Phase = WorkerPhase.Ended;

        return attempt.Phase == ExecutionPhase.Finalizing
            ? ContinueFinalization(attempt)
            : ReconcileWorkers(attempt);
    }

    public IReadOnlyList<ExecutionEffect> ResizeWorkerGroup(Guid attemptId, int desiredCount)
    {
        if (desiredCount < 0)
            throw new InvalidOperationException("Worker count cannot be negative.");

        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase != ExecutionPhase.Running ||
            attempt.WorkerGroup == null)
            return Array.Empty<ExecutionEffect>();

        attempt.WorkerGroup.DesiredCount = desiredCount;
        return ReconcileWorkers(attempt);
    }

    public IReadOnlyList<ExecutionEffect> Recover()
    {
        var effects = new List<ExecutionEffect>(InterruptOwnerBoundAttempts());
        var queuesToReconcile = new HashSet<int>();

        foreach (var attempt in _attempts.Values.Where(attempt =>
                     !attempt.IsTerminal && attempt.BackendKind == ExecutionBackendKind.ExternalScheduler))
        {
            switch (attempt.Phase)
            {
                case ExecutionPhase.Preparing:
                    effects.Add(new PrepareExecution(attempt.Id));
                    break;
                case ExecutionPhase.Queued:
                    queuesToReconcile.Add(attempt.QueueId);
                    break;
                case ExecutionPhase.Starting when attempt.Receipt == null:
                    Release(attempt);
                    attempt.Health = ExecutionHealth.Indeterminate;
                    attempt.HealthDetail = "Submission outcome is unknown after restart.";
                    attempt.TransitionTo(
                        ExecutionPhase.Interrupted,
                        Now(),
                        attempt.HealthDetail);
                    _currentAttempts.Remove(attempt.Job);
                    queuesToReconcile.Add(attempt.QueueId);
                    break;
                case ExecutionPhase.Starting:
                    attempt.TransitionTo(ExecutionPhase.Pending, Now());
                    break;
                case ExecutionPhase.Cancelling when attempt.Receipt == null:
                    Release(attempt);
                    attempt.Health = ExecutionHealth.Indeterminate;
                    attempt.HealthDetail = "Cancellation could not be resumed without a scheduler receipt.";
                    attempt.TransitionTo(
                        ExecutionPhase.Interrupted,
                        Now(),
                        attempt.HealthDetail);
                    _currentAttempts.Remove(attempt.Job);
                    queuesToReconcile.Add(attempt.QueueId);
                    break;
                case ExecutionPhase.Cancelling when attempt.Receipt != null:
                    effects.Add(new CancelExecution(attempt.Id, attempt.Receipt));
                    break;
                case ExecutionPhase.Finalizing:
                    effects.AddRange(RecoverFinalization(attempt));
                    break;
            }
        }

        foreach (var queueId in queuesToReconcile)
            effects.AddRange(ReconcileQueue(queueId));

        return effects;
    }

    public IReadOnlyList<ExecutionEffect> ReconcileActiveEffects()
    {
        var effects = new List<ExecutionEffect>();
        foreach (var attempt in _attempts.Values.Where(attempt => !attempt.IsTerminal))
        {
            if (attempt.Phase == ExecutionPhase.Cancelling && attempt.Receipt != null)
                effects.Add(new CancelExecution(attempt.Id, attempt.Receipt));

            if (attempt.Phase == ExecutionPhase.Finalizing && attempt.WorkerGroup != null)
            {
                var receipts = attempt.WorkerGroup.Workers
                    .Where(worker => worker.Phase == WorkerPhase.Cancelling && worker.Receipt != null)
                    .Select(worker => worker.Receipt)
                    .ToArray();
                if (receipts.Length > 0)
                    effects.Add(new CancelWorkers(attempt.Id, receipts));
            }
        }

        return effects;
    }

    private IReadOnlyList<ExecutionEffect> ReconcileWorkers(ExecutionAttempt attempt)
    {
        var workerGroup = attempt.WorkerGroup;
        if (workerGroup == null || !workerGroup.Prepared || attempt.Phase != ExecutionPhase.Running)
            return Array.Empty<ExecutionEffect>();

        var effects = new List<ExecutionEffect>();
        int excess = workerGroup.AliveCount - workerGroup.DesiredCount;
        if (excess > 0)
        {
            var receipts = new List<BackendReceipt>();
            foreach (var worker in workerGroup.Workers
                         .Where(worker => worker.Phase != WorkerPhase.Ended)
                         .Reverse()
                         .Take(excess))
            {
                if (worker.Phase == WorkerPhase.Indeterminate)
                    continue;
                worker.Phase = WorkerPhase.Cancelling;
                if (worker.Receipt != null)
                    receipts.Add(worker.Receipt);
            }

            if (receipts.Count > 0)
                effects.Add(new CancelWorkers(attempt.Id, receipts));
            return effects;
        }

        int availableSubmissions = workerGroup.SubmissionLimit - workerGroup.TotalSubmissions;
        int toStart = Math.Min(Math.Min(-excess, availableSubmissions), 5);
        for (int index = 0; index < toStart; index++)
        {
            var worker = workerGroup.AddStartingWorker();
            effects.Add(new StartWorker(attempt.Id, worker.OperationId));
        }

        return effects;
    }

    private IReadOnlyList<ExecutionEffect> ContinueFinalization(ExecutionAttempt attempt)
    {
        if (attempt.WorkerGroup != null)
        {
            var receipts = new List<BackendReceipt>();
            foreach (var worker in attempt.WorkerGroup.Workers.Where(worker => worker.Phase != WorkerPhase.Ended))
            {
                if (worker.Phase is WorkerPhase.Starting)
                    worker.Phase = WorkerPhase.Cancelling;
                else if (worker.Phase is WorkerPhase.Indeterminate)
                    worker.Phase = WorkerPhase.Ended;
                else if (worker.Phase is WorkerPhase.Pending or WorkerPhase.Running)
                {
                    worker.Phase = WorkerPhase.Cancelling;
                    receipts.Add(worker.Receipt);
                }
            }

            if (receipts.Count > 0)
                return new ExecutionEffect[] { new CancelWorkers(attempt.Id, receipts) };
            if (attempt.WorkerGroup.AliveCount > 0)
                return Array.Empty<ExecutionEffect>();
        }

        if (attempt.FinalizationIssued)
            return Array.Empty<ExecutionEffect>();

        attempt.FinalizationIssued = true;
        return new ExecutionEffect[]
        {
            new FinalizeExecution(attempt.Id, attempt.PendingOutcome ?? ExecutionOutcome.Failed)
        };
    }

    private IReadOnlyList<ExecutionEffect> RecoverFinalization(ExecutionAttempt attempt)
    {
        if (attempt.WorkerGroup != null)
            foreach (var worker in attempt.WorkerGroup.Workers.Where(worker =>
                         worker.Receipt == null && worker.Phase != WorkerPhase.Ended))
                worker.Phase = WorkerPhase.Ended;

        var cleanup = ContinueFinalization(attempt);
        if (cleanup.Count > 0)
            return cleanup;

        attempt.FinalizationIssued = true;
        return new ExecutionEffect[]
        {
            new FinalizeExecution(attempt.Id, attempt.PendingOutcome ?? ExecutionOutcome.Failed)
        };
    }

    private IReadOnlyList<ExecutionEffect> ReconcileQueue(int queueId)
    {
        var queue = _queues[queueId];
        var effects = new List<ExecutionEffect>();
        var ordered = _attempts.Values
            .Where(attempt => attempt.QueueId == queueId &&
                              attempt.EnqueueSequence.HasValue &&
                              attempt.Phase is ExecutionPhase.Preparing or ExecutionPhase.Queued)
            .OrderBy(attempt => attempt.EnqueueSequence)
            .ToList();

        bool serializeStarts = queue.BackendKind == ExecutionBackendKind.ExternalScheduler;
        if (serializeStarts && _attempts.Values.Any(attempt =>
                attempt.QueueId == queueId && attempt.Phase == ExecutionPhase.Starting))
            return effects;

        foreach (var attempt in ordered)
        {
            if (attempt.Phase == ExecutionPhase.Preparing)
                break;

            switch (TryAllocate(attempt, queue))
            {
                case AllocationResult.Busy:
                    return effects;

                case AllocationResult.Rejected:
                    continue;
            }

            attempt.TransitionTo(ExecutionPhase.Starting, Now());
            effects.Add(new StartExecution(attempt.Id, attempt.GpuIndices));
            if (serializeStarts)
                return effects;
        }

        return effects;
    }

    private AllocationResult TryAllocate(ExecutionAttempt attempt, ExecutionQueuePolicy queue)
    {
        if (queue.Capacity is not { } capacity)
        {
            attempt.HasAllocation = true;
            return AllocationResult.Allocated;
        }

        if (!attempt.ResourceRequest.FitsWithin(capacity))
        {
            attempt.TransitionTo(
                ExecutionPhase.Failed,
                Now(),
                $"The request {attempt.ResourceRequest} exceeds queue capacity {capacity}.");
            _currentAttempts.Remove(attempt.Job);
            return AllocationResult.Rejected;
        }

        var allocatedAttempts = _attempts.Values.Where(other =>
            other.QueueId == queue.QueueId &&
            other.Id != attempt.Id &&
            other.HasAllocation);

        var used = ResourceVector.None;
        var usedGpus = new HashSet<int>();
        foreach (var allocated in allocatedAttempts)
        {
            used += allocated.ResourceRequest;
            usedGpus.UnionWith(allocated.GpuIndices);
        }

        var available = capacity - used;
        if (!attempt.ResourceRequest.FitsWithin(available))
            return AllocationResult.Busy;

        attempt.GpuIndices = Enumerable.Range(0, capacity.Gpus)
            .Where(index => !usedGpus.Contains(index))
            .Take(attempt.ResourceRequest.Gpus)
            .ToArray();
        attempt.HasAllocation = true;
        return AllocationResult.Allocated;
    }

    private void Release(ExecutionAttempt attempt)
    {
        attempt.HasAllocation = false;
        attempt.GpuIndices = Array.Empty<int>();
    }

    private bool TryGetCurrent(Guid attemptId, out ExecutionAttempt attempt)
    {
        if (!_attempts.TryGetValue(attemptId, out attempt))
            return false;

        return _currentAttempts.TryGetValue(attempt.Job, out var currentId) && currentId == attemptId;
    }

    private void AssignSequence(ExecutionAttempt attempt)
    {
        attempt.EnqueueSequence ??= ++_nextSequence;
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

    private enum AllocationResult
    {
        Allocated,
        Busy,
        Rejected
    }
}
