using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.Base;

/// <summary>
/// Generic base component for screens that display a list of items with cards.
/// Used for Project, Space, and View listing screens.
/// </summary>
/// <typeparam name="TItem">The type of item to display in the list.</typeparam>
public partial class ListingScreen<TItem> where TItem : class, IIdentifiable, IAnnotated, IAudited
{
    /// <summary>
    /// The title displayed at the top of the screen.
    /// </summary>
    [Parameter, EditorRequired]
    public string Title { get; set; }

    /// <summary>
    /// Text for the "Create" button that adds new items.
    /// </summary>
    [Parameter, EditorRequired]
    public string CreateButtonText { get; set; }
    
    /// <summary>
    /// Collection of items to display in the list.
    /// </summary>
    [Parameter, EditorRequired]
    public IEnumerable<TItem> Items { get; set; }
    
    /// <summary>
    /// Template for rendering each item in the list.
    /// </summary>
    [Parameter, EditorRequired]
    public RenderFragment<TItem> ItemTemplate { get; set; }
    
    /// <summary>
    /// Event callback triggered when the create button is clicked.
    /// </summary>
    [Parameter, EditorRequired]
    public EventCallback OnCreateClick { get; set; }
    
    /// <summary>
    /// Event callback triggered when the context menu is requested.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnContextMenu { get; set; }

    /// <summary>
    /// When true, items grid uses wrap-reverse (bottom-to-top). Default is true.
    /// When false, items grid uses normal wrap (top-to-bottom).
    /// </summary>
    [Parameter]
    public bool ReverseGrid { get; set; } = true;
}

