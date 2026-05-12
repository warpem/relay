using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

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

    private ReadOnlyFactoryInstance _instance;

    /// <summary>
    /// Gets the factory instance currently being edited.
    /// </summary>
    public ReadOnlyFactoryInstance CurrentInstance => _instance;

    /// <summary>
    /// Gets a value indicating whether the service is actively editing a factory instance.
    /// </summary>
    public bool IsActive => _instance != null;

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
        if (job != null && _instance != null)
            await SetInstance(null);
    }

    private async Task HandleSpaceChanged()
    {
        if (_instance != null)
            await SetInstance(null);
    }

    private async Task HandleFactoryDefinitionChanged()
    {
        if (_instance != null)
            await SetInstance(null);
    }

    /// <summary>
    /// Sets the factory instance to be edited.
    /// </summary>
    /// <param name="instance">The factory instance to edit, or null to clear</param>
    public async Task SetInstance(ReadOnlyFactoryInstance instance)
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();

        _instance = instance;

        if (instance != null)
        {
            if (_jobEditor.IsActive)
                await _jobEditor.SetJob(null);

            await _session.SetRightPanelCollapsed(false);

            _subscriptions.Add(_dataManager.FactoryInstanceUpdated.Add(
                GroupName.FactoryInstance(instance.Space.Project.Id, instance.Space.Id, instance.Id),
                async args => await OnInstanceUpdated.InvokeAllAsync(args.Object)));

            _subscriptions.Add(_dataManager.FactoryInstanceDeleted.Add(
                GroupName.FactoryInstance(instance.Space.Project.Id, instance.Space.Id, instance.Id),
                async _ => await SetInstance(null)));

            // Sub-job status changes affect the instance's aggregate status.
            // Close the editor if no sub-jobs remain in Building status.
            _subscriptions.Add(_dataManager.JobUpdated.Add(
                GroupName.Job(instance.Space.Project.Id, instance.Space.Id, null),
                async args =>
                {
                    if (instance.SubJobIds.Contains(args.Object.Id))
                    {
                        if (!instance.SubJobs.Any(j => j.Status == DataModel.JobStatus.Building))
                        {
                            await SetInstance(null);
                            return;
                        }

                        await OnInstanceUpdated.InvokeAllAsync(instance);
                    }
                }));
        }

        await OnInstanceChanged.InvokeAllAsync(instance);
    }

    public void Dispose()
    {
        _session.OnSpaceChanged -= HandleSpaceChanged;
        _session.OnFactoryDefinitionChanged -= HandleFactoryDefinitionChanged;
        _jobEditor.OnJobChanged -= HandleJobEditorChanged;
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
    }
}
