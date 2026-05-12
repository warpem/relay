using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Panels.Right;

/// <summary>
/// A component that displays properties and results for the currently selected job(s) in the right panel.
/// </summary>
/// <remarks>
/// This component shows information about one or more selected jobs, including:
/// - Basic metadata (name, creation date, author)
/// - Detailed properties organized in tabs
/// - Job results with iteration selection
/// - Downloadable resources from job output ports
/// 
/// It updates dynamically when job status changes, particularly for displaying results
/// as they become available during job execution.
/// </remarks>
public partial class JobProperties : ComponentBase, IDisposable
{
    /// <summary>
    /// Gets or sets the collection of jobs to display properties for.
    /// </summary>
    /// <remarks>
    /// The component can show properties for multiple selected jobs, though some features
    /// like result viewing only work when a single job is selected.
    /// </remarks>
    [Parameter]
    public IEnumerable<ReadOnlyJob> Jobs { get; set; }

    /// <summary>
    /// Subscriptions to job update events, used to refresh the display when jobs change.
    /// </summary>
    private List<GroupEventSubscription> _subscriptions = new();

    /// <summary>
    /// The currently selected iteration for viewing job results.
    /// </summary>
    /// <remarks>
    /// Jobs often produce results in multiple iterations (e.g., optimization steps).
    /// This tracks which iteration's results are currently displayed.
    /// Default is -1, meaning no iteration is selected.
    /// </remarks>
    private int _resultIteration = -1;
    
    /// <summary>
    /// List of iteration indices that have available results.
    /// </summary>
    private readonly List<int> _iterationsWithResults = new();
    
    /// <summary>
    /// Whether the result iteration was manually set by the user.
    /// </summary>
    /// <remarks>
    /// When false, the component will automatically select the latest iteration with results.
    /// When true, the user's selected iteration is preserved even when new results arrive.
    /// </remarks>
    private bool _resultIterationSetManually = false;
    
    /// <summary>
    /// Whether the timeline is expanded to show all events or collapsed to hide events before the most recent clearing.
    /// </summary>
    private bool _timelineExpanded = false;
    
    /// <summary>
    /// List of output ports that have downloadable results for the current iteration.
    /// </summary>
    private readonly List<ReadOnlyPortOut> _portsWithDownloadables = new();
    
    /// <summary>
    /// Gets or sets the data manager service for updating jobs.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }
    
    /// <summary>
    /// Gets or sets the session service for the current user context.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }
    
