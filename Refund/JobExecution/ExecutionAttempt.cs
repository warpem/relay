namespace Refund.JobExecution;

public enum ExecutionPhase
{
    WaitingForDependencies,
    Preparing,
    Queued,
    Starting,
    Pending,
    Running,
    Cancelling,
    Stopping,
    Finalizing,
    Succeeded,
    Failed,
    Canceled,
    Interrupted
}

public static class ExecutionPhaseExtensions
{
    public static bool IsTerminal(this ExecutionPhase phase) => phase is
        ExecutionPhase.Succeeded or
        ExecutionPhase.Failed or
        ExecutionPhase.Canceled or
        ExecutionPhase.Interrupted;
}

public enum ExecutionBackendKind
{
    Local,
    Managed,
    ExternalScheduler
}

public enum ExecutionPurpose
{
    Run,
    FinalizeOnly
}

public enum ExecutionHealth
{
    Healthy,
    Indeterminate
}

public enum BackendObservationKind
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled,
    AbsentFromActiveView,
    Unreachable,
    Unparseable,
    Indeterminate
}

public enum ExecutionOutcome
{
    Succeeded,
    Failed,
    Canceled,
    Interrupted
}

public enum WorkerPhase
{
    Starting,
    Pending,
    Running,
    Indeterminate,
    Cancelling,
    Ended
}

public readonly record struct JobAddress(int ProjectId, int SpaceId, int JobId);

public readonly record struct ResourceVector(int Cores, int MemoryGb, int Gpus)
{
    public static ResourceVector None => new(0, 0, 0);

    public bool IsValid => Cores >= 0 && MemoryGb >= 0 && Gpus >= 0;

    public bool FitsWithin(ResourceVector capacity) =>
        Cores <= capacity.Cores && MemoryGb <= capacity.MemoryGb && Gpus <= capacity.Gpus;

    public static ResourceVector operator +(ResourceVector left, ResourceVector right) =>
        new(left.Cores + right.Cores, left.MemoryGb + right.MemoryGb, left.Gpus + right.Gpus);

    public static ResourceVector operator -(ResourceVector left, ResourceVector right) =>
        new(left.Cores - right.Cores, left.MemoryGb - right.MemoryGb, left.Gpus - right.Gpus);
}

public sealed record ExecutionQueuePolicy(
    int QueueId,
    ExecutionBackendKind BackendKind,
    ResourceVector? Capacity = null,
    string BackendConfiguration = null);

public sealed record ExecutionHistoryEntry(
    ExecutionPhase Phase,
    DateTimeOffset Timestamp,
    string Detail = null);

public sealed record ExecutionAttemptSnapshot(
    Guid Id,
    JobAddress Job,
    int QueueId,
    ExecutionBackendKind BackendKind,
    string BackendConfiguration,
    string WorkerBackendConfiguration,
    ExecutionPurpose Purpose,
    ResourceVector ResourceRequest,
    ExecutionPhase Phase,
    ExecutionHealth Health,
    string HealthDetail,
    long? EnqueueSequence,
    bool HasAllocation,
    int[] GpuIndices,
    BackendReceipt Receipt,
    ExecutionOutcome? PendingOutcome,
    WorkerGroupSnapshot WorkerGroup,
    DateTimeOffset CreatedAt,
    ExecutionHistoryEntry[] History);

public sealed record ExecutionCoordinatorSnapshot(
    long NextSequence,
    ExecutionAttemptSnapshot[] Attempts);

public sealed record BackendReceipt(string Id);

public sealed record BackendObservation(BackendObservationKind Kind, string Detail = null);

public sealed record WorkerGroupRequest(
    int QueueId,
    int DesiredCount,
    int SubmissionLimit);

public sealed record WorkerLaunchSnapshot(
    Guid OperationId,
    WorkerPhase Phase,
    BackendReceipt Receipt);

public sealed record WorkerGroupSnapshot(
    int QueueId,
    int DesiredCount,
    int SubmissionLimit,
    int TotalSubmissions,
    bool Prepared,
    WorkerLaunchSnapshot[] Workers);

