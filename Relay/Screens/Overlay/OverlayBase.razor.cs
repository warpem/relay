using Microsoft.AspNetCore.Components;

namespace Relay.Screens.Overlay;

/// <summary>
/// Base component for all overlay screens in the application.
/// Provides common overlay functionality like modal presentation and close behavior.
/// </summary>
public partial class OverlayBase : ComponentBase
{
    /// <summary>
    /// The content to display within the overlay.
    /// </summary>
    [Parameter]
    public RenderFragment ChildContent { get; set; }
    
    /// <summary>
    /// Optional callback function to execute when the overlay is closed.
    /// If not provided, the default behavior will close the overlay via the session.
    /// </summary>
    [Parameter]
    public Func<Task> OnClose { get; set; }

    /// <summary>
    /// Handles the close button click event.
    /// Either executes the provided OnClose callback or uses the default session-based closing mechanism.
    /// </summary>
    private async Task OnCloseButtonClick()
    {
        if (OnClose != null)
            await OnClose();
        else
            await Session.CloseOverlayAsync();
    }
}