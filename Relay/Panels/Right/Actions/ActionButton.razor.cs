using Microsoft.AspNetCore.Components;
using Refund.Services;

namespace Relay.Panels.Right.Actions;

/// <summary>
/// Renders a single action button with optional confirmation dialog support.
/// </summary>
/// <remarks>
/// This component displays a menu action as a button that triggers the action's operation
/// when clicked. If the action requires confirmation, it shows a confirmation dialog before
/// executing the action.
/// 
/// The component automatically generates a unique ID for each button to ensure proper
/// dialog targeting when multiple action buttons are displayed.
/// 
/// Used primarily by the ActionButtons component to render individual action buttons in the UI.
/// </remarks>
public partial class ActionButton : ComponentBase
{
    /// <summary>
    /// Gets or sets the menu action to display as a button.
    /// </summary>
    /// <remarks>
    /// The action defines the appearance, enabled state, and operation to perform
    /// when the button is clicked.
    /// </remarks>
    [Parameter]
    public MenuAction Action { get; set; }

    /// <summary>
    /// A unique identifier for this button instance.
    /// </summary>
    /// <remarks>
    /// Used to associate confirmation dialogs with specific buttons when
    /// multiple action buttons are present.
    /// </remarks>
    private readonly string _id = "id-" + Guid.NewGuid().ToString()[..16];
    
    /// <summary>
    /// Tracks whether the confirmation dialog is currently open.
    /// </summary>
    private bool _confirmationOpen = false;
    private bool _menuOpen = false;

    /// <summary>
    /// Handles the primary click on the action button.
    /// </summary>
    /// <remarks>
    /// If the action requires confirmation, this opens the confirmation dialog.
    /// Otherwise, it immediately executes the action's operation.
    /// </remarks>
    private async Task HandlePrimaryClick()
    {
        if (Action.NeedsConfirmation)
            _confirmationOpen = true;
        else
            await Action.Action();
    }
}