public sealed record WorkerObservation(
    string ReceiptId,
    BackendObservationKind Kind,
    string Detail = null);

public abstract record ExecutionEffect(Guid AttemptId);

public sealed record PrepareExecution(Guid AttemptId) : ExecutionEffect(AttemptId);

public sealed record StartExecution(Guid AttemptId, IReadOnlyList<int> GpuIndices)
    : ExecutionEffect(AttemptId);

public sealed record ActivateExecution(Guid AttemptId) : ExecutionEffect(AttemptId);

public sealed record CancelExecution(Guid AttemptId, BackendReceipt Receipt)
    : ExecutionEffect(AttemptId);

public sealed record FinalizeExecution(Guid AttemptId, ExecutionOutcome Outcome)
    : ExecutionEffect(AttemptId);

public sealed record StartWorker(Guid AttemptId, Guid OperationId) : ExecutionEffect(AttemptId);

public sealed record CancelWorkers(Guid AttemptId, IReadOnlyList<BackendReceipt> Receipts)
    : ExecutionEffect(AttemptId);

public sealed class WorkerGroupState
{
    private readonly List<WorkerLaunchState> _workers = new();

    internal WorkerGroupState(WorkerGroupRequest request)
    {
        if (request.DesiredCount < 0)
            throw new InvalidOperationException("Worker count cannot be negative.");
        if (request.SubmissionLimit < request.DesiredCount)
            throw new InvalidOperationException("Worker submission limit cannot be below the desired count.");

        QueueId = request.QueueId;
        DesiredCount = request.DesiredCount;
        SubmissionLimit = request.SubmissionLimit;
    }

    internal WorkerGroupState(WorkerGroupSnapshot snapshot)
    {
        QueueId = snapshot.QueueId;
        DesiredCount = snapshot.DesiredCount;
        SubmissionLimit = snapshot.SubmissionLimit;
        TotalSubmissions = snapshot.TotalSubmissions;
        Prepared = snapshot.Prepared;
        _workers.AddRange((snapshot.Workers ?? Array.Empty<WorkerLaunchSnapshot>())
            .Select(worker => new WorkerLaunchState(worker)));
    }

    public int QueueId { get; }
    public int DesiredCount { get; internal set; }
    public int SubmissionLimit { get; }
    public int TotalSubmissions { get; internal set; }
    public bool Prepared { get; internal set; }
    public IReadOnlyList<WorkerLaunchState> Workers => _workers;
    public int AliveCount => _workers.Count(worker => worker.Phase != WorkerPhase.Ended);
    public int RunningCount => _workers.Count(worker => worker.Phase == WorkerPhase.Running);

    internal WorkerLaunchState AddStartingWorker()
    {
        var worker = new WorkerLaunchState(Guid.NewGuid());
        _workers.Add(worker);
        TotalSubmissions++;
        return worker;
    }

    internal WorkerGroupSnapshot CreateSnapshot() => new(
        QueueId,
        DesiredCount,
        SubmissionLimit,
        TotalSubmissions,
        Prepared,
        _workers.Select(worker => worker.CreateSnapshot()).ToArray());
}

public sealed class WorkerLaunchState
{
    internal WorkerLaunchState(Guid operationId)
    {
        OperationId = operationId;
        Phase = WorkerPhase.Starting;
    }

    internal WorkerLaunchState(WorkerLaunchSnapshot snapshot)
    {
        OperationId = snapshot.OperationId;
        Phase = snapshot.Phase;
        Receipt = snapshot.Receipt;
    }

    public Guid OperationId { get; }
    public WorkerPhase Phase { get; internal set; }
    public BackendReceipt Receipt { get; internal set; }

    internal WorkerLaunchSnapshot CreateSnapshot() => new(OperationId, Phase, Receipt);
}

public sealed class ExecutionAttempt
{
    private readonly List<ExecutionHistoryEntry> _history = new();

