using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.Session;

namespace Refund.Components.Jobs.ExpandedView;

/// <summary>
/// Component for displaying and navigating between different iterations of a job's output.
/// Provides a responsive pagination interface that adapts to available screen space
/// and intelligently shows iteration numbers based on current selection.
/// </summary>
public partial class IterationPagination : IDisposable
{
    /// <summary>
    /// Service for expanded job view state and operations.
    /// </summary>
    [Inject]
    private ExpandedJobViewService Service { get; set; }
    
    /// <summary>
    /// Session service for window and panel information.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }

    /// <summary>
    /// The width in pixels allocated for each pagination item (including spacing).
    /// </summary>
    private const int ItemWidth = 36;
    
    /// <summary>
    /// The minimum number of page items to display, regardless of available width.
    /// Should be an odd number to ensure symmetry around the current iteration.
    /// </summary>
    private const int MinVisibleItems = 7;
    
    /// <summary>
    /// Initializes the component and subscribes to relevant events.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        Service.OnJobChanged += HandleJobChanged;
        Service.OnJobUpdated += HandleJobUpdated;
        Service.OnIterationChanged += HandleIterationChanged;
        Session.OnWindowResized += HandleWindowResized;
    }

    /// <summary>
    /// Calculates the maximum number of page items that can be displayed based on the current window width.
    /// Ensures at least MinVisibleItems are shown.
    /// </summary>
    /// <returns>The maximum number of iteration items that can be displayed</returns>
    private int GetMaxVisibleItems()
    {
        // Calculate max items based on available width
        int availableWidth = Session.GetCenterPanelWidth();
        int maxItems = Math.Max(MinVisibleItems, (availableWidth - 40) / ItemWidth);
        
        return maxItems;
    }
    
    /// <summary>
    /// Handles job change events by triggering a UI refresh.
    /// </summary>
    /// <param name="job">The newly selected job</param>
    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Handles job update events by triggering a UI refresh.
    /// </summary>
    private async Task HandleJobUpdated()
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Handles iteration change events by triggering a UI refresh.
    /// </summary>
    /// <param name="iteration">The newly selected iteration</param>
    private async Task HandleIterationChanged(int iteration)
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Handles window resize events by triggering a UI refresh.
    /// This allows the pagination to respond to available space changes.
    /// </summary>
    private async Task HandleWindowResized()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Calculates which iteration numbers should be displayed in the pagination control.
    /// Applies the following rules:
    /// 1. If all iterations fit, show them all
    /// 2. Always show first and last iteration
    /// 3. Show the current iteration and surrounding ones
    /// 4. Use -1 as a marker for ellipsis (...)
    /// </summary>
    /// <returns>A list of iteration numbers to display, with -1 representing ellipsis points</returns>
    private List<int> GetVisibleIterations()
    {
        // Get a thread-safe snapshot of iterations
        var allIterations = Service.AvailableIterations;
        var maxVisibleItems = GetMaxVisibleItems();
        
        if (allIterations.Count <= maxVisibleItems)
            return allIterations.ToList();

        var result = new List<int>();
        int currentIteration = Service.CurrentIteration;
        
        // Find the current index safely in a collection that may be changing
        int currentIndex = -1;
        for (int i = 0; i < allIterations.Count; i++)
            if (allIterations[i] == currentIteration)
            {
                currentIndex = i;
                break;
            }

        // If current iteration wasn't found, default to last available
        if (currentIndex == -1)
            currentIndex = allIterations.Count - 1;

        // Always show first and last
        result.Add(allIterations[0]);
        
        // Calculate available slots for the middle section
        int middleSlots = maxVisibleItems - 3; // -3 for first, last, and one ellipsis
        
        if (currentIndex <= middleSlots / 2 + 1)
        {
            // Current is close to start - show more items at beginning
            for (int i = 1; i <= middleSlots; i++)
                result.Add(allIterations[i]);
            
            result.Add(-1); // Ellipsis
        }
        else if (currentIndex >= allIterations.Count - (middleSlots / 2 + 2))
        {
            // Current is close to end - show more items at end
            result.Add(-1); // Ellipsis
            
            for (int i = allIterations.Count - middleSlots - 1; i < allIterations.Count - 1; i++)
                result.Add(allIterations[i]);
        }
        else
        {
            // Current is in middle - distribute items evenly
            result.Add(-1); // First ellipsis
            
            int sideItems = (middleSlots - 1) / 2; // -1 for current item
            
            // Add items before current
            for (int i = currentIndex - sideItems; i < currentIndex; i++)
                result.Add(allIterations[i]);
                
            // Add current
            result.Add(allIterations[currentIndex]);
            
            // Add items after current
            for (int i = currentIndex + 1; i <= currentIndex + sideItems; i++)
                result.Add(allIterations[i]);
                
            result.Add(-1); // Second ellipsis
        }
        
        // Add last (only if we have more than one iteration)
        if (allIterations.Count > 1)
            result.Add(allIterations[^1]);
        
        return result;
    }

    /// <summary>
    /// Handles user clicking on an iteration number, updating the current iteration.
    /// </summary>
    /// <param name="iteration">The iteration number clicked</param>
    private async Task OnIterationClick(int iteration)
    {
        await Service.SetIterationAsync(iteration);
    }
    
    /// <summary>
    /// Cleans up event subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        Service.OnJobChanged -= HandleJobChanged;
        Service.OnJobUpdated -= HandleJobUpdated;
        Service.OnIterationChanged -= HandleIterationChanged;
        Session.OnWindowResized -= HandleWindowResized;
    }
}