using Microsoft.AspNetCore.Components;

namespace Refund.Components;

/// <summary>
/// Subscription class for dynamic tooltips that enables external control of tooltip visibility.
/// Provides callbacks that can be invoked to open or close a tooltip programmatically.
/// 
/// Used primarily in component hierarchies where a parent component needs to control tooltip
/// display without direct references to the tooltip component itself. Commonly used in job-related
/// components like JobLine to implement preview hover functionality.
/// </summary>
public class TooltipSubscription : IDisposable
{
    /// <summary>
    /// Callback function that is invoked to open the tooltip.
    /// Set by the DynamicTooltipContent component when initialized.
    /// 
    /// Usage example in JobLine.razor.cs:
    /// <code>
    /// private async Task HandleMouseEnter()
    /// {
    ///     if (ShowPreview && _tooltipSubscription.OpenCallback != null)
    ///         await _tooltipSubscription.OpenCallback();
    /// }
    /// </code>
    /// </summary>
    public Func<Task> OpenCallback { get; set; } = null;
    
    /// <summary>
    /// Callback function that is invoked to close the tooltip.
    /// Set by the DynamicTooltipContent component when initialized.
    ///
    /// Usage example in JobLine.razor.cs:
    /// <code>
    /// private async Task HandleMouseLeave()
    /// {
    ///     if (ShowPreview && _tooltipSubscription.CloseCallback != null)
    ///         await _tooltipSubscription.CloseCallback();
    /// }
    /// </code>
    /// </summary>
    public Func<Task> CloseCallback { get; set; } = null;
    
    /// <summary>
    /// Cleans up the subscription by clearing callback references.
    /// Prevents memory leaks by removing references to callback functions.
    /// </summary>
    public void Dispose()
    {
        OpenCallback = null;
        CloseCallback = null;
    }
}

/// <summary>
/// Component that provides dynamic tooltip content that can be shown or hidden programmatically.
/// Useful for tooltips that need to be controlled by application logic rather than just hover events.
///
/// This component is commonly used in job visualization contexts, such as in JobLine components,
/// where tooltips need to be activated by mouse enter/leave events but controlled programmatically.
/// The component uses a subscription pattern to allow parent components to control its visibility.
/// </summary>
public partial class DynamicTooltipContent : ComponentBase, IDisposable
{
    /// <summary>
    /// Whether the tooltip is currently visible.
    /// Toggled by the HandleTooltipOpened and HandleTooltipClosed methods which are linked
    /// to the Subscription callbacks.
    /// </summary>
    private bool _isVisible;

    /// <summary>
    /// Subscription object that allows external code to control this tooltip.
    /// Required parameter that establishes the communication channel between this tooltip
    /// and the parent component controlling its visibility.
    /// 
    /// Typically instantiated in the parent component:
    /// <code>
    /// private readonly TooltipSubscription _tooltipSubscription = new();
    /// </code>
    /// </summary>
    [Parameter, EditorRequired]
    public TooltipSubscription Subscription { get; set; } = null!;

    /// <summary>
    /// CSS class to apply to the tooltip container.
    /// </summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>
    /// Inline CSS styles to apply to the tooltip container.
    /// </summary>
    [Parameter]
    public string? Style { get; set; }

    /// <summary>
    /// Content to display inside the tooltip.
    /// </summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Additional attributes to pass to the tooltip container element.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Initializes the component by setting up the subscription callbacks.
    /// Connects this component to the provided subscription object.
    /// 
    /// This wires up the component's internal methods to the subscription callbacks,
    /// allowing external components to trigger tooltip visibility changes.
    /// </summary>
    protected override void OnInitialized()
    {
        Subscription.OpenCallback = HandleTooltipOpened;
        Subscription.CloseCallback = HandleTooltipClosed;
    }

    /// <summary>
    /// Handles showing the tooltip when triggered externally.
    /// Updates the visibility state and triggers a UI refresh.
    /// 
    /// Invoked when a parent component calls the OpenCallback on the subscription.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleTooltipOpened()
    {
        _isVisible = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles hiding the tooltip when triggered externally.
    /// Includes a small delay to ensure smooth visual transitions.
    /// 
    /// The 500ms delay prevents flickering when transitioning between tooltip states,
    /// especially important when users are moving between closely spaced UI elements.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    private async Task HandleTooltipClosed()
    {
        // Wait a little so the tooltip change isn't visible while it disappears
        await Task.Delay(500);
        
        _isVisible = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Cleans up resources when the component is disposed.
    /// Ensures the subscription is properly disposed to prevent memory leaks.
    /// This is crucial to prevent dangling references that could lead to unexpected behaviors
    /// or memory leaks in the application.
    /// </summary>
    public void Dispose()
    {
        Subscription.Dispose();
    }
}