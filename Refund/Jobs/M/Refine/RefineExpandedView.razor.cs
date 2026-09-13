using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;

namespace Refund.Jobs.M.Refine;

public partial class RefineExpandedView
{
    /// <summary>
    /// Service for managing expanded job view state
    /// </summary>
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    
    /// <summary>
    /// The M refinement job currently being viewed
    /// </summary>
    private ReadOnlyRefine _job;

    /// <summary>
    /// Initializes the component and sets up event handlers
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        // Subscribe to events from the expanded view service
        ExpandedViewService.OnJobChanged += HandleJobChanged;
        ExpandedViewService.OnJobUpdated += HandleJobUpdated;
        ExpandedViewService.OnIterationChanged += HandleIterationChanged;
        
        // Load initial job data
        await HandleJobChanged(ExpandedViewService.CurrentJob);
    }

    /// <summary>
    /// Handles changes to the currently displayed job
    /// </summary>
    /// <param name="job">The new job being displayed</param>
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        // Check if this is an M refinement job
        if (job is ReadOnlyRefine refine)
        {
            _job = refine;
        }
        else
        {
            _job = null;
        }
        
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles updates to the current job's data
    /// </summary>
    private async Task HandleJobUpdated()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles changes to the displayed iteration
    /// </summary>
    /// <param name="iteration">The new iteration number</param>
    private async Task HandleIterationChanged(int iteration)
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Unsubscribes from events when the component is disposed
    /// </summary>
    public ValueTask DisposeAsync()
    {
        ExpandedViewService.OnJobChanged -= HandleJobChanged;
        ExpandedViewService.OnJobUpdated -= HandleJobUpdated;
        ExpandedViewService.OnIterationChanged -= HandleIterationChanged;
        return ValueTask.CompletedTask;
    }
}
