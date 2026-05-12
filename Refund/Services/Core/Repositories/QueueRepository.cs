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

/// <summary>
/// Repository for managing job queues, including the local queue and cluster queues.
/// Handles queue state persistence, job submission, status monitoring, and auto-saving functionality.
/// Includes a daemon process that periodically checks job status and updates jobs accordingly.
/// </summary>
public class QueueRepository
{
    /// <summary>
    /// Path to the file where queue state is persisted.
    /// </summary>
    private readonly string _statePath;
    
    /// <summary>
    /// Lock object for thread-safe operations.
    /// </summary>
    private readonly object _saveLock = new();
    
    /// <summary>
    /// Flag indicating whether changes need to be saved.
    /// </summary>
    private bool _needsSaving = false;
    
    /// <summary>
    /// JSON serialization options used for reading/writing data.
    /// </summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Logger instance for structured logging.
    /// </summary>
    private readonly ILogger _logger = Log.ForContext<QueueRepository>();

    /// <summary>
    /// The single local queue for running jobs on the same machine.
    /// </summary>
    private readonly LocalQueue _localQueue;
    
    /// <summary>
    /// List of cluster queues for submitting jobs to remote compute resources.
    /// </summary>
    private readonly List<JobQueue> _clusterQueues = new();

    // Auto-save fields
    private int _autoSaveInterval;
    private Timer _autoSaveTimer;
    private bool _disposed;

    // Daemon fields
    private int _daemonInterval;
    private Timer _daemonTimer;

    /// <summary>
    /// Maps jobs to their log tracking tasks to avoid duplicate tracking.
    /// </summary>
    private readonly Dictionary<Job, Task> _trackProgressLogsTasks = new();
    
    /// <summary>
    /// Maps jobs to their results tracking tasks to avoid duplicate tracking.
    /// </summary>
    private readonly Dictionary<Job, Task> _trackProgressResultsTasks = new();
    
    /// <summary>
    /// Maps jobs to their finalization tasks to avoid duplicate finalization.
    /// </summary>
    private readonly ConcurrentDictionary<Job, Task> _finalizationTasks = new();
    
    /// <summary>
    /// Semaphore to limit concurrent progress tracking operations
    /// </summary>
    private readonly SemaphoreSlim _progressTrackingSemaphore = new(Environment.ProcessorCount * 2);
    
    /// <summary>
    /// Semaphore to limit concurrent cluster operations
    /// </summary>
    private readonly SemaphoreSlim _clusterOperationsSemaphore = new(Environment.ProcessorCount);
    
    /// <summary>
    /// Timeout for progress tracking operations (30 seconds)
    /// </summary>
    private readonly TimeSpan _progressTrackingTimeout = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Timeout for cluster operations (60 seconds)
    /// </summary>
    private readonly TimeSpan _clusterOperationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tracks currently running progress operations to prevent duplicates
    /// </summary>
    private readonly ConcurrentDictionary<Job, DateTime> _activeProgressOperations = new();
    
    /// <summary>
    /// Callback for updating job state in a thread-safe manner.
    /// </summary>
    private readonly Action<Job, Action<Job>> _jobUpdateCallback;

    /// <summary>
    /// Async callback for updating job state without blocking a thread pool thread.
    /// Used by progress tracking to avoid thread pool starvation from .Wait() calls.
    /// </summary>
    private readonly Func<Job, Action<Job>, Task> _jobUpdateCallbackAsync;

    /// <summary>
    /// Gets the local job queue.
    /// </summary>
    public JobQueue LocalQueue => _localQueue;

    /// <summary>
    /// Gets a read-only collection of cluster queues.
    /// </summary>
    public ReadOnlyCollection<JobQueue> ClusterQueues => _clusterQueues.ToList().AsReadOnly();

    /// <summary>
    /// Initializes a new instance of the QueueRepository class.
    /// </summary>
    /// <param name="statePath">Path to the file where queue state will be persisted</param>
    /// <param name="jobUpdateCallback">Callback function for updating job state in a thread-safe manner</param>
    public QueueRepository(string statePath, Action<Job, Action<Job>> jobUpdateCallback, Func<Job, Action<Job>, Task> jobUpdateCallbackAsync)
    {
        _statePath = statePath;
        _jobUpdateCallback = jobUpdateCallback;
        _jobUpdateCallbackAsync = jobUpdateCallbackAsync;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        _jsonOptions.MakeReadOnly();

        // Initialize the local queue
        _localQueue = new LocalQueue(_jobUpdateCallback)
        {
            Id = -1,
            QueueType = JobQueueType.Local
        };

        InitializeClusterQueues();
    }

