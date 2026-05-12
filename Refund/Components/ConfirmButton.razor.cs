using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

/// <summary>
/// Position options for the confirmation dialog
/// </summary>
public enum ConfirmationPosition
{
    /// <summary>Display confirmation dialog to the left of the button</summary>
    Left,
    /// <summary>Display confirmation dialog to the right of the button</summary>
    Right,
    /// <summary>Display confirmation dialog above the button</summary>
    Top,
    /// <summary>Display confirmation dialog below the button</summary>
    Bottom
}

/// <summary>
/// A button that requires confirmation before executing its action.
/// This component displays a confirmation popup when clicked, requiring a second click to confirm.
/// Useful for preventing accidental actions, especially for destructive operations.
/// </summary>
public partial class ConfirmButton : FluentComponentBase
{
    /// <summary>
    /// Unique identifier for this button instance.
    /// Used to ensure proper DOM element targeting.
    /// </summary>
    private readonly string _id = "id-" + Guid.NewGuid().ToString()[..16];

    /// <summary>
    /// Tracks whether the confirmation popup is currently open.
    /// </summary>
    private bool _confirmationOpen = false;
    
    /// <summary>
    /// Content to display inside the button.
    /// </summary>
    [Parameter]
    public RenderFragment ChildContent { get; set; }
    
    /// <summary>
    /// Event callback that is invoked after the confirmation is clicked.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }
    
    /// <summary>
    /// Controls the position of the confirmation popup relative to the button.
    /// Can be Left, Right, Top, or Bottom.
    /// </summary>
    [Parameter]
    public ConfirmationPosition Position { get; set; } = ConfirmationPosition.Left;

    /// <summary>
    /// Gets the horizontal position for the FluentPopover based on the Position parameter.
    /// </summary>
    private HorizontalPosition GetHorizontalPosition() => Position switch
    {
        ConfirmationPosition.Left => HorizontalPosition.Left,
        ConfirmationPosition.Right => HorizontalPosition.Right,
        _ => HorizontalPosition.Center
    };

    /// <summary>
    /// Gets the vertical position for the FluentPopover based on the Position parameter.
    /// </summary>
    private VerticalPosition GetVerticalPosition() => Position switch
    {
        ConfirmationPosition.Top => VerticalPosition.Top,
        ConfirmationPosition.Bottom => VerticalPosition.Bottom,
        _ => VerticalPosition.Center
    };

    /// <summary>
    /// Determines if the arrow should be displayed on the left side of the confirmation.
    /// </summary>
    private bool ShowLeftArrow => Position == ConfirmationPosition.Right;

    /// <summary>
    /// Determines if the arrow should be displayed on the right side of the confirmation.
    /// </summary>
    private bool ShowRightArrow => Position == ConfirmationPosition.Left;

    /// <summary>
    /// Determines if the arrow should be displayed on the top of the confirmation.
    /// </summary>
    private bool ShowTopArrow => Position == ConfirmationPosition.Bottom;

    /// <summary>
    /// Determines if the arrow should be displayed on the bottom of the confirmation.
    /// </summary>
    private bool ShowBottomArrow => Position == ConfirmationPosition.Top;
    
    /// <summary>
    /// Gets the appropriate CSS class for the container based on the position.
    /// </summary>
    private string GetContainerClass() => Position switch
    {
        ConfirmationPosition.Left => "horizontal-layout",
        ConfirmationPosition.Right => "horizontal-layout",
        ConfirmationPosition.Top => "vertical-layout",
        ConfirmationPosition.Bottom => "vertical-layout",
        _ => "horizontal-layout"
    };

    /// <summary>
    /// Handles the confirmation click event.
    /// Closes the confirmation popup and invokes the OnClick callback.
    /// </summary>
    /// <param name="e">Mouse event arguments</param>
    private async Task HandleClick(MouseEventArgs e)
    {
        _confirmationOpen = false;
        await OnClick.InvokeAsync(e);
    }
}