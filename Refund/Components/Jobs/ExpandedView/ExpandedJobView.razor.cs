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
    }

    /// <summary>
    /// Handles events when the selected job changes in the service.
    /// Forces the component to re-render with the new job information.
    /// </summary>
    /// <param name="job">The newly selected job</param>
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Cleans up event subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        Service.OnJobChanged -= HandleJobChanged;
    }
}