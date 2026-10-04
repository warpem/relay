using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Refund.Services;

/// <summary>
/// Provides services for editing and tracking changes to a job.
/// </summary>
/// <remarks>
/// This service manages the state of a job being edited in the UI. It:
/// - Tracks the currently edited job
/// - Subscribes to update and deletion events for the job
/// - Notifies subscribers when the job changes or is updated
/// 
/// The service is designed to be used by job editor components that need to react
/// to changes in the job state (e.g., parameter updates, status changes).
/// </remarks>
public class JobEditorService : IDisposable
{
    private readonly DataManager _dataManager;
    private readonly RelaySession _session;
    private readonly List<GroupEventSubscription> _subscriptions = new();
    private readonly object _subscriptionLock = new();

    private ReadOnlyJob _job;
    private long _version;
    private bool _disposed;
    
    /// <summary>
    /// Gets the job currently being edited.
    /// </summary>
    public ReadOnlyJob CurrentJob { get { lock (_subscriptionLock) return _job; } }
    
    /// <summary>
    /// Gets a value indicating whether the service is actively editing a job.
    /// </summary>
    public bool IsActive => CurrentJob != null;
    
    /// <summary>
    /// Event raised when the job being edited changes (a different job is selected).
    /// </summary>
    public Func<ReadOnlyJob, Task> OnJobChanged { get; set; }
    
    /// <summary>
    /// Event raised when the current job is updated (e.g., parameters change, status changes).
    /// </summary>
    public Func<ReadOnlyJob, Task> OnJobUpdated { get; set; }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="JobEditorService"/> class.
    /// </summary>
    /// <param name="dataManager">The data manager service for subscribing to job events</param>
    public JobEditorService(DataManager dataManager, RelaySession session)
    {
        _dataManager = dataManager;
        _session = session;
        _session.OnSpaceChanged += HandleSpaceChanged;
        _session.OnFactoryDefinitionChanged += HandleFactoryDefinitionChanged;
    }

    private async Task HandleSpaceChanged()
    {
        if (CurrentJob != null)
            await SetJob(null);
    }

    private async Task HandleFactoryDefinitionChanged()
    {
        if (CurrentJob != null)
            await SetJob(null);
    }
    
    /// <summary>
    /// Sets the job to be edited.
    /// </summary>
    /// <param name="job">The job to edit, or null to clear the current job</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method:
    /// 1. Unsubscribes from events for the previous job
    /// 2. Sets the new job as the current job
    /// 3. Sets up event subscriptions for the new job:
    ///    - Subscribes to job updates to notify when the job changes
    ///    - Subscribes to job deletion to automatically clear the job if it's deleted
    /// 4. Notifies subscribers that the job has changed
    /// </remarks>
    public Task SetJob(ReadOnlyJob job) => SetJob(job, null);

    /// <summary>Closes the editor only if it still contains the submitted job.</summary>
    public Task ClearJob(ReadOnlyJob job)
    {
        long version;
        lock (_subscriptionLock)
        {
            if (_job != job || _disposed) return Task.CompletedTask;
            version = _version;
        }
        return SetJob(null, version);
    }

    private async Task SetJob(ReadOnlyJob job, long? expectedVersion)
    {
        long version;
        lock (_subscriptionLock)
        {
            if (_disposed || (expectedVersion.HasValue && expectedVersion != _version)) return;
            ClearSubscriptions();
            _job = job;
            version = ++_version;

            // Capture this editing session, including when the same job is reopened.
            if (job?.Space != null)
            {
                var group = GroupName.SpecificJob(job);
                _subscriptions.Add(_dataManager.JobUpdated.Add(group, async args =>
                {
                    if (!IsCurrent(version)) return;
                    if (args.Object.Status != DataModel.JobStatus.Building)
                        await SetJob(null, version);
                    else
                        await Notify(version, OnJobUpdated, args.Object);
                }));
                _subscriptions.Add(_dataManager.JobDeleted.Add(group, _ => SetJob(null, version)));
            }
        }

        if (job != null && IsCurrent(version))
            await _session.SetRightPanelCollapsed(false);
        await Notify(version, OnJobChanged, job);
    }

    private bool IsCurrent(long version)
    {
        lock (_subscriptionLock) return !_disposed && version == _version;
    }

    private async Task Notify(long version, Func<ReadOnlyJob, Task> handlers, ReadOnlyJob job)
    {
        if (handlers == null) return;
        foreach (Func<ReadOnlyJob, Task> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(version)) return;
            await handler(job);
        }
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    /// <remarks>
    /// This method unsubscribes from all event subscriptions to prevent memory leaks.
    /// </remarks>
    public void Dispose()
    {
        _session.OnSpaceChanged -= HandleSpaceChanged;
        _session.OnFactoryDefinitionChanged -= HandleFactoryDefinitionChanged;
        lock (_subscriptionLock)
        {
            _disposed = true;
            ++_version;
            _job = null;
            ClearSubscriptions();
        }
    }

    // Caller holds _subscriptionLock. Detach first so cleanup is also reentrant.
    private void ClearSubscriptions()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        foreach (var subscription in subscriptions)
            subscription.Unsubscribe();
    }
}
