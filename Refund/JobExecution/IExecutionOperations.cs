namespace Refund.JobExecution;

public sealed record BackendStartResult(
    BackendReceipt Receipt,
    bool IsRunning);

public sealed class IndeterminateBackendStartException : Exception
{
    public IndeterminateBackendStartException(string message, Exception innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IExecutionOperations
{
    bool DependenciesReady(ExecutionAttemptSnapshot attempt);

    Task PrepareAsync(ExecutionAttemptSnapshot attempt, CancellationToken cancellationToken);

    Task<BackendStartResult> StartAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<int> gpuIndices,
        CancellationToken cancellationToken);

    Task ActivateAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken);

    Task<BackendObservation> ObserveAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken);

    Task<BackendObservation> CancelAsync(
        ExecutionAttemptSnapshot attempt,
        BackendReceipt receipt,
        CancellationToken cancellationToken);

    Task FinalizeAsync(
        ExecutionAttemptSnapshot attempt,
        ExecutionOutcome outcome,
        CancellationToken cancellationToken);

    Task<BackendStartResult> StartWorkerAsync(
        ExecutionAttemptSnapshot attempt,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkerObservation>> ObserveWorkersAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<BackendReceipt> receipts,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkerObservation>> CancelWorkersAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<BackendReceipt> receipts,
        CancellationToken cancellationToken);

    Task TrackProgressAsync(ExecutionAttemptSnapshot attempt, CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}

public interface IExecutionStateStore
{
    Task<ExecutionCoordinatorSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ExecutionCoordinatorSnapshot snapshot, CancellationToken cancellationToken);
}
