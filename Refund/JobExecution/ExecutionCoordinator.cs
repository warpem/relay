namespace Refund.JobExecution;

/// <summary>
/// The single mutable authority for execution attempts. This class is deliberately synchronous:
/// callers serialize access to it, reconcile and persist its state, then execute planned effects
/// outside the coordinator. No backend code runs here.
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

    public void ForgetTerminalAttempts(IEnumerable<Guid> attemptIds)
    {
        foreach (var attemptId in attemptIds.Distinct())
        {
            if (!_attempts.TryGetValue(attemptId, out var attempt))
                continue;
            if (!attempt.IsTerminal)
                throw new InvalidOperationException(
                    $"Attempt {attemptId} cannot be forgotten before it becomes terminal.");

            _attempts.Remove(attemptId);
            if (_currentAttempts.TryGetValue(attempt.Job, out var currentId) &&
                currentId == attemptId)
                _currentAttempts.Remove(attempt.Job);
        }
    }

    public ExecutionAttempt RequestRun(
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

        EnsureJobHasNoAttempt(job);

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

        if (dependenciesReady)
            AssignSequence(attempt);
        return attempt;
    }

    public ExecutionAttempt RequestFinalization(
        JobAddress job,
        int queueId)
    {
        if (!_queues.TryGetValue(queueId, out var queue))
            throw new InvalidOperationException($"Queue {queueId} does not exist.");

        EnsureJobHasNoAttempt(job);

        var attempt = new ExecutionAttempt(
            Guid.NewGuid(), job, queueId, queue.BackendKind,
            queue.BackendConfiguration, null, ExecutionPurpose.FinalizeOnly,
            ResourceVector.None, null, ExecutionPhase.Finalizing, Now())
        {
            PendingOutcome = ExecutionOutcome.Succeeded
        };

        _attempts.Add(attempt.Id, attempt);
        _currentAttempts[job] = attempt.Id;
        return attempt;
    }

    public void DependenciesSatisfied(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase != ExecutionPhase.WaitingForDependencies)
            return;

        AssignSequence(attempt);
        attempt.TransitionTo(ExecutionPhase.Preparing, Now());
    }

    private void EnsureJobHasNoAttempt(JobAddress job)
    {
        if (_attempts.Values.Any(attempt => attempt.Job == job))
            throw new InvalidOperationException($"Job {job} already has an execution attempt.");
    }

    public void DependencyCheckFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase != ExecutionPhase.WaitingForDependencies)
            return;

        attempt.TransitionTo(ExecutionPhase.Failed, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
    }

    public void PreparationCompleted(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Preparing or ExecutionPhase.Cancelling))
            return;

        if (attempt.Phase == ExecutionPhase.Cancelling)
        {
            attempt.TransitionTo(ExecutionPhase.Canceled, Now());
            _currentAttempts.Remove(attempt.Job);
            return;
        }

        if (attempt.WorkerGroup != null)
            attempt.WorkerGroup.Prepared = true;
        attempt.TransitionTo(ExecutionPhase.Queued, Now());
    }

    public void PreparationFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Preparing or ExecutionPhase.Cancelling))
            return;

        var terminal = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionPhase.Canceled
            : ExecutionPhase.Failed;
        attempt.TransitionTo(terminal, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
    }

    public void StartCompleted(
        Guid attemptId,
        BackendReceipt receipt,
        bool isRunning)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return;

        attempt.Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        attempt.Health = ExecutionHealth.Healthy;
        attempt.HealthDetail = null;

        if (attempt.Phase == ExecutionPhase.Cancelling)
            return;

        if (attempt.BackendKind == ExecutionBackendKind.Managed)
        {
            if (isRunning)
                throw new InvalidOperationException("An already-running backend cannot require activation.");
            return;
        }

        attempt.TransitionTo(isRunning ? ExecutionPhase.Running : ExecutionPhase.Pending, Now());
    }

    public void ActivationCompleted(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.Receipt == null)
            return;

        if (attempt.Phase == ExecutionPhase.Cancelling)
            return;
        if (attempt.Phase != ExecutionPhase.Starting)
            return;

        attempt.TransitionTo(ExecutionPhase.Running, Now());
    }

    public void ActivationFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return;

        Release(attempt);
        var terminal = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionPhase.Canceled
            : ExecutionPhase.Failed;
        attempt.TransitionTo(terminal, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
    }

    public void StartFailed(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return;

        Release(attempt);
        var terminal = attempt.Phase == ExecutionPhase.Cancelling
            ? ExecutionPhase.Canceled
            : ExecutionPhase.Failed;
        attempt.TransitionTo(terminal, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
    }

    public void StartIndeterminate(Guid attemptId, string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase is not (ExecutionPhase.Starting or ExecutionPhase.Cancelling))
            return;

        Release(attempt);
        attempt.Health = ExecutionHealth.Indeterminate;
        attempt.HealthDetail = detail;
        attempt.TransitionTo(ExecutionPhase.Interrupted, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
    }

    public void Observe(Guid attemptId, BackendObservation observation)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.IsTerminal)
            return;

        switch (observation.Kind)
        {
            case BackendObservationKind.AbsentFromActiveView:
            case BackendObservationKind.Unreachable:
            case BackendObservationKind.Unparseable:
            case BackendObservationKind.Indeterminate:
                attempt.Health = ExecutionHealth.Indeterminate;
                attempt.HealthDetail = observation.Detail;
                return;

            case BackendObservationKind.Pending:
                attempt.Health = ExecutionHealth.Healthy;
                attempt.HealthDetail = null;
                if (attempt.Phase == ExecutionPhase.Starting)
                    attempt.TransitionTo(ExecutionPhase.Pending, Now());
                return;

            case BackendObservationKind.Running:
                attempt.Health = ExecutionHealth.Healthy;
                attempt.HealthDetail = null;
                bool enteredRunning = attempt.Phase is ExecutionPhase.Starting or ExecutionPhase.Pending;
                if (enteredRunning)
                    attempt.TransitionTo(ExecutionPhase.Running, Now());
                return;

            case BackendObservationKind.Succeeded:
                BeginFinalization(attempt, ExecutionOutcome.Succeeded, observation.Detail);
                return;

            case BackendObservationKind.Failed:
                BeginFinalization(attempt, ExecutionOutcome.Failed, observation.Detail);
                return;

            case BackendObservationKind.Canceled:
                BeginFinalization(attempt, ExecutionOutcome.Canceled, observation.Detail);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(observation));
        }
    }

    public void RequestCancel(Guid attemptId)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.IsTerminal ||
            attempt.Phase is ExecutionPhase.Cancelling or ExecutionPhase.Stopping or ExecutionPhase.Finalizing)
            return;

        if (attempt.Phase is ExecutionPhase.WaitingForDependencies or ExecutionPhase.Queued)
        {
            Release(attempt);
            attempt.TransitionTo(ExecutionPhase.Canceled, Now());
            _currentAttempts.Remove(attempt.Job);
            return;
        }

        attempt.TransitionTo(ExecutionPhase.Cancelling, Now());
    }

    public void CancelCompleted(Guid attemptId, BackendObservation observation)
    {
        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase != ExecutionPhase.Cancelling)
            return;

        Observe(attemptId, observation);
        if (attempt.Phase == ExecutionPhase.Cancelling)
            attempt.TransitionTo(ExecutionPhase.Stopping, Now(), observation.Detail);
    }

    public void FinalizationCompleted(Guid attemptId, string failure = null)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.Phase != ExecutionPhase.Finalizing)
            return;

        var phase = failure != null
            ? ExecutionPhase.Failed
            : attempt.PendingOutcome switch
            {
                ExecutionOutcome.Succeeded => ExecutionPhase.Succeeded,
                ExecutionOutcome.Canceled => ExecutionPhase.Canceled,
                ExecutionOutcome.Interrupted => ExecutionPhase.Interrupted,
                _ => ExecutionPhase.Failed
            };

        string detail = failure ?? (phase is ExecutionPhase.Failed or ExecutionPhase.Interrupted
            ? attempt.HealthDetail ?? attempt.History.LastOrDefault()?.Detail
            : null);
        attempt.TransitionTo(phase, Now(), detail);
        _currentAttempts.Remove(attempt.Job);
    }

    public void InterruptOwnerBoundAttempts()
    {
        var interrupted = _attempts.Values.Where(attempt =>
                !attempt.IsTerminal && attempt.BackendKind != ExecutionBackendKind.ExternalScheduler)
            .ToList();

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
        }
    }

    private void BeginFinalization(
        ExecutionAttempt attempt,
        ExecutionOutcome observedOutcome,
        string detail)
    {
        if (attempt.Phase is not (ExecutionPhase.Starting
            or ExecutionPhase.Pending
            or ExecutionPhase.Running
            or ExecutionPhase.Cancelling
            or ExecutionPhase.Stopping))
            return;

        var outcome = attempt.Phase is ExecutionPhase.Cancelling or ExecutionPhase.Stopping
            ? ExecutionOutcome.Canceled
            : observedOutcome;

        bool hasUntraceableWorker = attempt.WorkerGroup?.Workers.Any(worker =>
            worker.Phase == WorkerPhase.Indeterminate) == true;
        if (hasUntraceableWorker)
        {
            outcome = ExecutionOutcome.Interrupted;
            detail = "A worker submission has an unknown outcome and no scheduler receipt; " +
                     "Relay cannot prove that all worker processes stopped.";
        }

        Release(attempt);
        attempt.PendingOutcome = outcome;
        attempt.Health = hasUntraceableWorker
            ? ExecutionHealth.Indeterminate
            : ExecutionHealth.Healthy;
        attempt.HealthDetail = hasUntraceableWorker ? detail : null;
        attempt.TransitionTo(ExecutionPhase.Finalizing, Now(), detail);
    }

    public void WorkerStarted(
        Guid attemptId,
        Guid operationId,
        BackendReceipt receipt,
        bool isRunning)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return;

        var worker = attempt.WorkerGroup.Workers.FirstOrDefault(item => item.OperationId == operationId);
        if (worker is not { Phase: WorkerPhase.Starting or WorkerPhase.Cancelling })
            return;

        worker.Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        if (worker.Phase == WorkerPhase.Cancelling || attempt.Phase == ExecutionPhase.Finalizing)
        {
            worker.Phase = WorkerPhase.Cancelling;
            return;
        }

        worker.Phase = isRunning ? WorkerPhase.Running : WorkerPhase.Pending;
    }

    public void WorkerStartFailed(
        Guid attemptId,
        Guid operationId,
        string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return;

        var worker = attempt.WorkerGroup.Workers.FirstOrDefault(item => item.OperationId == operationId);
        if (worker is not { Phase: WorkerPhase.Starting or WorkerPhase.Cancelling })
            return;

        worker.Phase = WorkerPhase.Ended;
    }

    public void WorkerStartIndeterminate(
        Guid attemptId,
        Guid operationId,
        string detail)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return;

        var worker = attempt.WorkerGroup.Workers.FirstOrDefault(item => item.OperationId == operationId);
        if (worker is not { Phase: WorkerPhase.Starting or WorkerPhase.Cancelling })
            return;

        worker.Phase = attempt.Phase == ExecutionPhase.Finalizing
            ? WorkerPhase.Ended
            : WorkerPhase.Indeterminate;
        attempt.Health = ExecutionHealth.Indeterminate;
        attempt.HealthDetail = detail;
        if (attempt.Phase == ExecutionPhase.Finalizing)
            attempt.PendingOutcome = ExecutionOutcome.Interrupted;
    }

    public void ObserveWorkers(
        Guid attemptId,
        IReadOnlyList<WorkerObservation> observations)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return;

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

    }

    public void WorkersCanceled(
        Guid attemptId,
        IReadOnlyCollection<string> receiptIds)
    {
        if (!TryGetCurrent(attemptId, out var attempt) || attempt.WorkerGroup == null)
            return;

        var canceled = receiptIds.ToHashSet();
        foreach (var worker in attempt.WorkerGroup.Workers)
            if (worker.Receipt != null && canceled.Contains(worker.Receipt.Id))
                worker.Phase = WorkerPhase.Ended;
    }

    public void ResizeWorkerGroup(Guid attemptId, int desiredCount)
    {
        if (desiredCount < 0)
            throw new InvalidOperationException("Worker count cannot be negative.");

        if (!TryGetCurrent(attemptId, out var attempt) ||
            attempt.Phase != ExecutionPhase.Running ||
            attempt.WorkerGroup == null)
            return;

        attempt.WorkerGroup.DesiredCount = desiredCount;
    }

    public void Recover()
    {
        foreach (var attempt in _attempts.Values.Where(attempt => !attempt.IsTerminal))
            RecoverReceiptlessWorkers(attempt);

        InterruptOwnerBoundAttempts();

        foreach (var attempt in _attempts.Values.Where(attempt =>
                     !attempt.IsTerminal && attempt.BackendKind == ExecutionBackendKind.ExternalScheduler))
        {
            switch (attempt.Phase)
            {
                case ExecutionPhase.Starting when attempt.Receipt == null:
                    Release(attempt);
                    attempt.Health = ExecutionHealth.Indeterminate;
                    attempt.HealthDetail = "Submission outcome is unknown after restart.";
                    attempt.TransitionTo(
                        ExecutionPhase.Interrupted,
                        Now(),
                        attempt.HealthDetail);
                    _currentAttempts.Remove(attempt.Job);
                    break;
                case ExecutionPhase.Starting:
                    attempt.TransitionTo(ExecutionPhase.Pending, Now());
                    break;
                case ExecutionPhase.Cancelling or ExecutionPhase.Stopping when attempt.Receipt == null:
                    Release(attempt);
                    attempt.Health = ExecutionHealth.Indeterminate;
                    attempt.HealthDetail = "Cancellation could not be resumed without a scheduler receipt.";
                    attempt.TransitionTo(
                        ExecutionPhase.Interrupted,
                        Now(),
                        attempt.HealthDetail);
                    _currentAttempts.Remove(attempt.Job);
                    break;
            }
        }
    }

    public IReadOnlyList<ExecutionEffect> PlanEffects()
    {
        var effects = new List<ExecutionEffect>();

        foreach (var attempt in _attempts.Values.Where(attempt =>
                     !attempt.IsTerminal && attempt.Phase == ExecutionPhase.Running))
            ReconcileWorkers(attempt);

        foreach (var attempt in _attempts.Values.Where(attempt =>
                     !attempt.IsTerminal && attempt.Phase == ExecutionPhase.Finalizing))
            PrepareFinalization(attempt);

        foreach (var queueId in _queues.Keys)
            ReconcileQueue(queueId);

        foreach (var attempt in _attempts.Values
                     .Where(attempt => !attempt.IsTerminal)
                     .OrderBy(attempt => attempt.EnqueueSequence ?? long.MaxValue)
                     .ThenBy(attempt => attempt.CreatedAt))
        {
            switch (attempt.Phase)
            {
                case ExecutionPhase.Preparing:
                    effects.Add(new PrepareExecution(attempt.Id));
                    break;
                case ExecutionPhase.Starting when attempt.Receipt == null:
                    effects.Add(new StartExecution(attempt.Id, attempt.GpuIndices));
                    break;
                case ExecutionPhase.Starting when
                    attempt.BackendKind == ExecutionBackendKind.Managed:
                    effects.Add(new ActivateExecution(attempt.Id));
                    break;
                case ExecutionPhase.Cancelling when attempt.Receipt != null:
                    effects.Add(new CancelExecution(attempt.Id, attempt.Receipt));
                    break;
                case ExecutionPhase.Finalizing when attempt.WorkerGroup?.AliveCount is null or 0:
                    effects.Add(new FinalizeExecution(
                        attempt.Id,
                        attempt.PendingOutcome ?? ExecutionOutcome.Failed));
                    break;
            }

            if (attempt.WorkerGroup != null)
            {
                foreach (var worker in attempt.WorkerGroup.Workers.Where(worker =>
                             worker.Receipt == null &&
                             worker.Phase is WorkerPhase.Starting or WorkerPhase.Cancelling))
                    effects.Add(new StartWorker(attempt.Id, worker.OperationId));

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

    private void ReconcileWorkers(ExecutionAttempt attempt)
    {
        var workerGroup = attempt.WorkerGroup;
        if (workerGroup == null || !workerGroup.Prepared || attempt.Phase != ExecutionPhase.Running)
            return;

        int excess = workerGroup.AliveCount - workerGroup.DesiredCount;
        if (excess > 0)
        {
            foreach (var worker in workerGroup.Workers
                         .Where(worker => worker.Phase is not (
                             WorkerPhase.Ended or WorkerPhase.Indeterminate))
                         .Reverse()
                         .Take(excess))
            {
                worker.Phase = WorkerPhase.Cancelling;
            }
            return;
        }

        int availableSubmissions = workerGroup.SubmissionLimit - workerGroup.TotalSubmissions;
        int toStart = Math.Min(Math.Min(-excess, availableSubmissions), 5);
        for (int index = 0; index < toStart; index++)
            workerGroup.AddStartingWorker();
    }

    private void PrepareFinalization(ExecutionAttempt attempt)
    {
        if (attempt.WorkerGroup == null)
            return;

        foreach (var worker in attempt.WorkerGroup.Workers.Where(worker => worker.Phase != WorkerPhase.Ended))
        {
            if (worker.Phase is WorkerPhase.Starting)
                worker.Phase = WorkerPhase.Cancelling;
            else if (worker.Phase is WorkerPhase.Indeterminate)
                worker.Phase = WorkerPhase.Ended;
            else if (worker.Phase is WorkerPhase.Pending or WorkerPhase.Running)
                worker.Phase = WorkerPhase.Cancelling;
        }
    }

    private static void RecoverReceiptlessWorkers(ExecutionAttempt attempt)
    {
        if (attempt.WorkerGroup == null)
            return;

        bool untraceable = false;
        foreach (var worker in attempt.WorkerGroup.Workers.Where(worker =>
                     worker.Receipt == null && worker.Phase is WorkerPhase.Starting or WorkerPhase.Cancelling))
        {
            worker.Phase = WorkerPhase.Indeterminate;
            untraceable = true;
        }

        untraceable |= attempt.WorkerGroup.Workers.Any(worker =>
            worker.Phase == WorkerPhase.Indeterminate);
        if (!untraceable)
            return;

        attempt.Health = ExecutionHealth.Indeterminate;
        attempt.HealthDetail =
            "A worker submission has an unknown outcome and no scheduler receipt; " +
            "Relay cannot prove that all worker processes stopped.";
        if (attempt.Phase == ExecutionPhase.Finalizing)
            attempt.PendingOutcome = ExecutionOutcome.Interrupted;
    }

    private void ReconcileQueue(int queueId)
    {
        var queue = _queues[queueId];
        var ordered = _attempts.Values
            .Where(attempt => attempt.QueueId == queueId &&
                              attempt.EnqueueSequence.HasValue &&
                              attempt.Phase is ExecutionPhase.Preparing or ExecutionPhase.Queued)
            .OrderBy(attempt => attempt.EnqueueSequence)
            .ToList();

        bool serializeStarts = queue.BackendKind == ExecutionBackendKind.ExternalScheduler;
        if (serializeStarts && _attempts.Values.Any(attempt =>
                attempt.QueueId == queueId && attempt.Phase == ExecutionPhase.Starting))
            return;

        foreach (var attempt in ordered)
        {
            if (attempt.Phase == ExecutionPhase.Preparing)
                break;

            switch (TryAllocate(attempt, queue))
            {
                case AllocationResult.Busy:
                    return;

                case AllocationResult.Rejected:
                    continue;
            }

            attempt.TransitionTo(ExecutionPhase.Starting, Now());
            if (serializeStarts)
                return;
        }
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
