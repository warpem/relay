using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues.ReadOnly;

namespace Refund.JobQueues;

/// <summary>
/// Manages job submission and execution on the local machine.
/// Executes jobs directly in separate threads, handling their lifecycle
/// from staging to completion without requiring a cluster scheduler.
/// </summary>
/// <remarks>
/// The LocalQueue is a central component in the job execution system:
/// 
/// 1. It's instantiated as a singleton in QueueRepository, with a unique ID of -1
///    and QueueType of JobQueueType.Local
/// 2. It's used for executing jobs that don't require cluster resources
/// 3. It follows the same interface pattern as ClusterQueue, allowing the system
///    to handle both local and cluster jobs through the same QueueRepository methods
/// 4. It's exposed as an immutable ReadOnlyLocalQueue instance via AsReadOnly() method
///    to prevent external code from modifying its state
/// </remarks>
public class LocalQueue : JobQueue
{
    private static readonly ConditionalWeakTable<LocalQueue, ReadOnlyLocalQueue> ReadOnlyCache = new();
    
    /// <summary>
    /// Tracks the current status of jobs that are in the queue
    /// </summary>
    private Dictionary<Job, ClusterJobStatus> JobStatuses = new();
    
    /// <summary>
    /// Set of jobs that have completed successfully
    /// </summary>
    private HashSet<Job> FinishedJobs = new();
    
    /// <summary>
    /// Set of jobs that have failed during execution
    /// </summary>
    private HashSet<Job> FailedJobs = new();
    
    /// <summary>
    /// Tracks currently running jobs with their cancellation tokens
    /// to support aborting jobs on demand
    /// </summary>
    private Dictionary<Job, CancellationTokenSource> RunningJobs = new();
    
    /// <summary>
    /// Semaphore to limit concurrent local job executions
    /// </summary>
    private readonly SemaphoreSlim _localJobSemaphore = new(Environment.ProcessorCount);

    /// <summary>
    /// Initializes a new instance of the LocalQueue class with job update callback.
    /// </summary>
    /// <param name="jobUpdateCallback">Callback function to update job state in the data model</param>
    public LocalQueue(Action<Job, Action<Job>> jobUpdateCallback) : base(jobUpdateCallback)
    {
        Alias = "Local Queue";
    }
    
    /// <summary>
    /// Submits a job for local execution.
    /// Sets up the job environment and starts execution in a separate thread.
    /// </summary>
    /// <param name="job">The job to submit for local execution</param>
    /// <param name="customValues">Optional dictionary of custom values (not used in LocalQueue)</param>
    /// <remarks>
    /// The job must implement the ILocalJob interface to be executed locally.
    /// The execution follows these steps:
    /// 1. Prepare job directory
    /// 2. Call job's Stage method to prepare input files
    /// 3. Call job's RunLocal method to execute the main processing
    /// 4. Update job status based on success or failure
    /// 
    /// Execution happens in a separate thread to avoid blocking the main thread.
    /// 
    /// This method is called from QueueRepository when jobs are submitted for execution,
    /// typically under lock (_saveLock). It first calls base.SubmitJob() to perform any 
    /// common job submission operations defined in JobQueue. This pattern is mirrored by 
    /// ClusterQueue.SubmitJob, which ensures consistent job submission behavior regardless
    /// of queue type.
    /// </remarks>
    public override void SubmitJob(Job job, Dictionary<string, string> customValues = null)
    {
        base.SubmitJob(job);
        
        // JobUpdateCallback(job, (j) =>
        // {
        //     j.Status = JobStatus.Staging;
        // });
        
        JobStatuses.Add(job, ClusterJobStatus.Pending);
        FailedJobs.Remove(job);
        CancellationTokenSource cts = new();
        
        var executionTask = Task.Run(async () =>
        {
            // Acquire semaphore to limit concurrent local jobs
            await _localJobSemaphore.WaitAsync(cts.Token);
            
            try
            {
                // Set up job directory
                job.DirectoryName = job.Id.ToString();

                if (Directory.Exists(job.DirectoryPath))
                    Directory.Delete(job.DirectoryPath, true);

                Directory.CreateDirectory(job.DirectoryPath);
                Directory.CreateDirectory(job.RelayResultsDirectoryPath);

                // Prepare job inputs
                job.Stage();

                // Update job status to running
                JobUpdateCallback(job, (j) =>
                {
                    j.Status = JobStatus.Running;
                    j.AddEvent(EventType.RunningStarted);
                    JobStatuses[j] = ClusterJobStatus.Running;
                });

                // Execute the job
                ((ILocalJob)job).RunLocal(cts.Token);

                // Mark job as finished
                JobUpdateCallback(job, (j) =>
                {
                    JobStatuses.Remove(j);
                    FinishedJobs.Add(j);
                });
            }
            catch (Exception exc)
            {
                // Log the exception and mark job as failed
                if (!File.Exists(job.ErrorFilePath))
                    File.Create(job.ErrorFilePath).Close();
                
                File.AppendAllText(job.ErrorFilePath, exc.ToString());
                
                JobUpdateCallback(job, (j) =>
                {
                    j.Status = JobStatus.Failed;
                    JobStatuses.Remove(j);
                    FailedJobs.Add(j);
                });
            }
            finally
            {
                _localJobSemaphore.Release();
                // Clean up tracking
                RunningJobs.Remove(job);
            }
        }, cts.Token);

        RunningJobs.Add(job, cts);
    }

