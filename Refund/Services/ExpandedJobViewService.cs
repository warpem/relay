using Microsoft.Extensions.Logging;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Refund.Services;

/// <summary>
/// Manages the expanded job view state, including iteration selection, log visibility, and cached job data.
/// </summary>
/// <remarks>
/// This service handles the state for the expanded job view panel in the UI, which displays detailed 
/// information about a selected job. It manages:
/// 
/// - The currently selected job and its iterations
/// - Loading and caching of job logs and output for different iterations
/// - The visibility state of the log panel
/// - Events for notifying UI components of state changes
/// 
/// The service automatically subscribes to job update events and refreshes data when needed.
/// </remarks>
public class ExpandedJobViewService : IDisposable
{
    private readonly DataManager _dataManager;
    private readonly RelaySession _session;
    private readonly ILogger<ExpandedJobViewService> _logger;
    private readonly List<GroupEventSubscription> _subscriptions = new();
    private readonly Func<string, Task<string>> _readTextAsync;
    private ReadOnlyJob _selectedJob;
    private long _version;
    private bool _disposed;
    private SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Gets the currently selected job from the session.
    /// </summary>
    private ReadOnlyJob _job => _session.Job;
    
    /// <summary>
    /// Gets the currently selected job for display in the expanded view.
    /// </summary>
    public ReadOnlyJob CurrentJob => _job;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpandedJobViewService"/> class.
    /// </summary>
    /// <param name="dataManager">The data manager service for subscribing to job update events</param>
    /// <param name="session">The session service that tracks the current application state</param>
    /// <param name="logger">The logger for this service</param>
    public ExpandedJobViewService(DataManager dataManager, RelaySession session, ILogger<ExpandedJobViewService> logger)
        : this(dataManager, session, logger, path => File.ReadAllTextAsync(path)) { }

    internal ExpandedJobViewService(DataManager dataManager, RelaySession session,
        ILogger<ExpandedJobViewService> logger, Func<string, Task<string>> readTextAsync)
    {
        _dataManager = dataManager;
        _session = session;
        _logger = logger;
        _readTextAsync = readTextAsync;
        _session.OnJobChanged += HandleSessionJobChanged;
        _ = HandleSessionJobChanged();
    }

    /// <summary>
    /// Handles changes to the currently selected job in the session.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method performs a complete state reset when the job changes:
    /// - Unsubscribes from previous job events
    /// - Clears cached data for the previous job
    /// - Sets up subscriptions for the new job
    /// - Loads initial data for the new job
    /// - Selects the latest iteration automatically
    /// - Notifies subscribers of the job change
    /// </remarks>
    private async Task HandleSessionJobChanged()
    {
        ReadOnlyJob job;
        long version;
        lock (_stateLock)
        {
            if (_disposed) return;
            ClearSubscriptions();
            job = _selectedJob = _job;
            version = ++_version;
            // A new job must not wait for an old job's outstanding disk reads.
            _refreshGate = new SemaphoreSlim(1, 1);
            _currentIteration = -1;
            _availableIterations.Clear();
            _iterationMetadata.Clear();
            _cachedLogs.Clear();
            _currentErrors = _currentStaging = string.Empty;
            _hasNewLogs = _hasNewErrors = _hasNewStaging = false;

            if (job != null)
                _subscriptions.Add(_dataManager.JobUpdated.Add(GroupName.SpecificJob(job),
                    args => HandleJobUpdated(args.Object, version)));
        }

        if (job != null)
        {
            RefreshIterations(job, version);
            if (!await RefreshLogsAsync(job, version)) return;
        }
        await NotifyCurrent(job, version, OnJobChanged, job);
        int iteration;
        lock (_stateLock)
        {
            if (!IsCurrent(job, version)) return;
            iteration = _availableIterations.Count > 0 ? _availableIterations.Max() : -1;
        }
        await SetIterationAsync(iteration, job, version);
    }

    private int _currentIteration = -1;
    
    /// <summary>
    /// Gets the currently selected iteration for the expanded job.
    /// </summary>
    /// <remarks>
    /// A value of -1 indicates that no iteration is selected.
    /// </remarks>
    public int CurrentIteration { get { lock (_stateLock) return _currentIteration; } }
    
    /// <summary>
    /// Gets the current visualization iteration, which is the minimum of the selected iteration
    /// and the highest iteration that has visualization data available.
    /// </summary>
    /// <remarks>
    /// This ensures that visualizations aren't shown for iterations where the data isn't available yet.
    /// </remarks>
    public int CurrentVisIteration => Math.Min(CurrentIteration, _job?.VisAvailableIteration ?? -1);

