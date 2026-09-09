using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobQueues;
using Serilog;

namespace Refund.Services.Core.Repositories;

public sealed class QueueRepository
{
    private readonly string _statePath;
    private readonly object _queuesLock = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly ILogger _logger = Log.ForContext<QueueRepository>();
    private readonly Func<Job, Action<Job>, Task> _updateJob;
    private readonly LocalQueue _localQueue = new();
    private readonly List<JobQueue> _clusterQueues = new();
    private readonly ExecutionCoordinator _coordinator;
    private readonly RelayExecutionOperations _operations;
    private readonly ExecutionRuntime _runtime;
    private CancellationTokenSource _daemonCancellation;
    private Task _daemonTask;
    private DataRepository _dataRepository;
    private int _shutdownStarted;

    public QueueRepository(
        string statePath,
        Func<Job, Action<Job>, Task> updateJob)
    {
        _statePath = statePath;
        _updateJob = updateJob;

        var localPolicy = PolicyFor(_localQueue);
        _coordinator = new ExecutionCoordinator([localPolicy]);
        _operations = new RelayExecutionOperations(
            address => _dataRepository?.FindJob(
                address.ProjectId,
                address.SpaceId,
                address.JobId),
            updateJob);
        _runtime = new ExecutionRuntime(
            _coordinator,
            _operations,
            new JsonExecutionStateStore($"{statePath}.executions"),
            ProjectAttemptAsync);

        _localQueue.SetJobsProvider(() => ActiveJobs(_localQueue.Id));
    }

    public JobQueue LocalQueue => _localQueue;

    public ReadOnlyCollection<JobQueue> ClusterQueues
    {
        get
        {
            lock (_queuesLock)
                return _clusterQueues.ToList().AsReadOnly();
        }
    }

