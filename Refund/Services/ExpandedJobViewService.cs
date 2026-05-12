using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
    {
        _dataManager = dataManager;
        _session = session;
        _logger = logger;

        _session.OnJobChanged += HandleSessionJobChanged;
        HandleSessionJobChanged();
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
        _subscriptions.UnsubscribeAndClear();
        
        _currentIteration = -1;
        _availableIterations.Clear();
        _iterationMetadata.Clear();
        _cachedLogs.Clear();
        _currentErrors = string.Empty;
        _currentStaging = string.Empty;

        if (_job != null)
        {
            // Subscribe to job updates
            _subscriptions.Add(_dataManager.JobUpdated.Add(GroupName.Job(_job.Space.Project.Id, _job.Space.Id, _job.Id),
                                                           async args => await HandleJobUpdated(args.Object)));

            // Initial load
            await RefreshIterationsAsync();
            await RefreshLogsAsync();
        
            await OnJobChanged.InvokeAllAsync(_job);
            
            // Set initial iteration to the highest available
            if (_availableIterations.Any())
                await SetIterationAsync(_availableIterations.Max());
        }
        else
        {
            await OnJobChanged.InvokeAllAsync(null);
        }
    }
    
    private int _currentIteration = -1;
    
    /// <summary>
    /// Gets the currently selected iteration for the expanded job.
    /// </summary>
    /// <remarks>
    /// A value of -1 indicates that no iteration is selected.
    /// </remarks>
    public int CurrentIteration => _currentIteration;
    
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
    private readonly object _iterationsLock = new object();
    
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
            lock (_iterationsLock)
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
            lock (_iterationsLock)
                return _iterationMetadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
    
    private bool _isLogPanelExpanded = false;
    
    /// <summary>
    /// Gets a value indicating whether the log panel is currently expanded.
    /// </summary>
    public bool IsLogPanelExpanded => _isLogPanelExpanded;

    private LogSection _currentSection = LogSection.Staging;
    
    /// <summary>
    /// Gets the currently selected log section (Staging, Output, or Errors).
    /// </summary>
    public LogSection CurrentSection => _currentSection;

    private bool _hasNewLogs = false;
    
    /// <summary>
    /// Gets a value indicating whether there are new logs that haven't been viewed yet.
    /// </summary>
    public bool HasNewLogs => _hasNewLogs;

    private bool _hasNewErrors = false;
    
    /// <summary>
    /// Gets a value indicating whether there are new error logs that haven't been viewed yet.
    /// </summary>
    public bool HasNewErrors => _hasNewErrors;

    private bool _hasNewStaging = false;
    
    /// <summary>
    /// Gets a value indicating whether there are new staging logs that haven't been viewed yet.
    /// </summary>
    public bool HasNewStaging => _hasNewStaging;

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
    private readonly ConcurrentDictionary<int, string> _cachedLogs = new();
    
    private string _currentErrors = string.Empty;
    
    /// <summary>
    /// Gets the current error log content for the selected job.
    /// </summary>
    public string CurrentErrors => _currentErrors;

    private string _currentStaging = string.Empty;
    
    /// <summary>
    /// Gets the current staging log content for the selected job.
    /// </summary>
    public string CurrentStaging => _currentStaging;

    /// <summary>
    /// Sets the currently selected iteration.
    /// </summary>
    /// <param name="iteration">The iteration number to select</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method validates that the selected iteration is available before changing the selection.
    /// When the iteration changes, it refreshes the logs for the new iteration and notifies subscribers.
    /// </remarks>
    public async Task SetIterationAsync(int iteration)
    {
        if (_job == null ||
            iteration == _currentIteration ||
            iteration < -1 || 
            !_availableIterations.Contains(iteration))
            return;
            
        _currentIteration = iteration;
        await RefreshLogsAsync();
        
        _logger.LogDebug("Iteration changed to {Iteration} for job {JobId}", iteration, _job?.Id);
        await OnIterationChanged.InvokeAllAsync(iteration);
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
        _currentSection = section;
        if (!_isLogPanelExpanded)
        {
            _isLogPanelExpanded = true;
            await OnLogPanelStateChanged.InvokeAllAsync();
        }
    
        // Clear new indicators for this section
        if (section == LogSection.Staging)
            _hasNewStaging = false;
        else if (section == LogSection.Output)
            _hasNewLogs = false;
        else if (section == LogSection.Errors)
            _hasNewErrors = false;
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
        if (_isLogPanelExpanded)
        {
            _isLogPanelExpanded = false;
            await OnLogPanelStateChanged.InvokeAllAsync();
        }
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
    private async Task HandleJobUpdated(ReadOnlyJob job)
    {
        try
        {
            bool showingLastIteration = _availableIterations.Count == 0 ||
                                        CurrentIteration == _availableIterations.Max();

            // Store previous log content to detect changes
            string previousErrors = _currentErrors;
            string previousStaging = _currentStaging;
            var previousIterationCount = _availableIterations.Count;
            string previousCurrentIterationLog = CurrentIteration >= 0 ? GetLogsForIteration(CurrentIteration) : string.Empty;

            await RefreshIterationsAsync();
            await RefreshLogsAsync();

            if (showingLastIteration && _availableIterations.Any())
                await SetIterationAsync(_availableIterations.Max());

            // If current iteration is no longer available, switch to the highest available
            if (!_availableIterations.Contains(CurrentIteration))
                await SetIterationAsync(_availableIterations.Any() ? _availableIterations.Max() : -1);

            // Only set new flags if logs actually changed and panel is not showing them
            bool errorsChanged = _currentErrors != previousErrors;
            bool stagingChanged = _currentStaging != previousStaging;
            bool newIterationsAvailable = _availableIterations.Count > previousIterationCount;
            bool currentIterationLogChanged = CurrentIteration >= 0 && GetLogsForIteration(CurrentIteration) != previousCurrentIterationLog;

            if (stagingChanged &&
                (!_isLogPanelExpanded || _currentSection != LogSection.Staging))
                _hasNewStaging = true;
            if ((newIterationsAvailable || currentIterationLogChanged) &&
                (!_isLogPanelExpanded || _currentSection != LogSection.Output))
                _hasNewLogs = true;
            if (errorsChanged &&
                (!_isLogPanelExpanded || _currentSection != LogSection.Errors))
                _hasNewErrors = true;

            _logger.LogDebug("Job updated event raised for job {JobId}", _job?.Id);
            await OnJobUpdated.InvokeAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling job update for job {JobId}", _job?.Id);
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
    private async Task RefreshIterationsAsync()
    {
        if (_job == null)
            return;
            
        var newIterations = new List<int>();
        var newMetadata = new Dictionary<int, (bool hasLogs, bool hasResults, bool hasVis)>();

        // Build new collections without holding the lock
        for (int i = 0; i <= Math.Max(_job.LogsAvailableIteration, _job.VisAvailableIteration); i++)
        {
            bool hasLogs = i <= _job.LogsAvailableIteration;
            bool hasResults = _job.HasResultFilesForIteration(i);
            bool hasVis = i <= _job.VisAvailableIteration;
            
            if (hasLogs || hasResults || hasVis)
            {
                newIterations.Add(i);
                newMetadata[i] = (hasLogs, hasResults, hasVis);
            }
        }
        
        newIterations.Sort();

        // Update collections under lock
        lock (_iterationsLock)
        {
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
    private async Task RefreshLogsAsync()
    {
        if (_job == null)
            return;

        // Phase 1: Update all caches from disk before firing any events.
        // This ensures that even if an event handler throws, subsequent handlers
        // and renders will see the latest data.
        try
        {
            // Handle error log
            if (Directory.Exists(_job.DirectoryPath))
            {
                string errorPath = _job.ErrorFilePath;
                _currentErrors = File.Exists(errorPath)
                    ? await File.ReadAllTextAsync(errorPath)
                    : string.Empty;
            }
            else
            {
                _currentErrors = string.Empty;
            }

            // Handle staging log
            if (Directory.Exists(_job.DirectoryPath))
            {
                string stagingPath = _job.LifecycleFilePath;
                if (File.Exists(stagingPath))
                {
                    string rawStagingContent = await File.ReadAllTextAsync(stagingPath);
                    _currentStaging = ProcessStagingContent(rawStagingContent);
                }
                else
                {
                    _currentStaging = string.Empty;
                }
            }
            else
            {
                _currentStaging = string.Empty;
            }

            // Update log cache for all available iterations
            List<int> iterationsToProcess;
            lock (_iterationsLock)
            {
                iterationsToProcess = _availableIterations.ToList();
            }

            foreach (int iteration in iterationsToProcess)
            {
                if (_job == null) break; // Job could have been cleared while processing

                string logPath = _job.LogFilePath(iteration);
                if (File.Exists(logPath))
                {
                    int maxIteration;
                    lock (_iterationsLock)
                    {
                        maxIteration = _availableIterations.Any() ? _availableIterations.Max() : -1;
                    }

                    // For current and last two iterations, always re-read from disk
                    // This ensures we have the most up-to-date logs for iterations that might still be active
                    if (iteration == _currentIteration ||
                        iteration == maxIteration ||
                        iteration == maxIteration - 1 ||
                        !_cachedLogs.ContainsKey(iteration))

                        _cachedLogs[iteration] = await File.ReadAllTextAsync(logPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading log files from disk for job {JobId}", _job?.Id);
        }

        // Phase 2: Notify subscribers. Each event is fired independently so that
        // a failure in one handler doesn't prevent the others from running.

        try { await OnErrorsUpdated.InvokeAllAsync(_currentErrors); }
        catch (Exception ex) { _logger.LogError(ex, "Error notifying error log subscribers for job {JobId}", _job?.Id); }

        try { await OnStagingUpdated.InvokeAllAsync(_currentStaging); }
        catch (Exception ex) { _logger.LogError(ex, "Error notifying staging log subscribers for job {JobId}", _job?.Id); }

        try { await OnLogsUpdated.InvokeAllAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Error notifying output log subscribers for job {JobId}", _job?.Id); }
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
        if (_cachedLogs.TryGetValue(iteration, out var logs))
            return logs;
        return string.Empty;
    }
    
    /// <summary>
    /// Toggles the expanded/collapsed state of the log panel.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task ToggleLogPanelExpanded()
    {
        await SetLogPanelExpanded(!_isLogPanelExpanded);
    }

    /// <summary>
    /// Sets the expanded/collapsed state of the log panel.
    /// </summary>
    /// <param name="expanded">True to expand the panel, false to collapse it</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// When expanding the panel, this method also resets the "new logs" indicator
    /// to avoid showing a notification for logs that are now visible.
    /// </remarks>
    private async Task SetLogPanelExpanded(bool expanded)
    {
        if (_isLogPanelExpanded != expanded)
        {
            _isLogPanelExpanded = expanded;
            _hasNewLogs = false; // Reset new logs indicator when opening
            _hasNewStaging = false; // Reset new staging indicator when opening
            await OnLogPanelStateChanged.InvokeAllAsync();
        }
    }
    
    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    /// <remarks>
    /// This method cleans up event subscriptions to prevent memory leaks when the service is no longer needed.
    /// </remarks>
    public void Dispose()
    {
        foreach(var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
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