    // Available iterations are those that have either logs or results
    private readonly List<int> _availableIterations = new();
    private readonly object _stateLock = new object();
    
    /// <summary>
    /// Gets a read-only list of iterations that have logs, results, or visualizations available.
    /// </summary>
    /// <remarks>
    /// This property returns a copy of the internal list to ensure thread safety.
    /// The list is sorted in ascending order (oldest to newest iteration).
    /// </remarks>
    public IReadOnlyList<int> AvailableIterations
    {
        get
        {
            lock (_stateLock)
                return _availableIterations.ToList();
        }
    }
    
    private readonly Dictionary<int, (bool hasLogs, bool hasResults, bool hasVis)> _iterationMetadata = new();

    /// <summary>
    /// Gets a dictionary mapping iteration numbers to metadata about what is available for each iteration.
    /// </summary>
    /// <remarks>
    /// The metadata for each iteration includes:
    /// - hasLogs: Whether log files exist for this iteration
    /// - hasResults: Whether result files exist for this iteration
    /// - hasVis: Whether visualization data is available for this iteration
    /// 
    /// This property returns a copy of the internal dictionary to ensure thread safety.
    /// </remarks>
    public IReadOnlyDictionary<int, (bool hasLogs, bool hasResults, bool hasVis)> IterationMetadata
    {
        get
        {
            lock (_stateLock)
                return _iterationMetadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
    
    private bool _isLogPanelExpanded = false;
    
    /// <summary>
    /// Gets a value indicating whether the log panel is currently expanded.
    /// </summary>
    public bool IsLogPanelExpanded { get { lock (_stateLock) return _isLogPanelExpanded; } }

    private LogSection _currentSection = LogSection.Staging;
    
    /// <summary>
    /// Gets the currently selected log section (Staging, Output, or Errors).
    /// </summary>
    public LogSection CurrentSection { get { lock (_stateLock) return _currentSection; } }

    private bool _hasNewLogs = false;
    
    /// <summary>
    /// Gets a value indicating whether there are new logs that haven't been viewed yet.
    /// </summary>
    public bool HasNewLogs { get { lock (_stateLock) return _hasNewLogs; } }

    private bool _hasNewErrors = false;
    
    /// <summary>
    /// Gets a value indicating whether there are new error logs that haven't been viewed yet.
    /// </summary>
    public bool HasNewErrors { get { lock (_stateLock) return _hasNewErrors; } }

    private bool _hasNewStaging = false;
    
    /// <summary>
    /// Gets a value indicating whether there are new staging logs that haven't been viewed yet.
    /// </summary>
    public bool HasNewStaging { get { lock (_stateLock) return _hasNewStaging; } }

    // Events
    /// <summary>
    /// Event raised when the selected job changes.
    /// </summary>
    public event Func<ReadOnlyJob, Task> OnJobChanged;
    
    /// <summary>
    /// Event raised when the selected job is updated (e.g., status changes, new logs available).
    /// </summary>
    public event Func<Task> OnJobUpdated;
    
    /// <summary>
    /// Event raised when the selected iteration changes.
    /// </summary>
    public event Func<int, Task> OnIterationChanged;
    
    /// <summary>
    /// Event raised when the logs for the current iteration are updated.
    /// </summary>
    public event Func<Task> OnLogsUpdated;
    
    /// <summary>
    /// Event raised when the error logs for the job are updated.
    /// </summary>
    public event Func<string, Task> OnErrorsUpdated;
    
    /// <summary>
    /// Event raised when the staging logs for the job are updated.
    /// </summary>
    public event Func<string, Task> OnStagingUpdated;
    
    /// <summary>
    /// Event raised when the log panel's expanded/collapsed state changes.
    /// </summary>
    public event Func<Task> OnLogPanelStateChanged;
    
    /// <summary>
    /// Cache of log content by iteration.
    /// </summary>
    /// <remarks>
    /// This cache stores the log content for each iteration to avoid reading from disk unnecessarily.
    /// </remarks>
    private readonly Dictionary<int, string> _cachedLogs = new();
    
    private string _currentErrors = string.Empty;
    
    /// <summary>
    /// Gets the current error log content for the selected job.
    /// </summary>
    public string CurrentErrors { get { lock (_stateLock) return _currentErrors; } }

    private string _currentStaging = string.Empty;
    
    /// <summary>
    /// Gets the current staging log content for the selected job.
    /// </summary>
    public string CurrentStaging { get { lock (_stateLock) return _currentStaging; } }

    /// <summary>
    /// Sets the currently selected iteration.
    /// </summary>
    /// <param name="iteration">The iteration number to select</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method validates that the selected iteration is available before changing the selection.
    /// When the iteration changes, it refreshes the logs for the new iteration and notifies subscribers.
    /// </remarks>
    public Task SetIterationAsync(int iteration)
    {
        ReadOnlyJob job;
        long version;
        lock (_stateLock) { job = _selectedJob; version = _version; }
        return SetIterationAsync(iteration, job, version);
    }

    private async Task SetIterationAsync(int iteration, ReadOnlyJob job, long version)
    {
        lock (_stateLock)
        {
            if (job == null || !IsCurrent(job, version) || iteration == _currentIteration ||
                iteration < -1 || (iteration != -1 && !_availableIterations.Contains(iteration))) return;
            _currentIteration = iteration;
        }
        if (!await RefreshLogsAsync(job, version)) return;
        lock (_stateLock)
            if (_currentIteration != iteration) return;
        await NotifyCurrent(job, version, OnIterationChanged, iteration);
    }

    /// <summary>
    /// Opens the log panel and selects the specified section.
    /// </summary>
    /// <param name="section">The log section to display (Output or Errors)</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method expands the log panel if it's not already expanded, switches to the specified
    /// section, and clears the "new logs" or "new errors" indicator for the selected section.
    /// </remarks>
    public async Task OpenLogPanel(LogSection section)
    {
        bool changed;
        lock (_stateLock)
        {
            _currentSection = section;
            changed = !_isLogPanelExpanded;
            _isLogPanelExpanded = true;
            if (section == LogSection.Staging) _hasNewStaging = false;
            else if (section == LogSection.Output) _hasNewLogs = false;
            else if (section == LogSection.Errors) _hasNewErrors = false;
        }
        if (changed) await OnLogPanelStateChanged.InvokeAllAsync();
    }

    /// <summary>
    /// Closes the log panel.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method collapses the log panel if it's currently expanded and notifies subscribers.
    /// </remarks>
    public async Task CloseLogPanel()
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _isLogPanelExpanded;
            _isLogPanelExpanded = false;
        }
        if (changed) await OnLogPanelStateChanged.InvokeAllAsync();
    }