    private void InitializeClusterQueues()
    {
    }

    /// <summary>
    /// Loads queue state from persistent storage, restoring queues and their jobs.
    /// Uses the dataRepository to find jobs by ID for reconnecting them to queues.
    /// </summary>
    /// <param name="dataRepository">The data repository used to look up job references</param>
    public void LoadQueues(DataRepository dataRepository)
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                _logger.Information("No previous queue state found in {StatePath}", Path.GetFullPath(_statePath));
                return;
            }

            var stateJson = File.ReadAllText(_statePath);
            var stateNode = JsonNode.Parse(stateJson);

            if (stateNode == null)
                throw new Exception($"Couldn't parse JSON from {Path.GetFullPath(_statePath)}");

            // Load local queue's persisted job list (restores Waiting and unsettled jobs)
            if (stateNode["Local"] != null)
                _localQueue.ReadFromJson(stateNode["Local"], (pId, sId, jId) => dataRepository.FindJob(pId, sId, jId));

            // Load cluster queues from the JSON file
            if (stateNode["Cluster"]?.AsArray() != null)
                foreach (var queueNode in stateNode["Cluster"].AsArray())
                {
                    var queue = new ClusterQueue(_jobUpdateCallback);
                    // The job finder delegate allows the queue to resolve job references by ID
                    queue.ReadFromJson(queueNode, (pId, sId, jId) => dataRepository.FindJob(pId, sId, jId));

                    _clusterQueues.Add(queue);
                }

            _logger.Information("Successfully loaded {LocalJobCount} local jobs and {ClusterQueueCount} cluster queues from {StatePath}",
                _localQueue.QueuedJobs.Count, _clusterQueues.Count, Path.GetFullPath(_statePath));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading queue state from {StatePath}", _statePath);
        }
    }

    #region Auto-save methods

    /// <summary>
    /// Starts the timer for periodically saving queue state to disk.
    /// </summary>
    /// <param name="milliseconds">The interval, in milliseconds, at which to save changes</param>
    public void StartAutoSave(int milliseconds)
    {
        _autoSaveInterval = milliseconds;
        _autoSaveTimer = new Timer(SaveChanges, null, _autoSaveInterval, Timeout.Infinite);
    }

    /// <summary>
    /// Stops the auto-save timer.
    /// </summary>
    public void StopAutoSave()
    {
        _autoSaveTimer?.Dispose();
    }

    /// <summary>
    /// Timer callback that saves queue state to disk if changes have been made.
    /// Reschedules itself after completion if the repository is not disposed.
    /// </summary>
    /// <param name="state">State object passed by the Timer (not used)</param>
    private void SaveChanges(object state)
    {
        try
        {
            if (_needsSaving)
                SaveQueues();
            _needsSaving = false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error saving queue state to {StatePath}", _statePath);
        }
        finally
        {
            if (!_disposed)
                _autoSaveTimer?.Change(_autoSaveInterval, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Persists all queues to the state file.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    private void SaveQueues()
    {
        lock (_saveLock)
        {
            var directoryPath = Path.GetDirectoryName(_statePath);

            if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);

            var queuesJson = new JsonObject();

            // Save local queue
            var localJson = new JsonObject();
            _localQueue.WriteToJson(localJson);
            queuesJson["Local"] = localJson;

            queuesJson["Cluster"] = new JsonArray(_clusterQueues.Where(q => q is ClusterQueue)
                                                                .Select(q =>
                                                                {
                                                                    var queueWriter = new JsonObject();
                                                                    q.WriteToJson(queueWriter);

                                                                    return queueWriter;
                                                                })
                                                                .ToArray<JsonNode>());

            File.WriteAllText(_statePath, queuesJson.ToJsonString(_jsonOptions));
            
            _logger.Information("Successfully saved queue state with {ClusterQueueCount} cluster queues to {StatePath}", 
                _clusterQueues.Count(q => q is ClusterQueue), Path.GetFullPath(_statePath));
        }
    }

    #endregion

    #region Queue operations

    /// <summary>
    /// Creates a new cluster queue based on an optional template.
    /// </summary>
    public JobQueue CreateClusterQueue(ClusterQueue template = null)
    {
        lock (_saveLock)
        {
            var queue = new ClusterQueue(_jobUpdateCallback);

            if (template != null)
                queue.AdoptState(template);

            queue.Id = _clusterQueues.Select(q => q.Id).DefaultIfEmpty(0).Max() + 1;
            _clusterQueues.Add(queue);
            
            _needsSaving = true;
            
            _logger.Information("Cluster queue {QueueId} successfully created (alias: {QueueAlias})", 
                queue.Id, queue.Alias);

            return queue;
        }
    }

    /// <summary>
    /// Updates an existing queue with the specified action.
    /// </summary>
    public void UpdateQueue(JobQueue queue, Action<JobQueue> updateAction)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            updateAction(queue);
            
            _needsSaving = true;
        }
        
        _logger.Information("Queue {QueueId} successfully updated (alias: {QueueAlias})", 
            queue.Id, queue.Alias);
    }

    /// <summary>
    /// Deletes a cluster queue.
    /// </summary>
    public void DeleteClusterQueue(ClusterQueue queue)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            _clusterQueues.Remove(queue);
            
            _needsSaving = true;
        }
        
        _logger.Information("Cluster queue {QueueId} successfully deleted (alias: {QueueAlias})", 
            queue.Id, queue.Alias);
    }
    
    /// <summary>
    /// Moves a queue to a specific position in the cluster queues list.
    /// </summary>
    /// <param name="queue">The queue to move</param>
    /// <param name="newPosition">The new position for the queue</param>
    internal void ReorderClusterQueue(JobQueue queue, int newPosition)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));
        
        lock (_saveLock)
        {
            if (!_clusterQueues.Contains(queue))
                throw new Exception($"Queue {queue.Id} not found");

            if (newPosition < 0 || newPosition >= _clusterQueues.Count)
                throw new ArgumentOutOfRangeException(nameof(newPosition), "New position is out of range");

            int currentPosition = _clusterQueues.IndexOf(queue);

            // Remove from current position
            _clusterQueues.RemoveAt(currentPosition);

            // Adjust the target position if it was after the removal point
            // When we remove an item at position X, all subsequent positions shift down by 1
            // So if we want to insert at position Y where Y > X, we need to decrease Y by 1
            int adjustedPosition = newPosition > currentPosition ? newPosition : newPosition;
        
            // Insert at the adjusted position
            _clusterQueues.Insert(adjustedPosition, queue);
            
            _needsSaving = true;
            
            _logger.Information("Cluster queue {QueueId} successfully reordered from position {OldPosition} to {NewPosition} (alias: {QueueAlias})", 
                queue.Id, currentPosition, adjustedPosition, queue.Alias);
        }
    }

    /// <summary>
    /// Queues a job in the local queue.
    /// </summary>
    public void QueueLocalJob(Job job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            _localQueue.Enqueue(job);
            _needsSaving = true;
        }
        
        _logger.Information("Job {JobId} successfully queued in local queue (alias: {JobAlias})", 
            job.Id, job.Alias);
    }

    /// <summary>
    /// Queues a job in a cluster queue.
    /// </summary>
    public void QueueClusterJob(Job job, JobQueue queue)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            queue.Enqueue(job);
            _needsSaving = true;
        }
        
        _logger.Information("Job {JobId} successfully queued in cluster queue {QueueId} (job alias: {JobAlias}, queue alias: {QueueAlias})", 
            job.Id, queue.Id, job.Alias, queue.Alias);
    }

    /// <summary>
    /// Removes a job from the local queue.
    /// </summary>
    public void DequeueLocalJob(Job job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            _localQueue.Dequeue(job);
            _needsSaving = true;
        }
        
        _logger.Information("Job {JobId} successfully dequeued from local queue (alias: {JobAlias})", 
            job.Id, job.Alias);
    }

    /// <summary>
    /// Removes a job from a cluster queue.
    /// </summary>
    public void DequeueClusterJob(Job job, JobQueue queue)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            queue.Dequeue(job);
            _needsSaving = true;
        }
        
        _logger.Information("Job {JobId} successfully dequeued from cluster queue {QueueId} (job alias: {JobAlias}, queue alias: {QueueAlias})", 
            job.Id, queue.Id, job.Alias, queue.Alias);
    }

    /// <summary>
    /// Finds a queue by its ID.
    /// </summary>
    public JobQueue FindQueue(int id)
    {
        return id == -1 ? _localQueue : _clusterQueues.FirstOrDefault(q => q.Id == id);
    }

    #endregion

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
                    // For any other state (Finished, Failed, etc.), remove the job from the queue
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

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes of the resources used by the QueueRepository.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// Performs a final save of any pending changes and disposes of the auto-save and daemon timers.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            SaveChanges(null); // Final save of any pending changes
            _autoSaveTimer?.Dispose();
            _daemonTimer?.Dispose();
            _progressTrackingSemaphore?.Dispose();
            _clusterOperationsSemaphore?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}