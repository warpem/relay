using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

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

    private ReadOnlyJob _job;
    
    /// <summary>
    /// Gets the job currently being edited.
    /// </summary>
    public ReadOnlyJob CurrentJob => _job;
    
    /// <summary>
    /// Gets a value indicating whether the service is actively editing a job.
    /// </summary>
    public bool IsActive => _job != null;
    
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
        if (_job != null)
            await SetJob(null);
    }

    private async Task HandleFactoryDefinitionChanged()
    {
        if (_job != null)
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
    public async Task SetJob(ReadOnlyJob job)
    {
        foreach(var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
        
        _job = job;

        if (job != null)
        {
            await _session.SetRightPanelCollapsed(false);

            // Blueprint sub-jobs in the factory builder have no Space — skip event subscriptions.
            // Their updates are handled through FactoryDefinitionUpdated in the JobEditor component.
            if (job.Space != null)
            {
                _subscriptions.Add(_dataManager.JobUpdated.Add(GroupName.Job(job.Space.Project.Id, job.Space.Id, job.Id),
                                                               async (args) =>
                                                               {
                                                                   if (args.Object.Status != DataModel.JobStatus.Building)
                                                                   {
                                                                       await SetJob(null);
                                                                       return;
                                                                   }

                                                                   await OnJobUpdated.InvokeAllAsync(args.Object);
                                                               }));

                _subscriptions.Add(_dataManager.JobDeleted.Add(GroupName.Job(job.Space.Project.Id, job.Space.Id, job.Id),
                                                               async _ => await SetJob(null)));
            }
        }
        
        await OnJobChanged.InvokeAllAsync(job);
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
        foreach(var sub in _subscriptions)
            sub.Unsubscribe();
    }
}