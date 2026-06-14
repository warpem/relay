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
public partial class QueueRepository
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