    internal ExecutionAttempt(
        Guid id,
        JobAddress job,
        int queueId,
        ExecutionBackendKind backendKind,
        string backendConfiguration,
        string workerBackendConfiguration,
        ExecutionPurpose purpose,
        ResourceVector resourceRequest,
        WorkerGroupRequest workerGroup,
        ExecutionPhase phase,
        DateTimeOffset createdAt)
    {
        Id = id;
        Job = job;
        QueueId = queueId;
        BackendKind = backendKind;
        BackendConfiguration = backendConfiguration;
        WorkerBackendConfiguration = workerBackendConfiguration;
        Purpose = purpose;
        ResourceRequest = resourceRequest;
        WorkerGroup = workerGroup == null ? null : new WorkerGroupState(workerGroup);
        Phase = phase;
        CreatedAt = createdAt;
        _history.Add(new ExecutionHistoryEntry(phase, createdAt));
    }

    internal ExecutionAttempt(ExecutionAttemptSnapshot snapshot)
    {
        Id = snapshot.Id;
        Job = snapshot.Job;
        QueueId = snapshot.QueueId;
        BackendKind = snapshot.BackendKind;
        BackendConfiguration = snapshot.BackendConfiguration;
        WorkerBackendConfiguration = snapshot.WorkerBackendConfiguration;
        Purpose = snapshot.Purpose;
        ResourceRequest = snapshot.ResourceRequest;
        Phase = snapshot.Phase;
        Health = snapshot.Health;
        HealthDetail = snapshot.HealthDetail;
        EnqueueSequence = snapshot.EnqueueSequence;
        HasAllocation = snapshot.HasAllocation;
        GpuIndices = snapshot.GpuIndices ?? Array.Empty<int>();
        Receipt = snapshot.Receipt;
        PendingOutcome = snapshot.PendingOutcome;
        WorkerGroup = snapshot.WorkerGroup == null ? null : new WorkerGroupState(snapshot.WorkerGroup);
        CreatedAt = snapshot.CreatedAt;
        _history.AddRange(snapshot.History ?? Array.Empty<ExecutionHistoryEntry>());

        if (_history.Count == 0)
            _history.Add(new ExecutionHistoryEntry(Phase, CreatedAt));
    }

    public Guid Id { get; }
    public JobAddress Job { get; }
    public int QueueId { get; }
    public ExecutionBackendKind BackendKind { get; }
    public string BackendConfiguration { get; }
    public string WorkerBackendConfiguration { get; }
    public ExecutionPurpose Purpose { get; }
    public ResourceVector ResourceRequest { get; }
    public ExecutionPhase Phase { get; internal set; }
    public ExecutionHealth Health { get; internal set; } = ExecutionHealth.Healthy;
    public string HealthDetail { get; internal set; }
    public long? EnqueueSequence { get; internal set; }
    public bool HasAllocation { get; internal set; }
    public IReadOnlyList<int> GpuIndices { get; internal set; } = Array.Empty<int>();
    public BackendReceipt Receipt { get; internal set; }
    public ExecutionOutcome? PendingOutcome { get; internal set; }
    public WorkerGroupState WorkerGroup { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<ExecutionHistoryEntry> History => _history;

    public bool IsTerminal => Phase.IsTerminal();

    internal void TransitionTo(ExecutionPhase phase, DateTimeOffset timestamp, string detail = null)
    {
        if (Phase == phase)
            return;

        Phase = phase;
        _history.Add(new ExecutionHistoryEntry(phase, timestamp, detail));
    }

    internal ExecutionAttemptSnapshot CreateSnapshot() => new(
        Id,
        Job,
        QueueId,
        BackendKind,
        BackendConfiguration,
        WorkerBackendConfiguration,
        Purpose,
        ResourceRequest,
        Phase,
        Health,
        HealthDetail,
        EnqueueSequence,
        HasAllocation,
        GpuIndices.ToArray(),
        Receipt,
        PendingOutcome,
        WorkerGroup?.CreateSnapshot(),
        CreatedAt,
        _history.ToArray());
}
