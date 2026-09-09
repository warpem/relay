using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobQueues;
using Serilog;

namespace Refund.JobExecution;

public sealed class RelayExecutionOperations : IExecutionOperations
{
    private readonly Func<JobAddress, Job> _findJob;
    private readonly Func<Job, Action<Job>, Task> _updateJob;
    private readonly ManagedExecutionHost _managed = new();
    private readonly ConcurrentDictionary<Guid, LocalExecution> _local = new();
    private readonly object _localGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger _logger = Log.ForContext<RelayExecutionOperations>();

    public RelayExecutionOperations(
        Func<JobAddress, Job> findJob,
        Func<Job, Action<Job>, Task> updateJob)
    {
        _findJob = findJob;
        _updateJob = updateJob;
    }

    public bool DependenciesReady(ExecutionAttemptSnapshot attempt) =>
        FindJob(attempt).IsReadyToStage();

    public async Task PrepareAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        var job = FindJob(attempt);
        try
        {
            if (attempt.BackendKind == ExecutionBackendKind.Local)
                await PrepareLocalAsync(job, cancellationToken);
            else
            {
                var queue = Queue(attempt.BackendConfiguration);
                queue.ValidateSubmissionConfiguration();
                await queue.PrepareAndWriteScript(job, attempt.Id);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (attempt.WorkerGroup != null)
                PrepareWorkerGroup(attempt, job);
        }
        catch (Exception exception)
        {
            await job.WriteToErrorLog($"Job preparation failed:\n{exception}");
            throw;
        }
    }

