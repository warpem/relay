using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues;
using Refund.Jobs;
using Refund.Utils;
using Timer = System.Threading.Timer;

namespace Refund.Services.Core.Repositories;

public partial class QueueRepository
{
    #region Daemon methods

    /// <summary>
    /// Starts the daemon process that periodically checks the status of jobs in all queues.
    /// The daemon is responsible for transitioning jobs through their execution lifecycle,
    /// updating status, and tracking progress.
    /// </summary>
    /// <param name="milliseconds">The interval, in milliseconds, at which to check job status</param>
    public void StartDaemon(int milliseconds)
    {
        _daemonInterval = milliseconds;
        _daemonTimer = new Timer(RunDaemon, null, _daemonInterval, Timeout.Infinite);
    }

    /// <summary>
    /// Stops the daemon process.
    /// </summary>
    public void StopDaemon()
    {
        _daemonTimer?.Dispose();
    }

    /// <summary>
    /// Timer callback that processes all queues to check job status.
    /// Handles jobs in all queues concurrently and reschedules itself after completion.
    /// </summary>
    /// <param name="state">State object passed by the Timer (not used)</param>
    private void RunDaemon(object state)
    {
        _logger.Debug("Daemon iteration started at {Timestamp}", DateTime.Now.ToString("HH:mm:ss"));

        // Run daemon work in a separate task to avoid blocking timer
        _ = Task.Run(async () =>
        {
            try
            {
                await RunDaemonAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in daemon processing");
            }
            finally
            {
                // Reschedule the daemon if not disposed
                if (!_disposed)
                    _daemonTimer?.Change(_daemonInterval, Timeout.Infinite);

                _logger.Debug("Daemon iteration finished at {Timestamp}", DateTime.Now.ToString("HH:mm:ss"));
            }
        });
    }