/// <summary>
/// Base logic class for listing screens that provides common functionality 
/// such as item selection, navigation, and dialog management.
/// This is implemented by specific screens like ProjectScreen, SpaceScreen, and ViewScreen
/// to create a consistent hierarchy-based navigation pattern throughout the application.
/// </summary>
/// <typeparam name="TItem">The type of item to display in the list.</typeparam>
public abstract class ListingScreenLogic<TItem> : ComponentBase, IDisposable 
    where TItem : class, IIdentifiable, IAnnotated, IAudited
{
    /// <summary>
    /// The session service that maintains application state and navigation context.
    /// Used by derived screens to perform navigation between hierarchical entities.
    /// </summary>
    [Inject] protected RelaySession Session { get; set; }
    
    /// <summary>
    /// The data manager service for accessing and manipulating data.
    /// Derived screens use this to subscribe to entity-specific events and retrieve collections.
    /// </summary>
    [Inject] protected DataManager DataManager { get; set; }
    
    /// <summary>
    /// Service for tracking selected cards in the listing.
    /// Maintains selection state across components for consistent UX.
    /// </summary>
    [Inject] protected CardSelectionService Selection { get; set; }
    
    /// <summary>
    /// Service for displaying modal dialogs.
    /// Used to show create/edit dialogs for entities in derived screens.
    /// </summary>
    [Inject] protected IDialogService DialogService { get; set; }

    /// <summary>
    /// List of event subscriptions that need to be tracked for proper cleanup.
    /// Each derived screen adds specific event subscriptions to monitor relevant data changes.
    /// </summary>
    protected List<GroupEventSubscription> _subscriptions = new();
    
    /// <summary>
    /// Tracks the last selected item for range selection.
    /// </summary>
    protected SelectionKey? _lastSelectedKey;

    /// <summary>
    /// Gets the title for the listing screen.
    /// Implemented by derived screens to provide context-specific titles
    /// (e.g., "Projects", "Spaces in [Project]", "Jobs in [View]").
    /// </summary>
    /// <returns>The screen title.</returns>
    protected abstract string GetTitle();
    
    /// <summary>
    /// Gets the text for the create button.
    /// Implemented by derived screens to provide context-specific actions
    /// (e.g., "New Project", "New Space", "Add Job").
    /// </summary>
    /// <returns>The create button text.</returns>
    protected abstract string GetCreateButtonText();
    
    /// <summary>
    /// Gets the collection of items to display.
    /// Implemented by derived screens to fetch the appropriate collection
    /// from the current navigation context.
    /// </summary>
    /// <returns>The items collection.</returns>
    protected abstract IEnumerable<TItem> GetItems();
    
    /// <summary>
    /// Shows the dialog for creating a new item.
    /// Implemented by derived screens to display entity-specific creation dialogs,
    /// such as CreateProjectDialog, CreateSpaceDialog, or JobTypeMenu.
    /// </summary>
    protected abstract Task ShowCreateDialogAsync();
    
    /// <summary>
    /// Handles the result of the create dialog after it's closed.
    /// Implemented by derived screens to process dialog results and perform
    /// actions like navigating to the newly created item.
    /// </summary>
    /// <param name="result">The dialog result containing entity creation information.</param>
    protected abstract Task OnCreateDialogClosedAsync(DialogResult result);
    
    /// <summary>
    /// Navigates to the detail view of an item.
    /// Implemented by derived screens to navigate to the appropriate view
    /// for a specific entity (e.g., navigating from Project to Space).
    /// </summary>
    /// <param name="item">The item to navigate to.</param>
    protected abstract Task NavigateToItemAsync(TItem item);

    /// <summary>
    /// Returns the selection key for an item. Override in screens with mixed item types (e.g., ViewScreen).
    /// </summary>
    protected abstract SelectionKey GetSelectionKey(TItem item);

    /// <summary>
    /// Initializes the component and sets up event subscriptions.
    /// Called by SpaceScreen, ViewScreen, and ProjectScreen during their initialization.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        SubscribeToEvents();
    }

    /// <summary>
    /// Sets up event subscriptions for the component.
    /// Derived screens override this to add entity-specific subscriptions
    /// that react to data changes (creation, deletion, updates) for their entity type.
    /// </summary>
    protected virtual void SubscribeToEvents()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
    }

    // Track time of the last right-click to prevent HandleItemClicked from processing
    // clicks that happen within a short time window after a context menu was shown
    protected DateTime? _lastContextMenuTime = null;
    protected const int CONTEXT_MENU_CLICK_THRESHOLD_MS = 1000;

    /// <summary>
    /// Handles item click events with selection logic.
    /// Supports single selection, ctrl/cmd+click for multi-select, 
    /// and shift+click for range selection.
    /// 
    /// Used by all derived screens (ProjectScreen, SpaceScreen, ViewScreen) to
    /// provide consistent selection behavior across the application.
    /// </summary>
    /// <param name="item">The clicked item.</param>
    /// <param name="args">Mouse event arguments containing modifier key information.</param>
    protected virtual async Task HandleItemClicked(TItem item, MouseEventArgs args)
    {
        // Update last context menu time if this is a right-click (Button == 2)
        if (args.Button == 2 || args.Type == "contextmenu")
        {
            _lastContextMenuTime = DateTime.Now;
            return;
        }
        
        // Only process left-click (Button == 0)
        if (args.Button != 0)
            return;
        
        // Skip processing if this click happens shortly after a context menu was shown
        // This prevents delayed clicks from context menu interactions being processed
        if (_lastContextMenuTime != null && 
            (DateTime.Now - _lastContextMenuTime.Value).TotalMilliseconds > CONTEXT_MENU_CLICK_THRESHOLD_MS)
        {
            _lastContextMenuTime = null;
            return;
        }
        
        var key = GetSelectionKey(item);

        if (MouseUtils.ModifierSelectSingle(args, Session.ClientOs))
        {
            if (!Selection.IsSelected(key))
                await Selection.AddRange([key]);
            else
                await Selection.RemoveRange([key]);

            _lastSelectedKey = key;
        }
        else if (MouseUtils.ModifierSelectRange(args, Session.ClientOs))
        {
            if (_lastSelectedKey.HasValue)
            {
                var allItems = GetItems().ToList();
                int startIndex = allItems.FindIndex(i => GetSelectionKey(i).Equals(_lastSelectedKey.Value));
                int endIndex = allItems.FindIndex(i => GetSelectionKey(i).Equals(key));

                if (startIndex >= 0 && endIndex >= 0)
                {
                    int min = Math.Min(startIndex, endIndex);
                    int max = Math.Max(startIndex, endIndex);
                    await Selection.Replace(allItems.Skip(min).Take(max - min + 1).Select(GetSelectionKey));
                }
            }
        }
        else
        {
            await Selection.Replace([key]);
            _lastSelectedKey = key;
        }
    }

    /// <summary>
    /// Performs cleanup by unsubscribing from events.
    /// Essential for preventing memory leaks in components that subscribe to global events.
    /// Called when screens are navigated away from or removed from the component tree.
    /// </summary>
    public virtual void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
    }
}