    public async Task<BackendStartResult> StartAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<int> gpuIndices,
        CancellationToken cancellationToken)
    {
        var job = FindJob(attempt);
        return attempt.BackendKind switch
        {
            ExecutionBackendKind.Local => StartLocal(attempt, job),
            ExecutionBackendKind.Managed => await _managed.StartAsync(
                attempt,
                SubmissionScriptPath(job),
                job.RunDirectory,
                job.PathStdOut,
                job.PathStdErr,
                gpuIndices,
                cancellationToken),
            ExecutionBackendKind.ExternalScheduler => await StartExternalAsync(attempt, job),
            _ => throw new ArgumentOutOfRangeException(nameof(attempt.BackendKind))
        };
    }

    public Task ActivateAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken) =>
        attempt.BackendKind == ExecutionBackendKind.Managed
            ? _managed.ActivateAsync(attempt, cancellationToken)
            : throw new InvalidOperationException(
                $"The {attempt.BackendKind} backend does not require activation.");

    public async Task<BackendObservation> ObserveAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        return attempt.BackendKind switch
        {
            ExecutionBackendKind.Local => ObserveLocal(attempt),
            ExecutionBackendKind.Managed => _managed.Observe(attempt),
            ExecutionBackendKind.ExternalScheduler =>
                await Queue(attempt.BackendConfiguration).ObserveReceipt(attempt.Receipt.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(attempt.BackendKind))
        };
    }

    public async Task<BackendObservation> CancelAsync(
        ExecutionAttemptSnapshot attempt,
        BackendReceipt receipt,
        CancellationToken cancellationToken)
    {
        switch (attempt.BackendKind)
        {
            case ExecutionBackendKind.Local:
                return await CancelLocalAsync(attempt, cancellationToken);
            case ExecutionBackendKind.Managed:
                return await _managed.CancelAsync(attempt, cancellationToken);
            case ExecutionBackendKind.ExternalScheduler:
                await Queue(attempt.BackendConfiguration).CancelReceipt(receipt.Id);
                return new BackendObservation(
                    BackendObservationKind.Indeterminate,
                    "The scheduler accepted the cancellation request.");
            default:
                throw new ArgumentOutOfRangeException(nameof(attempt.BackendKind));
        }
    }

    public async Task FinalizeAsync(
        ExecutionAttemptSnapshot attempt,
        ExecutionOutcome outcome,
        CancellationToken cancellationToken)
    {
        var job = FindJob(attempt);
        if (attempt.Purpose == ExecutionPurpose.FinalizeOnly)
        {
            await job.WriteToLifecycleLog("Finalizing job");
            job.FinalizeRun((updatedJob, action) =>
                _updateJob(updatedJob, action).GetAwaiter().GetResult());
            await job.WriteToLifecycleLog("Job finalization finished");
            return;
        }

        if (job.TrackProgressLogs() is { } logs)
            await _updateJob(job, _ => logs());
        while (job.TrackProgressResults() is { } results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _updateJob(job, _ => results());
        }

        await job.WriteToLifecycleLog(outcome switch
        {
            ExecutionOutcome.Succeeded => "Job completed",
            ExecutionOutcome.Canceled => "Job canceled",
            ExecutionOutcome.Interrupted => "Job interrupted",
            _ => "Job failed"
        });
    }

    public async Task<BackendStartResult> StartWorkerAsync(
        ExecutionAttemptSnapshot attempt,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var job = FindJob(attempt);
        var queue = Queue(attempt.WorkerBackendConfiguration);
        string receipt;
        try
        {
            receipt = await queue.SubmitScript(WorkerScriptPath(job));
        }
        catch (Exception exception)
        {
            throw new IndeterminateBackendStartException(
                "The worker submission may have reached the scheduler, but no receipt was obtained.",
                exception);
        }
        await WriteSubmissionLogAsync(
            job,
            $"Worker submitted with scheduler ID {receipt}");
        return new BackendStartResult(new BackendReceipt(receipt), false);
    }

    public async Task<IReadOnlyList<WorkerObservation>> ObserveWorkersAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<BackendReceipt> receipts,
        CancellationToken cancellationToken)
    {
        var queue = Queue(attempt.WorkerBackendConfiguration);
        var observed = await queue.ObserveReceipts(receipts.Select(receipt => receipt.Id));
        var observations = new List<WorkerObservation>(receipts.Count);

        foreach (var receipt in receipts)
        {
            var observation = observed[receipt.Id];
            observations.Add(new WorkerObservation(
                receipt.Id, observation.Kind, observation.Detail));
        }

        return observations;
    }

    public async Task<IReadOnlyList<WorkerObservation>> CancelWorkersAsync(
        ExecutionAttemptSnapshot attempt,
        IReadOnlyList<BackendReceipt> receipts,
        CancellationToken cancellationToken)
    {
        var queue = Queue(attempt.WorkerBackendConfiguration);
        await queue.CancelReceipts(receipts.Select(receipt => receipt.Id));
        return receipts.Select(receipt => new WorkerObservation(
            receipt.Id,
            BackendObservationKind.Indeterminate,
            "The scheduler accepted the worker cancellation request.")).ToArray();
    }

    public async Task TrackProgressAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        var job = FindJob(attempt);
        if (job.TrackProgressLogs() is { } logs)
            await _updateJob(job, _ => logs());
        if (job.TrackProgressResults() is { } results)
            await _updateJob(job, _ => results());
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        LocalExecution[] localExecutions;
        lock (_localGate)
        {
            _shutdown.Cancel();
            localExecutions = _local.Values.ToArray();
        }

        await _managed.ShutdownAsync(cancellationToken);
        var localTasks = localExecutions.Select(execution => execution.Task).ToArray();
        try
        {
            await Task.WhenAll(localTasks)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task PrepareLocalAsync(Job job, CancellationToken cancellationToken)
    {
        job.ResetWorkingDirectory();
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        await job.WriteToLifecycleLog("Preparation started");
        cancellationToken.ThrowIfCancellationRequested();
        job.Stage();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private BackendStartResult StartLocal(ExecutionAttemptSnapshot attempt, Job job)
    {
        if (job is not ILocalJob localJob)
            throw new InvalidOperationException($"Job {job.Id} does not support local execution.");

        lock (_localGate)
        {
            if (_shutdown.IsCancellationRequested)
                throw new InvalidOperationException("Local execution is shutting down.");

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var task = Task.Run(async () =>
            {
                try
                {
                    localJob.RunLocal(cancellation.Token);
                }
                catch (Exception exception)
                {
                    await job.WriteToErrorLog(exception.ToString());
                    throw;
                }
            }, CancellationToken.None);
            if (!_local.TryAdd(attempt.Id, new LocalExecution(task, cancellation)))
            {
                cancellation.Cancel();
                cancellation.Dispose();
                throw new InvalidOperationException(
                    $"Attempt {attempt.Id} already has a local execution.");
            }

            return new BackendStartResult(new BackendReceipt(attempt.Id.ToString()), true);
        }
    }

    private BackendObservation ObserveLocal(ExecutionAttemptSnapshot attempt)
    {
        if (!_local.TryGetValue(attempt.Id, out var execution))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The local task is not owned by this Relay process.");
        if (!execution.Task.IsCompleted)
            return new BackendObservation(BackendObservationKind.Running);

        _local.TryRemove(attempt.Id, out _);
        execution.Cancellation.Dispose();
        if (execution.Task.IsCanceled)
            return new BackendObservation(BackendObservationKind.Canceled);
        if (execution.Task.Exception != null)
            return new BackendObservation(
                BackendObservationKind.Failed,
                execution.Task.Exception.GetBaseException().Message);
        return new BackendObservation(BackendObservationKind.Succeeded);
    }

    private async Task<BackendObservation> CancelLocalAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        if (!_local.TryGetValue(attempt.Id, out var execution))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The local task is not owned by this Relay process.");

        execution.Cancellation.Cancel();
        if (!execution.Task.IsCompleted)
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "Cancellation was requested; the local task is still exiting.");

        return ObserveLocal(attempt);
    }

    private async Task<BackendStartResult> StartExternalAsync(
        ExecutionAttemptSnapshot attempt,
        Job job)
    {
        string rawOutput = null;
        var queue = Queue(attempt.BackendConfiguration);
        string receipt;
        try
        {
            receipt = await queue.SubmitScript(
                SubmissionScriptPath(job), output => rawOutput = output);
        }
        catch (Exception exception)
        {
            throw new IndeterminateBackendStartException(
                "The submission may have reached the scheduler, but no receipt was obtained.",
                exception);
        }
        if (!string.IsNullOrWhiteSpace(rawOutput))
            await WriteSubmissionLogAsync(job, rawOutput);
        await WriteSubmissionLogAsync(job, $"Scheduler receipt: {receipt}");
        return new BackendStartResult(new BackendReceipt(receipt), false);
    }

    private async Task WriteSubmissionLogAsync(Job job, string message)
    {
        try
        {
            await job.WriteToLifecycleLog(message);
        }
        catch (Exception exception)
        {
            _logger.Warning(
                exception,
                "Could not record scheduler submission for job {JobId}",
                job.Id);
        }
    }

    private static void PrepareWorkerGroup(ExecutionAttemptSnapshot attempt, Job job)
    {
        if (job is not IPooledJob pooledJob)
            throw new InvalidOperationException($"Job {job.Id} does not define worker commands.");

        var queue = Queue(attempt.WorkerBackendConfiguration);
        queue.ValidateSubmissionConfiguration();
        string logs = Path.Combine(job.DirectoryPath, "worker_logs");
        Directory.CreateDirectory(logs);
        var resourceValues = pooledJob.GetWorkerResourceValues(logs);
        resourceValues["attempt_id"] = attempt.Id.ToString("D");
        queue.BuildWorkerScript(
            pooledJob.GetWorkerCommand(0),
            resourceValues,
            pooledJob.WorkerRequiredModules,
            WorkerScriptPath(job));
    }

    private Job FindJob(ExecutionAttemptSnapshot attempt) =>
        _findJob(attempt.Job)
        ?? throw new InvalidOperationException(
            $"Job {attempt.Job.ProjectId}/{attempt.Job.SpaceId}/{attempt.Job.JobId} no longer exists.");

    private static ClusterQueue Queue(string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
            throw new InvalidOperationException("The attempt has no queue configuration snapshot.");

        var node = JsonNode.Parse(configuration)
                   ?? throw new InvalidDataException("The queue configuration snapshot is invalid JSON.");
        var queue = new ClusterQueue();
        queue.ReadFromJson(node);
        return queue;
    }

    private static string SubmissionScriptPath(Job job) =>
        Path.Combine(job.DirectoryPath, "submit.sh");

    private static string WorkerScriptPath(Job job) =>
        Path.Combine(job.DirectoryPath, "worker_submit.sh");

    private sealed record LocalExecution(Task Task, CancellationTokenSource Cancellation);
}
