using Microsoft.AspNetCore.Components;
using Refund.Services;

namespace Relay.Panels.Right.Actions;

/// <summary>
/// Renders a single level of a hierarchical menu of actions.
/// </summary>
/// <remarks>
/// This component displays a collection of menu actions as a list of selectable items,
/// typically used in dropdown menus or context menus. It handles the styling of menu items
/// based on their action properties, such as text color and disabled state.
/// 
/// For actions with sub-actions, the component can be used recursively to build
/// multi-level hierarchical menus.
/// </remarks>
public partial class ActionMenuLevel : ComponentBase
{
    /// <summary>
    /// Gets or sets the collection of menu actions to display at this menu level.
    /// </summary>
    /// <remarks>
    /// Each action in the collection becomes a selectable item in the menu.
    /// Actions with sub-actions will render nested menus.
    /// </remarks>
    [Parameter]
    public IEnumerable<MenuAction> Actions { get; set; }

    /// <summary>
    /// Generates the CSS inline style for a menu item based on the action's appearance properties.
    /// </summary>
    /// <param name="action">The menu action to generate a style for</param>
    /// <returns>A CSS inline style string</returns>
    /// <remarks>
    /// This method combines the background color, text color, and disabled state of an action
    /// into a consistent style string. Disabled actions are shown with a line-through style
    /// to indicate they cannot be selected.
    /// </remarks>
    private string GetItemStyle(MenuAction action) => (action.BackgroundColor != null ? $"background-color: {action.BackgroundColor};" : "") +
                                                      (action.TextColor != null ? $" color: {action.TextColor}; " : "") +
                                                      (action.IsDisabled ? "text-decoration: line-through;" : "");
}