    /// <summary>
    /// Gets or sets the JavaScript runtime for clipboard operations.
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; }
    
    /// <summary>
    /// Gets or sets the toast service for showing notifications.
    /// </summary>
    [Inject]
    private IToastService ToastService { get; set; }

    [Inject]
    private IDialogService DialogService { get; set; }

    /// <summary>
    /// Initializes subscriptions and updates the display when parameters change.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        base.OnParametersSet();
        
        // Clean up old subscriptions
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();

        // Reset state
        _resultIteration = -1;
        _iterationsWithResults.Clear();
        _resultIterationSetManually = false;
        _timelineExpanded = false;

        UpdateResults();
        
        // Set up new subscriptions for job updates
        if (Jobs != null && Jobs.Any())
            foreach (var job in Jobs)
                _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(job.Space.Project.Id, job.Space.Id, job.Id),
                                                              HandleJobUpdated));
    }

    /// <summary>
    /// Handles updates to a job's data, refreshing results and the UI.
    /// </summary>
    /// <param name="args">Event arguments containing the updated job</param>
    private async Task HandleJobUpdated(GroupEventArgs<ReadOnlyJob> args)
    {
        UpdateResults();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Updates the lists of available results and downloadable resources.
    /// </summary>
    /// <remarks>
    /// This method is called when jobs are updated or parameters change.
    /// It determines which iterations have results and which ports have
    /// downloadable files, and updates the current result iteration if needed.
    /// </remarks>
    private void UpdateResults()
    {
        _iterationsWithResults.Clear();
        _portsWithDownloadables.Clear();
        
        if (Jobs != null && Jobs.Count() == 1)
        {
            var job = Jobs.First();

            // Find all iterations that have result files
            _iterationsWithResults.AddRange(Enumerable.Range(0, job.LogsAvailableIteration + 1)
                                                      .Where(i => job.HasResultFilesForIteration(i)));
                    
            // Automatically select the latest iteration if the user hasn't manually chosen one
            if (_iterationsWithResults.Any() && !_resultIterationSetManually)
                _resultIteration = _iterationsWithResults.Max();

            // Ensure the selected iteration is valid
            _resultIteration = Math.Min(_resultIteration, _iterationsWithResults.Any() ? 
                                                              _iterationsWithResults.Max() : 
                                                              -1);

            // Find ports with downloadable resources for the current iteration
            foreach (var port in job.PortsOut.Values)
            {
                try
                {
                    var downloadables = port.GetResource(_resultIteration)?.GetDownloadables();
                    if (downloadables == null)
                        continue;

                    if (downloadables.Any())
                        _portsWithDownloadables.Add(port);
                }
                catch (Exception ex)
                {
                    // Log or handle the error if necessary
                    Console.Error.WriteLine($"Error getting downloadables for {port.Job.QualifiedName} port {port.Name}: {ex.Message}");
                }
            }
        }
    }
    
    #region Details tab

    /// <summary>
    /// Gets a display name for a queue by its ID.
    /// Returns the queue's alias, or a fallback string if the queue no longer exists.
    /// </summary>
    /// <param name="queueId">The queue ID (-1 for local, positive for cluster queues)</param>
    /// <returns>A human-readable queue name</returns>
    private string GetQueueDisplayName(int queueId)
    {
        if (queueId == -1)
            return DataManager.LocalQueue.Alias ?? "Local";

        var queue = DataManager.FindClusterQueue(queueId);
        return queue?.Alias ?? $"Unknown queue (ID: {queueId})";
    }

    /// <summary>
    /// Updates a job's alias when changed in the UI.
    /// </summary>
    /// <param name="value">The new alias</param>
    private async Task HandleJobAliasChanged(string value)
    {
        await DataManager.UpdateJob(Session.User, Jobs.First(), originalJob =>
        {
            originalJob.Alias = value;
        });
    }

    /// <summary>
    /// Updates a job's notes when changed in the UI.
    /// </summary>
    /// <param name="value">The new notes</param>
    private async Task HandleJobNotesChanged(string value)
    {
        await DataManager.UpdateJob(Session.User, Jobs.First(), originalJob =>
        {
            originalJob.Notes = value;
        });
    }

    /// <summary>
    /// Copies the space's root directory path to the clipboard.
    /// </summary>
    private async Task HandlePathCopyClicked()
    {
        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", Jobs.First().DirectoryPath);
        ToastService.ShowSuccess("Path copied to clipboard", timeout: 1000);
    }

    private async Task HandleBrowseFolderClicked()
    {
        await FileBrowserDialog.Show(
            DialogService,
            this,
            _ => Task.CompletedTask,
            "Browse Files",
            currentFolder: Jobs.First().DirectoryPath,
            showSelectionButtons: false);
    }
    
    #endregion
    
    #region Results tab

    /// <summary>
    /// Handles the user selecting a different iteration to view results from.
    /// </summary>
    /// <param name="value">The selected iteration index</param>
    private async Task HandleResultIterationChanged(int value)
    {
        _resultIterationSetManually = true;
        _resultIteration = value;
    }
    
    #endregion
    
    #region Timeline
    
    /// <summary>
    /// Translates an EventType enum value to a human-friendly display string.
    /// </summary>
    /// <param name="eventType">The event type to translate.</param>
    /// <returns>A human-friendly description of the event type.</returns>
    private static string GetEventDisplayText(EventType eventType)
    {
        return eventType switch
        {
            EventType.Created => "Created",
            EventType.WaitingStarted => "Waiting",
            EventType.StagingStarted => "Staging",
            EventType.RunningStarted => "Running",
            EventType.Finished => "Finished",
            EventType.Failed => "Failed",
            EventType.Aborted => "Aborted",
            EventType.ClearingStarted => "Clearing",
            EventType.Deleted => "Deleted",
            _ => eventType.ToString() // Fallback to enum name
        };
    }
    
    /// <summary>
    /// Gets the events to display in the timeline, handling collapsing logic.
    /// </summary>
    /// <param name="job">The job whose events to process.</param>
    /// <returns>A tuple containing the visible events and the count of collapsed events.</returns>
    private (List<ReadOnlyJobEvent> VisibleEvents, int CollapsedCount) GetTimelineEvents(ReadOnlyJob job)
    {
        var allEvents = job.GetEvents(null);
        
        if (!allEvents.Any())
            return (new List<ReadOnlyJobEvent>(), 0);
            
        // If timeline is expanded, show all events
        if (_timelineExpanded)
            return (allEvents.ToList(), 0);
            
        // Find the most recent clearing event
        var mostRecentClearing = allEvents
            .Where(e => e.Type == EventType.ClearingStarted)
            .LastOrDefault();
            
        if (mostRecentClearing == null)
        {
            // No clearing events, show all events
            return (allEvents.ToList(), 0);
        }
        
        // Find the created event (should always be first)
        var createdEvent = allEvents.FirstOrDefault(e => e.Type == EventType.Created);
        
        // Show: Created event + events after the most recent clearing
        var visibleEvents = new List<ReadOnlyJobEvent>();
        
        if (createdEvent != null)
            visibleEvents.Add(createdEvent);
            
        var eventsAfterClearing = allEvents
            .Where(e => e.Timestamp > mostRecentClearing.Timestamp)
            .ToList();
            
        visibleEvents.AddRange(eventsAfterClearing);
        
        // Count collapsed events (from after creation up to and including the most recent clearing)
        var collapsedEvents = allEvents
            .Where(e => e.Type != EventType.Created && e.Timestamp <= mostRecentClearing.Timestamp)
            .ToList();
            
        return (visibleEvents, collapsedEvents.Count);
    }
    
    /// <summary>
    /// Toggles the timeline expansion state.
    /// </summary>
    private void ToggleTimelineExpansion()
    {
        _timelineExpanded = !_timelineExpanded;
    }
    
    #endregion
    
    #region Parameters tab
    
    /// <summary>
    /// Returns an empty string for error messages since parameters are read-only.
    /// </summary>
    /// <param name="parameterName">The parameter name (unused)</param>
    /// <returns>Always returns an empty string</returns>
    private string GetNoError(string parameterName) => string.Empty;
    
    /// <summary>
    /// Handles parameter changes (no-op since parameters are read-only).
    /// </summary>
    /// <param name="args">The parameter change arguments (unused)</param>
    private Task HandleParameterChanged((System.Reflection.PropertyInfo prop, object value) args)
    {
        // Parameters are read-only in JobProperties, so this is a no-op
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Returns an empty list for port errors since ports are read-only.
    /// </summary>
    /// <param name="portName">The port name (unused)</param>
    /// <returns>Always returns an empty list</returns>
    private List<string> GetNoPortErrors(string portName) => new List<string>();
    
    /// <summary>
    /// Handles edge removal (no-op since ports are read-only).
    /// </summary>
    /// <param name="edge">The edge to remove (unused)</param>
    private Task HandleEdgeRemoved(ReadOnlyEdge edge)
    {
        // Ports are read-only in JobProperties, so this is a no-op
        return Task.CompletedTask;
    }
    
    #endregion

    /// <summary>
    /// Cleans up subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
    }
}