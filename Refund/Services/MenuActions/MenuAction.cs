using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Services;

/// <summary>
/// Represents a menu action that can be performed on an object.
/// </summary>
/// <remarks>
/// This class defines both the visual appearance of a menu action and the operation
/// it performs when triggered. It supports hierarchical menus through SubActions.
///
/// Menu actions can be disabled with an explanation of why they're not available,
/// and can require confirmation before executing to prevent accidental operations.
/// </remarks>
public class MenuAction
{
    /// <summary>
    /// Gets or sets the display name of the action.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this action requires confirmation before executing.
    /// </summary>
    public bool NeedsConfirmation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this action is disabled.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Gets or sets a message explaining why the action is disabled (if it is).
    /// </summary>
    public string DisabledBecause { get; set; }

    /// <summary>
    /// Gets or sets the visual appearance of the action button.
    /// </summary>
    public Appearance? Appearance { get; set; }

    /// <summary>
    /// Gets or sets the text color of the action button (CSS color value).
    /// </summary>
    public string? TextColor { get; set; }

    /// <summary>
    /// Gets or sets the background color of the action button (CSS color value).
    /// </summary>
    public string? BackgroundColor { get; set; }

    /// <summary>
    /// Gets or sets the border color of the action button (CSS color value).
    /// </summary>
    public string? BorderColor { get; set; }

    /// <summary>
    /// Gets or sets the small icon to display for the action.
    /// </summary>
    public Icon IconSmall { get; set; }

    /// <summary>
    /// Gets or sets the large icon to display for the action.
    /// </summary>
    public Icon IconLarge { get; set; }

    /// <summary>
    /// Gets or sets the asynchronous operation to perform when the action is triggered.
    /// </summary>
    public Func<Task> Action { get; set; }

    /// <summary>
    /// Gets the list of sub-actions that can be selected from a dropdown menu.
    /// </summary>
    public List<MenuAction> SubActions { get; private set; } = new();
}
