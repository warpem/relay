using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Refund.Services;

/// <summary>
/// Provides services for editing and tracking changes to a factory instance.
/// </summary>
/// <remarks>
/// This service manages the state of a factory instance being edited in the UI. It:
/// - Tracks the currently edited factory instance
/// - Subscribes to update and deletion events for the instance and its sub-jobs
/// - Notifies subscribers when the instance changes or is updated
/// </remarks>
public class FactoryEditorService : IDisposable
{
    private readonly DataManager _dataManager;
    private readonly RelaySession _session;
    private readonly JobEditorService _jobEditor;
    private readonly List<GroupEventSubscription> _subscriptions = new();

    private readonly object _subscriptionLock = new();
    private long _version;
    private bool _disposed;
    private ReadOnlyFactoryInstance _instance;

    /// <summary>
    /// Gets the factory instance currently being edited.
    /// </summary>
    public ReadOnlyFactoryInstance CurrentInstance { get { lock (_subscriptionLock) return _instance; } }

    /// <summary>
    /// Gets a value indicating whether the service is actively editing a factory instance.
    /// </summary>
    public bool IsActive => CurrentInstance != null;

    /// <summary>
    /// Event raised when the factory instance being edited changes (a different instance is selected).
    /// </summary>
    public Func<ReadOnlyFactoryInstance, Task> OnInstanceChanged { get; set; }

    /// <summary>
    /// Event raised when the current factory instance is updated (e.g., sub-job status changes).
    /// </summary>
    public Func<ReadOnlyFactoryInstance, Task> OnInstanceUpdated { get; set; }

    public FactoryEditorService(DataManager dataManager, RelaySession session, JobEditorService jobEditor)
    {
        _dataManager = dataManager;
        _session = session;
        _jobEditor = jobEditor;
        _session.OnSpaceChanged += HandleSpaceChanged;
        _session.OnFactoryDefinitionChanged += HandleFactoryDefinitionChanged;
        _jobEditor.OnJobChanged += HandleJobEditorChanged;
    }

    private async Task HandleJobEditorChanged(ReadOnlyJob job)
    {
        if (job != null && _jobEditor.CurrentJob == job && CurrentInstance != null)
            await SetInstance(null);
    }

    private async Task HandleSpaceChanged()
    {
        if (CurrentInstance != null)
            await SetInstance(null);
    }

    private async Task HandleFactoryDefinitionChanged()
    {
        if (CurrentInstance != null)
            await SetInstance(null);
    }

    /// <summary>
    /// Sets the factory instance to be edited.
    /// </summary>
    /// <param name="instance">The factory instance to edit, or null to clear</param>
    public Task SetInstance(ReadOnlyFactoryInstance instance) => SetInstance(instance, null);

    private async Task SetInstance(ReadOnlyFactoryInstance instance, long? expectedVersion)
    {
        long version;
        lock (_subscriptionLock)
        {
            if (_disposed || (expectedVersion.HasValue && expectedVersion != _version)) return;
            ClearSubscriptions();
            _instance = instance;
            version = ++_version;

            if (instance != null)
            {
                var group = GroupName.FactoryInstance(instance.Space.Project.Id, instance.Space.Id, instance.Id);
                _subscriptions.Add(_dataManager.FactoryInstanceUpdated.Add(group,
                    args => Notify(version, OnInstanceUpdated, args.Object)));
                _subscriptions.Add(_dataManager.FactoryInstanceDeleted.Add(group,
                    _ => SetInstance(null, version)));
                _subscriptions.Add(_dataManager.JobUpdated.Add(
                    GroupName.Job(instance.Space.Project.Id, instance.Space.Id, null), async args =>
                    {
                        if (!IsCurrent(version) || !instance.SubJobIds.Contains(args.Object.Id)) return;
                        if (!instance.SubJobs.Any(j => j.Status == JobStatus.Building))
                            await SetInstance(null, version);
                        else
                            await Notify(version, OnInstanceUpdated, instance);
                    }));
            }
        }

        if (instance != null && IsCurrent(version))
        {
            var job = _jobEditor.CurrentJob;
            if (job != null)
                await _jobEditor.ClearJob(job);
            if (IsCurrent(version))
                await _session.SetRightPanelCollapsed(false);
        }
        await Notify(version, OnInstanceChanged, instance);
    }

    private bool IsCurrent(long version)
    {
        lock (_subscriptionLock) return !_disposed && version == _version;
    }

    private async Task Notify(long version, Func<ReadOnlyFactoryInstance, Task> handlers, ReadOnlyFactoryInstance instance)
    {
        if (handlers == null) return;
        foreach (Func<ReadOnlyFactoryInstance, Task> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(version)) return;
            await handler(instance);
        }
    }

    private void ClearSubscriptions()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        foreach (var subscription in subscriptions)
            subscription.Unsubscribe();
    }

    public void Dispose()
    {
        _session.OnSpaceChanged -= HandleSpaceChanged;
        _session.OnFactoryDefinitionChanged -= HandleFactoryDefinitionChanged;
        _jobEditor.OnJobChanged -= HandleJobEditorChanged;
        lock (_subscriptionLock)
        {
            _disposed = true;
            ++_version;
            _instance = null;
            ClearSubscriptions();
        }
    }
}
