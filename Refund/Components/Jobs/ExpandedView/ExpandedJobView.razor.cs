using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;

namespace Refund.Components.Jobs.ExpandedView;

/// <summary>
/// Component that displays the expanded details view for a job.
/// This is the primary component for showing detailed job information, logs, and visualizations
/// when a user has selected a job to examine in more depth.
/// </summary>
public partial class ExpandedJobView : IDisposable
{
    /// <summary>
    /// Service that manages the currently expanded job and provides methods
    /// for interacting with the expanded job view across the application.
    /// </summary>
    [Inject]
    public ExpandedJobViewService Service { get; set; }

    /// <summary>
    /// Initializes the component and subscribes to job change events.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        Service.OnJobChanged += HandleJobChanged;
        Service.OnIterationChanged += HandleIterationChanged;
        Service.OnJobUpdated += HandleJobUpdated;
    }

    /// <summary>
    /// Handles events when the selected job changes in the service.
    /// Forces the component to re-render with the new job information.
    /// </summary>
    /// <param name="job">The newly selected job</param>
    private Task HandleJobChanged(ReadOnlyJob job) => RefreshAsync();

    /// <summary>
    /// Re-renders so the job view's error boundary sees the new iteration and can give the view another try.
    /// </summary>
    private Task HandleIterationChanged(int iteration) => RefreshAsync();

    /// <summary>
    /// Re-renders so the job view's error boundary sees status changes (e.g. outputs finished being written).
    /// </summary>
    private Task HandleJobUpdated() => RefreshAsync();

    /// <summary>
    /// The service awaits its handlers one after another and stops at the first throw, so this never throws.
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
        }
    }
    
    /// <summary>
    /// Cleans up event subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        Service.OnJobChanged -= HandleJobChanged;
        Service.OnIterationChanged -= HandleIterationChanged;
        Service.OnJobUpdated -= HandleJobUpdated;
    }
}