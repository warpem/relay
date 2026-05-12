using Microsoft.AspNetCore.Components;
using Refund.Services;

namespace Relay.Panels.Right.Actions;

/// <summary>
/// Renders a collection of action buttons from a list of menu actions.
/// </summary>
/// <remarks>
/// This component displays multiple action buttons in a row, one for each menu action
/// in the provided collection. It serves as a container for grouping related actions
/// in the UI, typically shown in panels such as job properties, space properties, etc.
/// 
/// The component delegates the rendering of individual buttons to the <see cref="ActionButton"/>
/// component, ensuring consistent presentation and behavior across all action buttons.
/// </remarks>
public partial class ActionButtons : ComponentBase
{
    /// <summary>
    /// Gets or sets the collection of menu actions to display as buttons.
    /// </summary>
    /// <remarks>
    /// Each action in the collection will be rendered as a separate button.
    /// The appearance and behavior of each button is determined by the properties
    /// of the corresponding <see cref="MenuAction"/> object.
    /// </remarks>
    [Parameter]
    public IEnumerable<MenuAction> Actions { get; set; }
}