    /// <summary>
    /// Handles updates to the currently selected job.
    /// </summary>
    /// <param name="job">The updated job</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is called when the job is updated (e.g., new logs are available,
    /// job status changes). It performs the following actions:
    /// 
    /// 1. Refreshes the available iterations and logs
    /// 2. If the user was viewing the most recent iteration, automatically advances to
    ///    the new most recent iteration (to "follow" the progress)
    /// 3. If the current iteration is no longer available, selects a new iteration
    /// 4. Sets flags to indicate new logs are available (if the log panel isn't showing them)
    /// 5. Notifies subscribers of the job update
    /// </remarks>
    private async Task HandleJobUpdated(ReadOnlyJob job, long version)
    {
        try
        {
            bool showingLastIteration;
            string previousErrors, previousStaging, previousLog;
            int previousIterationCount;
            lock (_stateLock)
            {
                if (!IsCurrent(job, version)) return;
                showingLastIteration = _availableIterations.Count == 0 || _currentIteration == _availableIterations.Max();
                previousErrors = _currentErrors;
                previousStaging = _currentStaging;
                previousIterationCount = _availableIterations.Count;
                previousLog = GetLogsForIteration(_currentIteration);
            }
            RefreshIterations(job, version);
            int iteration;
            bool iterationChanged;
            lock (_stateLock)
            {
                if (!IsCurrent(job, version)) return;
                iteration = showingLastIteration || !_availableIterations.Contains(_currentIteration)
                    ? (_availableIterations.Count > 0 ? _availableIterations.Max() : -1)
                    : _currentIteration;
                iterationChanged = iteration != _currentIteration;
                _currentIteration = iteration;
            }
            if (!await RefreshLogsAsync(job, version)) return;
            lock (_stateLock)
            {
                if (!IsCurrent(job, version)) return;
                if (_currentStaging != previousStaging && (!_isLogPanelExpanded || _currentSection != LogSection.Staging))
                    _hasNewStaging = true;
                if ((_availableIterations.Count > previousIterationCount || GetLogsForIteration(_currentIteration) != previousLog) &&
                    (!_isLogPanelExpanded || _currentSection != LogSection.Output))
                    _hasNewLogs = true;
                if (_currentErrors != previousErrors && (!_isLogPanelExpanded || _currentSection != LogSection.Errors))
                    _hasNewErrors = true;
                iterationChanged &= _currentIteration == iteration;
            }
            if (iterationChanged)
                await NotifyCurrent(job, version, OnIterationChanged, iteration);
            await NotifyCurrent(job, version, OnJobUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling job update for job {JobId}", job?.Id);
        }
    }

    /// <summary>
    /// Refreshes the list of available iterations and their metadata.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method scans the job's directory structure to determine:
    /// 1. Which iterations have logs
    /// 2. Which iterations have result files
    /// 3. Which iterations have visualization data
    /// 
    /// It builds the list of available iterations (those with at least one of these types of data)
    /// and their corresponding metadata. The method uses thread-safe operations to update the
    /// shared collections, as they may be accessed from different threads.
    /// </remarks>
    private void RefreshIterations(ReadOnlyJob job, long version)
    {
        if (job == null || !IsCurrent(job, version))
            return;
            
        var newIterations = new List<int>();
        var newMetadata = new Dictionary<int, (bool hasLogs, bool hasResults, bool hasVis)>();

        // Build new collections without holding the lock
        for (int i = 0; i <= Math.Max(job.LogsAvailableIteration, job.VisAvailableIteration); i++)
        {
            bool hasLogs = i <= job.LogsAvailableIteration;
            bool hasResults = job.HasResultFilesForIteration(i);
            bool hasVis = i <= job.VisAvailableIteration;
            
            if (hasLogs || hasResults || hasVis)
            {
                newIterations.Add(i);
                newMetadata[i] = (hasLogs, hasResults, hasVis);
            }
        }
        
        newIterations.Sort();

        // Update collections under lock
        lock (_stateLock)
        {
            if (!IsCurrent(job, version)) return;
            _availableIterations.Clear();
            _availableIterations.AddRange(newIterations);
            
            _iterationMetadata.Clear();
            foreach (var kvp in newMetadata)
                _iterationMetadata[kvp.Key] = kvp.Value;
        }
    }
    
    /// <summary>
    /// Refreshes the log cache with the latest log files for the job.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method performs the following operations:
    /// 1. Loads the error log file if it exists
    /// 2. Updates the log cache for all available iterations
    /// 3. Always re-reads the logs for the current iteration and the last two iterations
    ///    (to ensure the most recent logs are shown)
    /// 4. Notifies subscribers when logs are updated
    /// 
    /// The method uses a caching strategy to avoid re-reading older iteration logs
    /// from disk unnecessarily, as these logs don't change once written.
    /// </remarks>
    private async Task<bool> RefreshLogsAsync(ReadOnlyJob job, long version)
    {
        SemaphoreSlim gate;
        lock (_stateLock)
        {
            if (job == null || !IsCurrent(job, version)) return false;
            gate = _refreshGate;
        }
        await gate.WaitAsync();
        string errors, staging;
        try
        {
            int[] iterations;
            lock (_stateLock)
            {
                if (!IsCurrent(job, version)) return false;
                int max = _availableIterations.Count > 0 ? _availableIterations.Max() : -1;
                iterations = _availableIterations.Where(i => i == _currentIteration || i >= max - 1 || !_cachedLogs.ContainsKey(i)).ToArray();
                errors = _currentErrors;
                staging = _currentStaging;
            }
            // Read using the captured job, then publish one complete snapshot only if
            // this selection is still current. No shared cache is written across awaits.
            var logs = new Dictionary<int, string>();
            try
            {
                errors = File.Exists(job.ErrorFilePath) ? await _readTextAsync(job.ErrorFilePath) : string.Empty;
                staging = File.Exists(job.LifecycleFilePath)
                    ? ProcessStagingContent(await _readTextAsync(job.LifecycleFilePath)) : string.Empty;
                foreach (int iteration in iterations)
                {
                    if (!IsCurrent(job, version)) return false;
                    string path = job.LogFilePath(iteration);
                    if (File.Exists(path)) logs[iteration] = await _readTextAsync(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading log files from disk for job {JobId}", job.Id);
            }
            lock (_stateLock)
            {
                if (!IsCurrent(job, version)) return false;
                _currentErrors = errors;
                _currentStaging = staging;
                foreach (var entry in logs) _cachedLogs[entry.Key] = entry.Value;
            }
        }
        finally { gate.Release(); }

        // Observers may navigate or request another iteration; never call them while
        // holding the refresh gate or the state lock.
        await NotifyLogObservers(job, version, OnErrorsUpdated, errors);
        await NotifyLogObservers(job, version, OnStagingUpdated, staging);
        await NotifyLogObservers(job, version, OnLogsUpdated);
        return IsCurrent(job, version);
    }

    private bool IsCurrent(ReadOnlyJob job, long version)
    {
        lock (_stateLock)
            return !_disposed && _version == version && _selectedJob == job && _job == job;
    }

    private async Task NotifyCurrent<T>(ReadOnlyJob job, long version, Func<T, Task> handlers, T value)
    {
        if (handlers == null) return;
        foreach (Func<T, Task> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(job, version)) return;
            await handler(value);
        }
    }

    private async Task NotifyCurrent(ReadOnlyJob job, long version, Func<Task> handlers)
    {
        if (handlers == null) return;
        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(job, version)) return;
            await handler();
        }
    }