    public void LoadQueues(DataRepository dataRepository)
    {
        _dataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));
        LoadConfiguration();

        foreach (var queue in ClusterQueues)
            _coordinator.UpsertQueue(PolicyFor(queue));

        _runtime.InitializeAsync().GetAwaiter().GetResult();
        MarkUnownedJobsInterruptedAsync().GetAwaiter().GetResult();
    }

    public async Task<JobQueue> CreateClusterQueueAsync(
        ClusterQueue template = null,
        CancellationToken cancellationToken = default)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            var queue = template == null ? new ClusterQueue() : Clone(template);
            lock (_queuesLock)
            {
                ManagedQueueRules.ValidateOnly(_clusterQueues.OfType<ClusterQueue>(), queue);
                queue.Id = _clusterQueues.Select(item => item.Id).DefaultIfEmpty(0).Max() + 1;
                queue.SetJobsProvider(() => ActiveJobs(queue.Id));
                _clusterQueues.Add(queue);
            }

            bool configurationSaved = false;
            try
            {
                SaveConfiguration();
                configurationSaved = true;
                await _runtime.UpsertQueueAsync(PolicyFor(queue), CancellationToken.None);
            }
            catch
            {
                lock (_queuesLock)
                    _clusterQueues.Remove(queue);
                if (configurationSaved)
                    SaveConfiguration();
                throw;
            }

            return queue;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task UpdateQueueAsync(
        JobQueue queue,
        Action<JobQueue> updateAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(updateAction);

        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            JsonNode original = queue.ToJson();
            if (queue is ClusterQueue cluster)
            {
                var proposed = Clone(cluster);
                updateAction(proposed);
                proposed.Id = cluster.Id;
                ManagedQueueRules.ValidateChange(
                    cluster,
                    proposed,
                    ClusterQueues.OfType<ClusterQueue>(),
                    IsQueueInUse(cluster.Id));
                cluster.ReadFromJson(proposed.ToJson());
                ManagedQueueRules.DisableDuplicateManagedQueues(
                    ClusterQueues.OfType<ClusterQueue>());
            }
            else
            {
                updateAction(queue);
                queue.Id = _localQueue.Id;
                queue.QueueType = JobQueueType.Local;
            }

            bool configurationSaved = false;
            try
            {
                SaveConfiguration();
                configurationSaved = true;
                await _runtime.UpsertQueueAsync(PolicyFor(queue), CancellationToken.None);
            }
            catch
            {
                queue.ReadFromJson(original);
                ManagedQueueRules.DisableDuplicateManagedQueues(
                    ClusterQueues.OfType<ClusterQueue>());
                if (configurationSaved)
                {
                    SaveConfiguration();
                    await _runtime.UpsertQueueAsync(PolicyFor(queue), CancellationToken.None);
                }
                throw;
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task DeleteClusterQueueAsync(
        ClusterQueue queue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);

        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            ManagedQueueRules.ValidateDelete(
                queue,
                IsQueueInUse(queue.Id));

            int index;
            lock (_queuesLock)
            {
                index = _clusterQueues.IndexOf(queue);
                if (index < 0)
                    throw new InvalidOperationException($"Queue {queue.Id} does not exist.");
                _clusterQueues.RemoveAt(index);
            }

            bool configurationSaved = false;
            try
            {
                SaveConfiguration();
                configurationSaved = true;
                await _runtime.RemoveQueueAsync(queue.Id, CancellationToken.None);
            }
            catch
            {
                lock (_queuesLock)
                    _clusterQueues.Insert(index, queue);
                if (configurationSaved)
                {
                    SaveConfiguration();
                    await _runtime.UpsertQueueAsync(PolicyFor(queue), CancellationToken.None);
                }
                throw;
            }

            ManagedQueueRules.DisableDuplicateManagedQueues(
                ClusterQueues.OfType<ClusterQueue>());
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task ReorderClusterQueueAsync(
        JobQueue queue,
        int newPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);

        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            int currentPosition;
            lock (_queuesLock)
            {
                currentPosition = _clusterQueues.IndexOf(queue);
                if (currentPosition < 0)
                    throw new InvalidOperationException($"Queue {queue.Id} does not exist.");
                if (newPosition < 0 || newPosition >= _clusterQueues.Count)
                    throw new ArgumentOutOfRangeException(nameof(newPosition));

                _clusterQueues.RemoveAt(currentPosition);
                _clusterQueues.Insert(newPosition, queue);
            }

            try
            {
                SaveConfiguration();
            }
            catch
            {
                lock (_queuesLock)
                {
                    _clusterQueues.Remove(queue);
                    _clusterQueues.Insert(currentPosition, queue);
                }
                throw;
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task QueueJobAsync(
        Job job,
        JobQueue queue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(queue);

        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            var registeredQueue = FindQueue(queue.Id);
            if (!ReferenceEquals(registeredQueue, queue))
                throw new InvalidOperationException($"Queue {queue.Id} is no longer registered.");
            if (queue is ClusterQueue { ManagedDisabledReason: { Length: > 0 } reason })
                throw new InvalidOperationException(reason);

            WorkerGroupRequest workerGroup = null;
            if (job is IPooledJob pooled && pooled.PoolQueueId > 0)
            {
                var workerQueue = FindQueue(pooled.PoolQueueId) as ClusterQueue
                    ?? throw new InvalidOperationException(
                        $"Worker queue {pooled.PoolQueueId} does not exist.");
                if (workerQueue.IsManaged)
                    throw new InvalidOperationException(
                        "Worker pools require an external scheduler queue.");

                workerGroup = new WorkerGroupRequest(
                    workerQueue.Id,
                    pooled.PoolSize,
                    pooled.PoolSubmissionCap);
            }

            await _runtime.RequestRunAsync(
                AddressOf(job),
                queue.Id,
                RequestFor(job, queue),
                job.IsReadyToStage(),
                workerGroup,
                cancellationToken);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public Task CancelJobAsync(
        Job job,
        CancellationToken cancellationToken = default) =>
        _runtime.RequestCancelAsync(AddressOf(job), cancellationToken);

    public Task FinalizeJobAsync(
        Job job,
        CancellationToken cancellationToken = default) =>
        _runtime.RequestFinalizationAsync(
            AddressOf(job),
            _localQueue.Id,
            cancellationToken);

    public Task ResizeWorkerGroupAsync(
        Job job,
        int desiredCount,
        CancellationToken cancellationToken = default) =>
        _runtime.ResizeWorkerGroupAsync(
            AddressOf(job),
            desiredCount,
            cancellationToken);

    public bool HasActiveAttempt(Job job) =>
        HasActiveAttempts(job.Space.Project.Id, job.Space.Id, job.Id);

    public bool HasActiveAttempts(int projectId, int? spaceId = null, int? jobId = null) =>
        _runtime.Attempts.Any(attempt =>
            !attempt.Phase.IsTerminal() &&
            attempt.Job.ProjectId == projectId &&
            (spaceId == null || attempt.Job.SpaceId == spaceId) &&
            (jobId == null || attempt.Job.JobId == jobId));

    public JobQueue FindQueue(int id)
    {
        if (id == _localQueue.Id)
            return _localQueue;

        lock (_queuesLock)
            return _clusterQueues.FirstOrDefault(queue => queue.Id == id);
    }

    public void StartDaemon(int milliseconds)
    {
        if (_daemonTask != null)
            throw new InvalidOperationException("The execution daemon is already running.");
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));

        _daemonCancellation = new CancellationTokenSource();
        _daemonTask = RunDaemonAsync(
            TimeSpan.FromMilliseconds(milliseconds),
            _daemonCancellation.Token);
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        if (_daemonCancellation != null)
        {
            _daemonCancellation.Cancel();
            try
            {
                await _daemonTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _runtime.ShutdownAsync();
        SaveConfiguration();
    }

    internal static JobStatus StatusFor(ExecutionAttemptSnapshot attempt) =>
        attempt.Phase switch
        {
            ExecutionPhase.WaitingForDependencies or
            ExecutionPhase.Preparing or
            ExecutionPhase.Queued => JobStatus.Waiting,
            ExecutionPhase.Starting or
            ExecutionPhase.Pending => JobStatus.Staging,
            ExecutionPhase.Running => JobStatus.Running,
            ExecutionPhase.Cancelling or
            ExecutionPhase.Stopping => JobStatus.Aborting,
            ExecutionPhase.Finalizing => JobStatus.Finalizing,
            ExecutionPhase.Succeeded => JobStatus.Finished,
            ExecutionPhase.Failed => JobStatus.Failed,
            ExecutionPhase.Canceled => JobStatus.Aborted,
            ExecutionPhase.Interrupted => JobStatus.Interrupted,
            _ => throw new ArgumentOutOfRangeException(nameof(attempt.Phase))
        };

    private async Task ProjectAttemptAsync(ExecutionAttemptSnapshot attempt)
    {
        var job = _dataRepository?.FindJob(
            attempt.Job.ProjectId,
            attempt.Job.SpaceId,
            attempt.Job.JobId);
        if (job == null)
            return;

        JobStatus projectedStatus = StatusFor(attempt);
        bool statusChanged = false;
        if (ProjectionWouldChange(job, attempt))
        {
            await _updateJob(job, mutable =>
            {
                JobStatus previousStatus = mutable.Status;
                ApplyProjection(mutable, attempt);
                statusChanged = mutable.Status != previousStatus;
            });
        }

        if (attempt.Phase.IsTerminal() && CanApplyProjection(job, attempt, projectedStatus))
            _dataRepository.SaveSpaceImmediately(job.Space);

        string detail = attempt.History.LastOrDefault()?.Detail;
        if (statusChanged && !string.IsNullOrWhiteSpace(detail) &&
            projectedStatus is JobStatus.Failed or JobStatus.Interrupted)
            await job.WriteToErrorLog(detail);
    }

    internal static bool ApplyProjection(Job job, ExecutionAttemptSnapshot attempt)
    {
        JobStatus status = StatusFor(attempt);
        if (!CanApplyProjection(job, attempt, status))
            return false;

        bool changed = false;
        if (job.QueueId != attempt.QueueId)
        {
            job.QueueId = attempt.QueueId;
            changed = true;
        }

        string receiptId = attempt.Receipt?.Id;
        if (job.ClusterJobId != receiptId)
        {
            job.ClusterJobId = receiptId;
            changed = true;
        }

        if (job is IPooledJob pooled)
        {
            int alive = attempt.WorkerGroup?.Workers.Count(
                worker => worker.Phase != WorkerPhase.Ended) ?? 0;
            int running = attempt.WorkerGroup?.Workers.Count(
                worker => worker.Phase == WorkerPhase.Running) ?? 0;
            int submitted = attempt.WorkerGroup?.TotalSubmissions ?? 0;
            if (pooled.PoolWorkersAlive != alive)
            {
                pooled.PoolWorkersAlive = alive;
                changed = true;
            }
            if (pooled.PoolWorkersRunning != running)
            {
                pooled.PoolWorkersRunning = running;
                changed = true;
            }
            if (pooled.PoolWorkersSubmitted != submitted)
            {
                pooled.PoolWorkersSubmitted = submitted;
                changed = true;
            }
        }

        if (job.Status == status)
            return changed;

        job.Status = status;
        job.AddEvent(status.ToEventType(), job.UpdatedBy);
        return true;
    }

    private static bool ProjectionWouldChange(Job job, ExecutionAttemptSnapshot attempt)
    {
        JobStatus status = StatusFor(attempt);
        if (!CanApplyProjection(job, attempt, status))
            return false;
        if (job.QueueId != attempt.QueueId || job.ClusterJobId != attempt.Receipt?.Id ||
            job.Status != status)
            return true;
        if (job is not IPooledJob pooled)
            return false;

        return pooled.PoolWorkersAlive != (attempt.WorkerGroup?.Workers.Count(
                   worker => worker.Phase != WorkerPhase.Ended) ?? 0) ||
               pooled.PoolWorkersRunning != (attempt.WorkerGroup?.Workers.Count(
                   worker => worker.Phase == WorkerPhase.Running) ?? 0) ||
               pooled.PoolWorkersSubmitted != (attempt.WorkerGroup?.TotalSubmissions ?? 0);
    }

    private static bool CanApplyProjection(
        Job job,
        ExecutionAttemptSnapshot attempt,
        JobStatus status) =>
        !attempt.Phase.IsTerminal() ||
        job.Status == status ||
        job.Status is JobStatus.Waiting or
            JobStatus.Staging or
            JobStatus.Running or
            JobStatus.Finalizing or
            JobStatus.Aborting;

    private async Task MarkUnownedJobsInterruptedAsync()
    {
        var owned = _runtime.Attempts
            .Where(attempt => !attempt.Phase.IsTerminal())
            .Select(attempt => attempt.Job)
            .ToHashSet();

        foreach (var project in _dataRepository.Projects)
        foreach (var space in project.Spaces)
        foreach (var job in space.Jobs)
        {
            if (job.Status != JobStatus.Waiting && !job.Status.IsUnsettled() ||
                owned.Contains(AddressOf(job)))
                continue;

            await _updateJob(job, mutable =>
            {
                mutable.Status = JobStatus.Interrupted;
                mutable.AddEvent(EventType.Interrupted);
            });
        }
    }

    private IReadOnlyList<Job> ActiveJobs(int queueId)
    {
        if (_dataRepository == null)
            return Array.Empty<Job>();

        return _runtime.ActiveAttempts(queueId)
            .Concat(_runtime.Attempts.Where(attempt =>
                !attempt.Phase.IsTerminal() && attempt.WorkerGroup?.QueueId == queueId))
            .DistinctBy(attempt => attempt.Id)
            .Select(attempt => _dataRepository.FindJob(
                attempt.Job.ProjectId,
                attempt.Job.SpaceId,
                attempt.Job.JobId))
            .Where(job => job != null)
            .ToArray();
    }

    private bool IsQueueInUse(int queueId) => _runtime.Attempts.Any(attempt =>
        !attempt.Phase.IsTerminal() &&
        (attempt.QueueId == queueId || attempt.WorkerGroup?.QueueId == queueId));

    private async Task RunDaemonAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await _runtime.TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Execution daemon tick failed");
            }
        }
    }

    private void LoadConfiguration()
    {
        if (!File.Exists(_statePath))
            return;

        var root = JsonNode.Parse(File.ReadAllText(_statePath)) as JsonObject
            ?? throw new InvalidDataException($"Queue configuration {_statePath} is invalid.");

        if (root["Local"] is JsonNode local)
            _localQueue.ReadFromJson(local);
        _localQueue.Id = -1;
        _localQueue.QueueType = JobQueueType.Local;

        if (root["Cluster"] is JsonArray clusterNodes)
        {
            lock (_queuesLock)
            {
                _clusterQueues.Clear();
                foreach (var node in clusterNodes)
                {
                    if (node == null)
                        continue;
                    var queue = new ClusterQueue();
                    queue.ReadFromJson(node);
                    if (queue.Id == _localQueue.Id ||
                        _clusterQueues.Any(existing => existing.Id == queue.Id))
                        throw new InvalidDataException(
                            $"Queue configuration contains duplicate ID {queue.Id}.");
                    queue.SetJobsProvider(() => ActiveJobs(queue.Id));
                    _clusterQueues.Add(queue);
                }
            }
        }

        foreach (var disabled in ManagedQueueRules.DisableDuplicateManagedQueues(
                     ClusterQueues.OfType<ClusterQueue>()))
            _logger.Error(
                "Queue {QueueId} is disabled: {Reason}",
                disabled.Id,
                disabled.ManagedDisabledReason);
    }

    private void SaveConfiguration()
    {
        JsonNode[] queues;
        lock (_queuesLock)
            queues = _clusterQueues.Select(queue => queue.ToJson()).ToArray();

        var document = new JsonObject
        {
            ["Local"] = _localQueue.ToJson(),
            ["Cluster"] = new JsonArray(queues)
        };
        string json = document.ToJsonString(_jsonOptions);
        string directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath =
            $"{_statePath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static ExecutionQueuePolicy PolicyFor(JobQueue queue)
    {
        if (queue is LocalQueue)
            return new ExecutionQueuePolicy(
                queue.Id,
                ExecutionBackendKind.Local,
                new ResourceVector(Environment.ProcessorCount, int.MaxValue, 0));

        var cluster = (ClusterQueue)queue;
        return new ExecutionQueuePolicy(
            cluster.Id,
            cluster.IsManaged
                ? ExecutionBackendKind.Managed
                : ExecutionBackendKind.ExternalScheduler,
            cluster.IsManaged
                ? new ResourceVector(
                    Math.Max(0, cluster.ManagedCores),
                    Math.Max(0, cluster.ManagedMemoryGb),
                    Math.Max(0, cluster.ManagedGpus))
                : null,
            cluster.ToJson().ToJsonString());
    }

    private static ResourceVector RequestFor(Job job, JobQueue queue)
    {
        if (queue is LocalQueue)
            return new ResourceVector(1, 0, 0);

        long cores = Math.Min(
            (long)Math.Max(0, job.ProcessCount) * Math.Max(0, job.CoreCount),
            int.MaxValue);
        return new ResourceVector(
            (int)cores,
            Math.Max(0, job.MemoryGb),
            Math.Max(0, job.GpuCount));
    }

    private static JobAddress AddressOf(Job job) =>
        new(job.Space.Project.Id, job.Space.Id, job.Id);

    private static ClusterQueue Clone(ClusterQueue queue)
    {
        var clone = new ClusterQueue();
        clone.ReadFromJson(queue.ToJson());
        return clone;
    }
}
