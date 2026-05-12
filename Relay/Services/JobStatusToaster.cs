using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;

namespace Relay.Services;

/// <summary>
/// Service responsible for displaying toast notifications when job statuses change.
/// Subscribes to job status change events from the DataManager and shows appropriate notifications
/// to inform users about job queuing, completion, or failure without requiring them to
/// actively monitor the job status screen.
/// </summary>
/// <remarks>
/// This service is registered as a scoped service in the dependency injection container,
/// alongside other UI state management services like CardSelectionService, JobEditorService,
/// ExpandedJobViewService, and MenuActionService. It's specifically designed to provide
/// real-time feedback on background job processing operations.
/// </remarks>
public class JobStatusToaster : IDisposable
{
    private DataManager DataManager { get; set; }
    private IToastService ToastService { get; set; }

    /// <summary>
    /// Collection of event subscriptions that need to be cleaned up when this service is disposed.
    /// </summary>
    private readonly List<GroupEventSubscription> _subscriptions = new();

    /// <summary>
    /// Initializes a new instance of the JobStatusToaster service.
    /// Subscribes to job status change events in the DataManager to provide real-time notifications.
    /// </summary>
    /// <param name="dataManager">The DataManager service that provides job status events</param>
    /// <param name="toastService">The FluentUI toast service used to display notifications</param>
    public JobStatusToaster(DataManager dataManager, IToastService toastService)
    {
        DataManager = dataManager;
        ToastService = toastService;

        // Subscribe to all job events regardless of project, space, or job ID
        _subscriptions.Add(DataManager.JobQueued.Add("P*_S*_J*", OnJobQueued));
        _subscriptions.Add(DataManager.JobFinished.Add("P*_S*_J*", OnJobFinished));
        _subscriptions.Add(DataManager.JobFailed.Add("P*_S*_J*", OnJobFailed));
    }

    /// <summary>
    /// Handler for job queued events. Displays a progress toast notification.
    /// </summary>
    /// <param name="args">Event arguments containing the job that was queued</param>
    private async Task OnJobQueued(GroupEventArgs<ReadOnlyJob> args)
    {
        ReadOnlyJob j = args.Object;
        ToastService.ShowToast(ToastIntent.Progress, $"Queued Job P{j.Space.Project.Id}-S{j.Space.Id}-{j.QualifiedName}");
    }

    /// <summary>
    /// Handler for job finished events. Displays a success toast notification.
    /// </summary>
    /// <param name="args">Event arguments containing the job that finished</param>
    private async Task OnJobFinished(GroupEventArgs<ReadOnlyJob> args)
    {
        ReadOnlyJob j = args.Object;
        ToastService.ShowToast(ToastIntent.Success, $"Finished Job P{j.Space.Project.Id}-S{j.Space.Id}-{j.QualifiedName}");
    }

    /// <summary>
    /// Handler for job failed events. Displays an error toast notification.
    /// </summary>
    /// <param name="args">Event arguments containing the job that failed</param>
    private async Task OnJobFailed(GroupEventArgs<ReadOnlyJob> args)
    {
        ReadOnlyJob j = args.Object;
        ToastService.ShowToast(ToastIntent.Error, $"Failed Job P{j.Space.Project.Id}-S{j.Space.Id}-{j.QualifiedName}");
    }

    /// <summary>
    /// Cleans up all event subscriptions when the service is disposed.
    /// </summary>
    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
    }
}