    private async Task NotifyLogObservers<T>(ReadOnlyJob job, long version, Func<T, Task> handlers, T value)
    {
        try { await NotifyCurrent(job, version, handlers, value); }
        catch (Exception ex) { _logger.LogError(ex, "Error notifying log subscribers for job {JobId}", job.Id); }
    }

    private async Task NotifyLogObservers(ReadOnlyJob job, long version, Func<Task> handlers)
    {
        try { await NotifyCurrent(job, version, handlers); }
        catch (Exception ex) { _logger.LogError(ex, "Error notifying log subscribers for job {JobId}", job.Id); }
    }

    /// <summary>
    /// Processes staging content to handle carriage return (\r) symbols.
    /// For each line that contains \r, only the content after the last \r is displayed.
    /// This is commonly used for progress indicators that overwrite the current line.
    /// </summary>
    /// <param name="rawContent">The raw staging content from the file</param>
    /// <returns>The processed content with \r handling applied</returns>
    private string ProcessStagingContent(string rawContent)
    {
        if (string.IsNullOrEmpty(rawContent))
            return string.Empty;

        var lines = rawContent.Split(['\n'], StringSplitOptions.None);
        var processedLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.Contains('\r'))
            {
                // Find the last \r in the line and take everything after it
                int lastCarriageReturn = line.LastIndexOf('\r');
                var processedLine = line.Substring(lastCarriageReturn + 1);
                processedLines.Add(processedLine);
            }
            else
            {
                processedLines.Add(line);
            }
        }

        return string.Join("\n", processedLines);
    }

    /// <summary>
    /// Gets the logs for a specific iteration.
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The log content for the specified iteration, or an empty string if not found</returns>
    /// <remarks>
    /// This method retrieves logs from the cache if available, avoiding disk reads when possible.
    /// </remarks>
    public string GetLogsForIteration(int iteration)
    {
        lock (_stateLock)
            return _cachedLogs.TryGetValue(iteration, out var logs) ? logs : string.Empty;
    }

    /// <summary>
    /// Toggles the expanded/collapsed state of the log panel.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task ToggleLogPanelExpanded()
    {
        lock (_stateLock)
        {
            _isLogPanelExpanded = !_isLogPanelExpanded;
            _hasNewLogs = _hasNewStaging = false;
        }
        await OnLogPanelStateChanged.InvokeAllAsync();
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    /// <remarks>
    /// This method cleans up event subscriptions to prevent memory leaks when the service is no longer needed.
    /// </remarks>
    public void Dispose()
    {
        _session.OnJobChanged -= HandleSessionJobChanged;
        lock (_stateLock)
        {
            _disposed = true;
            ++_version;
            ClearSubscriptions();
        }
    }

    private void ClearSubscriptions()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        foreach (var subscription in subscriptions) subscription.Unsubscribe();
    }
}

/// <summary>
/// Defines the different sections of the log panel.
/// </summary>
public enum LogSection
{
    /// <summary>
    /// The staging logs section.
    /// </summary>
    Staging,
    
    /// <summary>
    /// The standard output logs section.
    /// </summary>
    Output,
    
    /// <summary>
    /// The error logs section.
    /// </summary>
    Errors
}