    /// <summary>
    /// Checks the current status of a job.
    /// </summary>
    /// <param name="job">The job to check status for</param>
    /// <returns>The current status of the job</returns>
    /// <remarks>
    /// Status is determined from the internal tracking collections:
    /// - JobStatuses: For jobs in process (pending/running)
    /// - FinishedJobs: For successfully completed jobs
    /// - FailedJobs: For jobs that encountered errors
    /// 
    /// This method is called extensively by QueueRepository during job monitoring in
    /// handler methods like HandleStagingState, HandleRunningState, and HandleAbortingState.
    /// These handlers use the returned ClusterJobStatus to determine how to update the
    /// job's status in the data model and trigger appropriate state transitions.
    /// </remarks>
    public override async Task<(ClusterJobStatus status, string output)> CheckStatus(Job job)
    {
        if (JobStatuses.ContainsKey(job))
            return (JobStatuses[job], "");
        else if (FinishedJobs.Contains(job))
            return (ClusterJobStatus.Finished, "");
        else if (FailedJobs.Contains(job))
            return (ClusterJobStatus.Failed, "");
        else
            return (ClusterJobStatus.Unknown, "");
    }
    
    /// <summary>
    /// Aborts a currently running job.
    /// </summary>
    /// <param name="job">The job to abort</param>
    /// <remarks>
    /// Cancels the job's cancellation token to signal the running task to stop,
    /// and removes the job from the running jobs collection.
    /// 
    /// This method follows the same pattern as ClusterQueue.AbortJob, where both 
    /// derived classes call base.AbortJob() before their specific implementation.
    /// This maintains consistent job lifecycle management patterns across both
    /// queue types, enabling the QueueRepository to handle cancellation requests
    /// uniformly regardless of execution environment.
    /// </remarks>
    public override void AbortJob(Job job)
    {
        base.AbortJob(job);
        
        if (RunningJobs.ContainsKey(job))
        {
            RunningJobs[job].Cancel();
            RunningJobs.Remove(job);
        }
    }

    /// <summary>
    /// Only persist Waiting jobs for the local queue.
    /// Unsettled local jobs (Running, Staging, Finalizing, etc.) execute in Relay's own process
    /// and are interrupted when the app restarts, so there's no point restoring them into the queue.
    /// They'll be handled by the startup orphan detection in DataManager instead.
    /// </summary>
    public override void WriteToJson(JsonNode writer)
    {
        // Write base properties (Id, Alias, QueueType) but override the Jobs array
        // to only include Waiting jobs
        base.WriteToJson(writer);

        writer["Jobs"] = new JsonArray(QueuedJobs.Where(j => j.Status == JobStatus.Waiting)
                                                 .Select(j => JsonValue.Create($"{j.Space.Project.Id}.{j.Space.Id}.{j.Id}"))
                                                 .ToArray<JsonNode>());
    }

    /// <summary>
    /// Only restore Waiting jobs for the local queue.
    /// </summary>
    public override void ReadFromJson(JsonNode reader, Func<int, int, int, Job> findJob)
    {
        base.ReadFromJson(reader);

        string[] jobs = reader["Jobs"]?.Deserialize<string[]>() ?? Array.Empty<string>();

        foreach (string address in jobs)
        {
            string[] parts = address.Split('.');
            int projectId = int.Parse(parts[0]);
            int spaceId = int.Parse(parts[1]);
            int jobId = int.Parse(parts[2]);

            Job job = findJob(projectId, spaceId, jobId);

            if (job != null && job.Status == JobStatus.Waiting)
                Enqueue(job);
        }
    }

    /// <summary>
    /// Gets a read-only wrapper for this LocalQueue.
    /// </summary>
    /// <returns>A ReadOnlyLocalQueue instance that provides immutable access to this queue</returns>
    /// <remarks>
    /// Uses a ConditionalWeakTable to cache the wrapper and avoid creating
    /// multiple wrapper instances for the same LocalQueue.
    /// 
    /// This method is called by the QueueRepository to expose queues as read-only
    /// objects to external code, particularly when providing them as properties like
    /// ClusterQueues. It's also used by DataManager.Queue when exposing the newly created
    /// cluster queues to clients. This pattern ensures that queue configuration and state
    /// can only be modified through controlled repository methods, not directly by clients.
    /// </remarks>
    public override ReadOnlyJobQueue AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, queue => new ReadOnlyLocalQueue(queue));
    }
}