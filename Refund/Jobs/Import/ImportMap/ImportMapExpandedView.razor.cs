using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Refinement.Refinement3D.Refine3D;
using Refund.Services;

namespace Refund.Jobs.Import.ImportMap;

public partial class ImportMapExpandedView
{
    /// <summary>
    /// Service for managing expanded job view state
    /// </summary>
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    
    /// <summary>
    /// Service for displaying toast notifications
    /// </summary>
    [Inject] private IToastService ToastService { get; set; }
    
    /// <summary>
    /// The Refine3D job currently being viewed
    /// </summary>
    private ReadOnlyImportMap _job;

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
        // Check if this is a Refine3D job
        if (job is ReadOnlyImportMap importMap)
        {
            _job = importMap;
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
    public async ValueTask DisposeAsync()
    {
        ExpandedViewService.OnJobChanged -= HandleJobChanged;
        ExpandedViewService.OnJobUpdated -= HandleJobUpdated;
        ExpandedViewService.OnIterationChanged -= HandleIterationChanged;
    }
}