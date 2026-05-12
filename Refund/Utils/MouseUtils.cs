using Microsoft.AspNetCore.Components.Web;
using Refund.Services.Core.Session;

namespace Refund.Utils;

/// <summary>
/// Provides utilities for handling mouse interactions in a platform-aware manner.
/// </summary>
/// <remarks>
/// These utilities help create a consistent user experience across different operating systems
/// by adapting to platform-specific conventions for modifier keys and selection behaviors.
/// 
/// Used primarily in ListingScreen implementations to handle multi-selection patterns in item lists,
/// enabling both single-item toggle selection and range selection behaviors that match platform
/// conventions (Ctrl/Cmd for toggle selection, Shift for range selection).
/// </remarks>
public static class MouseUtils
{
    /// <summary>
    /// Determines whether the appropriate modifier key for single-item selection is pressed.
    /// </summary>
    /// <param name="args">The mouse event arguments containing key state information.</param>
    /// <param name="os">The client operating system to determine the appropriate modifier key.</param>
    /// <returns>
    /// True if the platform-appropriate modifier key for single selection is pressed; otherwise, false.
    /// </returns>
    /// <remarks>
    /// Accounts for platform differences in modifier keys:
    /// - On macOS, uses Command key (MetaKey)
    /// - On Windows/Linux, uses Control key (CtrlKey)
    /// 
    /// This method is used in ListingScreen.HandleItemClicked to implement the toggle selection 
    /// behavior, where Ctrl/Cmd+Click toggles an item's selection state without affecting other 
    /// selected items. This enables users to build a non-contiguous selection of items by 
    /// individually toggling them.
    /// </remarks>
    public static bool ModifierSelectSingle(MouseEventArgs args, ClientOs os)
    {
        if (os == ClientOs.Mac)
            return args.MetaKey;
        else
            return args.CtrlKey;
    }
    
    /// <summary>
    /// Determines whether the modifier key for range selection is pressed.
    /// </summary>
    /// <param name="args">The mouse event arguments containing key state information.</param>
    /// <param name="os">The client operating system (not used in this implementation as Shift is standard across platforms).</param>
    /// <returns>True if the Shift key is pressed; otherwise, false.</returns>
    /// <remarks>
    /// Used to implement the standard behavior where holding Shift while clicking 
    /// selects all items between the previously selected item and the currently clicked item.
    /// 
    /// Unlike single selection, range selection uses the same modifier key (Shift) 
    /// across all platforms.
    /// 
    /// In ListingScreen.HandleItemClicked, this method enables range selection when a user 
    /// Shift+Clicks an item, selecting all items between the previously selected item and 
    /// the currently clicked one. The implementation tracks the last selected item (_lastSelectedId) 
    /// to determine the range boundaries.
    /// </remarks>
    public static bool ModifierSelectRange(MouseEventArgs args, ClientOs os)
    {
        return args.ShiftKey;
    }

    /// <summary>
    /// Determines whether the click should open the target in a new browser tab.
    /// Returns true for middle-click (button 1) or when Ctrl (Windows/Linux) / Cmd (macOS) is held.
    /// </summary>
    public static bool IsNewTabClick(MouseEventArgs args)
        => args.Button == 1 || args.CtrlKey || args.MetaKey;
}