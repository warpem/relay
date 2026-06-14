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
    /// <summary>
    /// Handles a job in the Waiting state by checking if it's ready to be staged.
    /// If ready, transitions the job to Staging state and submits it to the queue for execution.
    /// </summary>
    /// <param name="job">The job in Waiting state</param>
    /// <param name="queue">The queue the job is in</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleWaitingState(Job job, JobQueue queue)
    {
        // Check if all job prerequisites are met
        if (job.IsReadyToStage())
        {
            try
            {
                await job.WriteToLifecycleLog($"Staging started");

                // Transition to Staging state
                _jobUpdateCallback(job, j =>
                {
                    j.Status = JobStatus.Staging;
                    j.AddEvent(EventType.StagingStarted);
                });

                // Submit the job to the queue for execution
                lock (_saveLock)
                {
                    queue.SubmitJob(job);
                }

                _logger.Information("Job {JobId} successfully transitioned from Waiting to Staging and submitted to {QueueType} (alias: {JobAlias})",
                                    job.Id, queue.GetType().Name, job.Alias);
            }
            catch (Exception exc)
            {
                await job.WriteToErrorLog(exc.ToString());
                _logger.Error("Job {JobId} failed to transition from Waiting to Staging and submitted to {QueueType} (alias: {JobAlias})\n{exception}",
                              job.Id, queue.GetType().Name, job.Alias, exc.ToString());
            }
        }
    }

    /// <summary>
    /// Handles a job in the Staging state by checking its status on the cluster or local machine.
    /// If the status has changed, transitions the job to the appropriate state (Running, Failed, or Finished).
    /// </summary>
    /// <param name="job">The job in Staging state</param>
    /// <param name="queue">The queue the job is in</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleStagingState(Job job, JobQueue queue)
    {
        // Check the job's status on the cluster or local machine
        (var clusterStatus, var output) = await queue.CheckStatus(job);

        // If the status is no longer Pending or Unknown, update the job state
        if (clusterStatus != ClusterJobStatus.Pending && clusterStatus != ClusterJobStatus.Unknown)
        {
            await job.WriteToLifecycleLog($"Staging finished with status {clusterStatus} and output:\n{output}\n");

            await job.WriteToLifecycleLog($"Job running");

            var updateAction = job.TrackProgressLogs();

            // If the job already finished or failed (e.g. fast cluster jobs that complete
            // before the daemon ever sees them as Running), route through HandleJobCompletion
            // so final progress tracking (logs + results) is performed properly.
            if (clusterStatus == ClusterJobStatus.Finished || clusterStatus == ClusterJobStatus.Failed)
            {
                bool isLocalQueue = queue == _localQueue;

                _jobUpdateCallback(job, j =>
                {
                    j.AddEvent(EventType.RunningStarted);
                    updateAction?.Invoke();
                    j.Status = JobStatus.Running;
                });

                await HandleJobCompletion(job, queue, isLocalQueue, clusterStatus);
            }
            else
            {
                _jobUpdateCallback(job, j =>
                {
                    j.AddEvent(EventType.RunningStarted);
                    updateAction?.Invoke();
                    j.Status = JobStatus.Running;
                });

                _logger.Information("Job {JobId} successfully transitioned from Staging to Running (alias: {JobAlias})",
                    job.Id, job.Alias);
            }
        }
    }

    /// <summary>
    /// Handles a job in the Running state by checking if it's still running.
    /// If still running, tracks its progress. If completed, transitions to appropriate completion state.
    /// </summary>
    /// <param name="job">The job in Running state</param>
    /// <param name="queue">The queue the job is in</param>
    /// <param name="isLocalQueue">Whether the job is in the local queue</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleRunningState(Job job, JobQueue queue, bool isLocalQueue)
    {
        // Check the job's status on the cluster or local machine
        (var clusterStatus, _) = await queue.CheckStatus(job);

        if (clusterStatus != ClusterJobStatus.Running)
        {
            await job.WriteToLifecycleLog($"Job status is no longer running: {clusterStatus}, initiating completion");

            // Job is no longer running - handle completion
            await HandleJobCompletion(job, queue, isLocalQueue, clusterStatus);
        }
        else
        {
            // Job is still running - track its progress
            await TrackJobProgress(job);
        }
    }

    /// <summary>
    /// Handles the completion of a job by updating its status, finalizing progress tracking,
    /// and removing it from the queue.
    /// </summary>
    /// <param name="job">The job that has completed</param>
    /// <param name="queue">The queue the job is in</param>
    /// <param name="isLocalQueue">Whether the job is in the local queue</param>
    /// <param name="clusterStatus">The final status reported by the cluster or local machine</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleJobCompletion(Job job, JobQueue queue, bool isLocalQueue, ClusterJobStatus clusterStatus)
    {
        // Update the job status based on the cluster status
        _jobUpdateCallback(job, j =>
        {
            if (clusterStatus == ClusterJobStatus.Failed ||
                clusterStatus == ClusterJobStatus.Unknown)
            {
                j.Status = JobStatus.Failed;
                j.AddEvent(EventType.Failed);
            }
            else if (clusterStatus == ClusterJobStatus.Finished)
            {
                j.Status = JobStatus.Finished;
                j.AddEvent(EventType.Finished);

                // Log successful completion
                _logger.Information("Job {JobId} completed successfully in {QueueType} queue (alias: {JobAlias})",
                    job.Id, isLocalQueue ? "local" : "cluster", job.Alias);
            }
            else
                throw new Exception($"Unexpected cluster job status: {clusterStatus}");
        });

        // Wait for any previous progress tracking tasks to complete and run final updates
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            // Wait for log tracking to complete with timeout
            if (_trackProgressLogsTasks.TryGetValue(job, out var logsTask) && !logsTask.IsCompleted)
            {
                try
                {
                    await logsTask.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("Timed out waiting for log tracking completion for job {JobId}", job.Id);
                }
            }

            // Wait for results tracking to complete with timeout
            if (_trackProgressResultsTasks.TryGetValue(job, out var resultsTask) && !resultsTask.IsCompleted)
            {
                try
                {
                    await resultsTask.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("Timed out waiting for results tracking completion for job {JobId}", job.Id);
                }
            }

            // Final progress updates for logs
            if (job.TrackProgressLogs() is { } updateActionLogs)
                _jobUpdateCallback(job, _ => updateActionLogs());

            // Final progress updates for results - process all pending results
            while (job.TrackProgressResults() is { } updateActionResults)
            {
                var action = updateActionResults;
                _jobUpdateCallback(job, _ => action());
            }

            await job.WriteToLifecycleLog($"Job completed");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in final progress tracking for job {JobId}", job.Id);

            try
            {
                await job.WriteToLifecycleLog($"Error in final progress tracking: {ex.Message}");
            }
            catch (Exception logEx)
            {
                _logger.Error(logEx, "Failed to write error to lifecycle log for job {JobId}", job.Id);
            }
        }

        // Remove the job from the queue
        if (isLocalQueue)
            DequeueLocalJob(job);
        else
            DequeueClusterJob(job, queue);
    }

    /// <summary>
    /// Handles a job in the Aborting state by checking its status on the cluster or local machine.
    /// If the job has completed or failed, updates its status and removes it from the queue.
    /// </summary>
    /// <param name="job">The job in Aborting state</param>
    /// <param name="queue">The queue the job is in</param>
    /// <param name="isLocalQueue">Whether the job is in the local queue</param>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleAbortingState(Job job, JobQueue queue, bool isLocalQueue)
    {
        // Check the job's status on the cluster or local machine
        (var clusterStatus, _) = await queue.CheckStatus(job);

        if (clusterStatus != ClusterJobStatus.Failed &&
            clusterStatus != ClusterJobStatus.Finished &&
            !string.IsNullOrWhiteSpace(job.ClusterJobId))
            queue.AbortJob(job);

        // Check the job's status again after aborting
        (clusterStatus, _) = await queue.CheckStatus(job);

        // If the status is no longer Pending or Unknown, finalize the abort
        if ((clusterStatus != ClusterJobStatus.Pending && clusterStatus != ClusterJobStatus.Running) ||
            (DateTime.Now - job.GetMostRecentEvent().Timestamp).TotalSeconds > 30)
        {
            await job.WriteToLifecycleLog($"Job aborted with status {clusterStatus}");

            // Aggregate any remaining stderr into error.txt before marking as aborted
            try
            {
                if (job.TrackProgressLogs() is { } updateAction)
                    _jobUpdateCallback(job, _ => updateAction());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in final progress tracking for aborted job {JobId}", job.Id);
            }

            _jobUpdateCallback(job, j =>
            {
                // Remove from queue first to avoid race conditions
                if (isLocalQueue)
                    DequeueLocalJob(job);
                else
                    DequeueClusterJob(job, queue);

                // Update job status
                j.Status = JobStatus.Aborted;
                j.AddEvent(EventType.Aborted);
            });

            _logger.Information("Job {JobId} successfully aborted in {QueueType} queue (alias: {JobAlias})",
                job.Id, isLocalQueue ? "local" : "cluster", job.Alias);
        }
    }

    private async Task HandleFinalizingState(Job job, JobQueue queue, bool isLocalQueue)
    {
        if (_finalizationTasks.TryGetValue(job, out var task) && !task.IsCompleted)
            return;

        _finalizationTasks[job] = Task.Run(async () =>
        {
            try
            {
                await job.WriteToLifecycleLog("Finalizing job");

                job.FinalizeRun(_jobUpdateCallback);

                await job.WriteToLifecycleLog("Job finalized, you should now be able to use its outputs");

                _jobUpdateCallback(job, j =>
                {
                    j.Status = JobStatus.Finished;
                    j.AddEvent(EventType.Finished);
                });
            }
            catch (Exception ex)
            {
                _jobUpdateCallback(job, j =>
                {
                    j.Status = JobStatus.Failed;
                    j.AddEvent(EventType.Failed);
                });

                await job.WriteToLifecycleLog("Couldn't finalize job:\n" +
                                              $"{ex.Message}");
                _logger.Error(ex, "Error finalizing job {JobId}", job.Id);
            }
            finally
            {
                if (isLocalQueue)
                    DequeueLocalJob(job);
                else
                    DequeueClusterJob(job, queue);

                _finalizationTasks.TryRemove(job, out _);
            }
        });
    }
}