    /// <summary>
    /// Async implementation of daemon processing with proper task handling
    /// </summary>
    private async Task RunDaemonAsync()
    {
        var tasks = new List<Task>();
        var maxConcurrentQueues = Math.Max(1, Environment.ProcessorCount / 2);
        using var semaphore = new SemaphoreSlim(maxConcurrentQueues);

        // Process local queue if it has jobs
        if (!_localQueue.IsEmpty)
        {
            tasks.Add(ProcessQueueJobsThrottled(_localQueue, semaphore));
        }

        // Process each non-empty cluster queue
        foreach (var queue in _clusterQueues.Where(q => !q.IsEmpty))
        {
            tasks.Add(ProcessQueueJobsThrottled(queue, semaphore));
        }

        // Wait for all queue processing to complete with timeout
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await Task.WhenAll(tasks).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Daemon processing timed out after 5 minutes");
        }
    }

    /// <summary>
    /// Processes queue jobs with semaphore throttling
    /// </summary>
    /// <param name="queue">The queue to process</param>
    /// <param name="semaphore">Semaphore for throttling</param>
    /// <returns>Task representing the operation</returns>
    private async Task ProcessQueueJobsThrottled(JobQueue queue, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            await ProcessQueueJobs(queue);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Processes all jobs in a specific queue asynchronously.
    /// Iterates through each job in the queue and handles it according to its current state.
    /// </summary>
    /// <param name="queue">The queue whose jobs should be processed</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task ProcessQueueJobs(JobQueue queue)
    {
        // Use ToArray to create a snapshot of jobs to avoid collection modification issues
        foreach (var job in queue.QueuedJobs.ToArray())
        {
            try
            {
                await ProcessJob(job, queue);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing job {JobId} in queue {QueueId}", job.Id, queue.Id);
            }
        }
    }

    /// <summary>
    /// Processes a single job based on its current status.
    /// This is the core job state machine that transitions jobs through their lifecycle.
    /// </summary>
    /// <param name="job">The job to process</param>
    /// <param name="queue">The queue containing the job</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task ProcessJob(Job job, JobQueue queue)
    {
        bool isLocalQueue = queue == _localQueue;

        try
        {
            // State machine for job processing - handle each status differently
            switch (job.Status)
            {
                case JobStatus.Waiting:
                    await HandleWaitingState(job, queue);
                    break;

                case JobStatus.Staging:
                    await HandleStagingState(job, queue);
                    break;

                case JobStatus.Running:
                    await HandleRunningState(job, queue, isLocalQueue);
                    break;

                case JobStatus.Aborting:
                    await HandleAbortingState(job, queue, isLocalQueue);
                    break;

                case JobStatus.Finalizing:
                    await HandleFinalizingState(job, queue, isLocalQueue);
                    break;

                default:
                    // For any other state (Finished, Failed, etc.), remove the job from the queue.
                    // Dissolve any worker pool first so a pooled job leaving the queue never
                    // strands its fleet (no-op for non-pooled jobs / already-dissolved pools).
                    await DissolvePool(job);

                    if (isLocalQueue)
                        DequeueLocalJob(job);
                    else
                        DequeueClusterJob(job, queue);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Log error and to job's error file
            _logger.Error(ex, "Error processing job {JobId} in {QueueType}", job.Id, queue.GetType().Name);
            await job.WriteToErrorLog($"Error processing job {job.Id} in {queue.GetType().Name}: {ex.Message}");

            // Dissolve any worker pool before failing the job. A transient cluster outage can
            // make CheckStatus/TrackJobProgress throw for a Running pooled job; without this the
            // job would be marked Failed and dequeued while its GPU worker fleet keeps running
            // (orphaned). DissolvePool swallows its own errors and is a no-op for non-pooled jobs.
            await DissolvePool(job);

            // On error, try to transition the job to Failed state
            try
            {
                _jobUpdateCallback(job, j =>
                {
                    j.AddEvent(EventType.Failed);
                    j.Status = JobStatus.Failed;
                });

                // Remove the failed job from the queue
                if (isLocalQueue)
                    DequeueLocalJob(job);
                else
                    DequeueClusterJob(job, queue);
            }
            catch (Exception updateEx)
            {
                _logger.Error(updateEx, "Error updating failed job state for job {JobId}", job.Id);
                await job.WriteToErrorLog($"Error processing job {job.Id} in {queue.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Tracks the progress of a running job by checking for log updates and result updates.
    /// Uses semaphore-controlled tasks to prevent thread explosion.
    /// </summary>
    /// <param name="job">The job to track progress for</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task TrackJobProgress(Job job)
    {
        // Check if we already have a recent progress operation for this job (within last 3 seconds)
        var now = DateTime.Now;
        if (_activeProgressOperations.TryGetValue(job, out var lastOperation) &&
            (now - lastOperation).TotalSeconds < 3)
        {
            return; // Skip if we recently processed this job
        }

        _activeProgressOperations[job] = now;

        try
        {
            // Track log progress if no task exists or previous task completed
            if (!_trackProgressLogsTasks.ContainsKey(job) || _trackProgressLogsTasks[job].IsCompleted)
            {
                var updateTask = TrackProgressWithThrottling(job, "logs", async () =>
                {
                    _logger.Debug("TrackProgressLogs called for job {JobId} at {Timestamp}", job.Id, DateTime.Now.ToString("HH:mm:ss"));

                    // Call the job's TrackProgressLogs method to check for log updates
                    if (job.TrackProgressLogs() is { } updateAction)
                        await _jobUpdateCallbackAsync(job, _ => updateAction());

                    _logger.Debug("TrackProgressLogs task finished for job {JobId} at {Timestamp}", job.Id, DateTime.Now.ToString("HH:mm:ss"));
                });

                // Store the task for tracking to avoid duplicate tracking
                lock (_trackProgressLogsTasks)
                    _trackProgressLogsTasks[job] = updateTask;
            }

            // Track results progress if no task exists or previous task completed
            if (!_trackProgressResultsTasks.ContainsKey(job) || _trackProgressResultsTasks[job].IsCompleted)
            {
                var updateTask = TrackProgressWithThrottling(job, "results", async () =>
                {
                    // Call the job's TrackProgressResults method to check for result updates
                    if (job.TrackProgressResults() is { } updateAction)
                        await _jobUpdateCallbackAsync(job, _ => updateAction());
                });

                // Store the task for tracking to avoid duplicate tracking
                lock (_trackProgressResultsTasks)
                    _trackProgressResultsTasks[job] = updateTask;
            }
        }
        finally
        {
            // Clean up old entries (older than 30 seconds) periodically
            if (now.Second % 10 == 0) // Only clean up every 10 seconds
            {
                var cutoff = now.AddSeconds(-30);
                var keysToRemove = _activeProgressOperations.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _activeProgressOperations.TryRemove(key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Executes progress tracking with semaphore throttling to prevent thread explosion
    /// </summary>
    /// <param name="job">The job being tracked</param>
    /// <param name="operationType">Type of operation for logging</param>
    /// <param name="operation">The operation to execute</param>
    /// <returns>A task representing the operation</returns>
    private Task TrackProgressWithThrottling(Job job, string operationType, Func<Task> operation)
    {
        return Task.Run(async () =>
        {
            // Try to acquire semaphore with short timeout
            var acquired = await _progressTrackingSemaphore.WaitAsync(TimeSpan.FromSeconds(1));
            if (!acquired)
            {
                _logger.Debug("Could not acquire semaphore for progress tracking of job {JobId} ({OperationType}) - skipping", job.Id, operationType);
                return;
            }

            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error tracking {OperationType} for job {JobId}", operationType, job.Id);
            }
            finally
            {
                _progressTrackingSemaphore.Release();
            }
        });
    }

    #endregion
}
