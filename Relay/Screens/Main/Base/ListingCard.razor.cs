using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.Base;

/// <summary>
/// Generic card component used to display items in listing screens.
/// Provides a consistent card UI with header, details section, job list, and context menu.
/// This is the presentation component that pairs with ListingCardLogic.
/// </summary>
/// <typeparam name="TItem">The type of item to display in the card.</typeparam>
public partial class ListingCard<TItem> : ComponentBase where TItem : class, IIdentifiable, IAnnotated, IAudited, IJobContainer
{
    /// <summary>
    /// The data item represented by this card.
    /// </summary>
    [Parameter, EditorRequired]
    public TItem Item { get; set; }
    
    /// <summary>
    /// Whether this card is currently selected.
    /// </summary>
    [Parameter]
    public bool IsSelected { get; set; }
    
    /// <summary>
    /// Event callback for when the card body is clicked.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }
    
    /// <summary>
    /// Event callback for when the card header is clicked for navigation.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnNavigate { get; set; }
    
    /// <summary>
    /// Content to display in the card header.
    /// </summary>
    [Parameter]
    public RenderFragment HeaderContent { get; set; }
    
    /// <summary>
    /// Additional details to display in the card body.
    /// </summary>
    [Parameter]
    public RenderFragment ExtraDetails { get; set; }
    
    /// <summary>
    /// Custom content for displaying jobs associated with this item.
    /// </summary>
    [Parameter]
    public RenderFragment JobList { get; set; }
    
    /// <summary>
    /// Function to generate context menu actions for this card.
    /// </summary>
    [Parameter]
    public Func<Task<List<MenuAction>>> GetContextMenuActions { get; set; }

    /// <summary>
    /// URL for the card header link. When set, the header renders as an anchor tag.
    /// </summary>
    [Parameter]
    public string NavigationUrl { get; set; }

    /// <summary>
    /// List of jobs associated with this item.
    /// </summary>
    public IReadOnlyList<ReadOnlyJob> Jobs => Item.Jobs;

    /// <summary>
    /// Unique ID for the card header element.
    /// </summary>
    private string _id => $"item-heading-{Item.Id}";
    
    /// <summary>
    /// Current context menu actions for this card.
    /// </summary>
    private List<MenuAction> _contextMenuActions;
    
    /// <summary>
    /// Handles clicks on the card header, triggering navigation.
    /// For modifier/middle clicks, bails so the <a> tag handles them natively.
    /// </summary>
    private async Task OnHeaderClick(MouseEventArgs args)
    {
        if (MouseUtils.IsNewTabClick(args))
            return;
        await OnNavigate.InvokeAsync(args);
    }
    
    /// <summary>
    /// Handles clicks on the card body, triggering selection.
    /// </summary>
    private async Task OnCardClick(MouseEventArgs args)
    {
        await OnClick.InvokeAsync(args);
    }

    /// <summary>
    /// Handles context menu open/close state.
    /// Loads menu actions when the menu is opened.
    /// </summary>
    /// <param name="value">Whether the context menu is open.</param>
    private async Task HandleContextMenu(bool value)
    {
        if (GetContextMenuActions == null)
            return;

        if (value)
        {
            _contextMenuActions = await GetContextMenuActions();
            
            // Notify all ListingScreen instances about the context menu being opened
            // This helps prevent spurious HandleItemClicked events from being processed
            if (OnClick.HasDelegate)
            {
                // Generate a dummy mouse event to update the _lastContextMenuTime in ListingScreenLogic
                var dummyEvent = new MouseEventArgs
                {
                    Button = 2, // Right mouse button
                    ClientX = 0,
                    ClientY = 0,
                    Type = "contextmenu",
                };
                await OnClick.InvokeAsync(dummyEvent);
            }
        }
        else
            _contextMenuActions = null;
    }
}

/// <summary>
/// Base logic class for card components used in listing screens.
/// Provides common functionality for event handling and state management.
/// 
/// This class is extended by typed card components like ProjectCard, SpaceCard,
/// and ViewCard, which implement the navigation and event subscription logic for
/// their specific entity types. The concrete implementations typically use
/// the Item property to access the current entity and navigate to the appropriate view.
/// </summary>
/// <typeparam name="TItem">The type of item displayed by the card.</typeparam>
public abstract class ListingCardLogic<TItem> : ComponentBase, IDisposable 
    where TItem : class, IIdentifiable, IAnnotated, IAudited, IJobContainer
{
    /// <summary>
    /// The data item represented by this card.
    /// Used by derived classes to access entity properties for navigation and event handling.
    /// For example, SpaceCard uses Item.Project.Id and Item.Id to construct navigation requests.
    /// </summary>
    [Parameter, EditorRequired]
    public TItem Item { get; set; }
    
    /// <summary>
    /// Cached reference to the current item for change detection.
    /// </summary>
    private TItem _item;
    
    /// <summary>
    /// Whether this card is currently selected.
    /// </summary>
    [Parameter]
    public bool IsSelected { get; set; }
    
    /// <summary>
    /// Event callback for when the card is clicked.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }
    
    /// <summary>
    /// Content to display in the card header.
    /// </summary>
    [Parameter]
    public RenderFragment HeaderContent { get; set; }
    
    /// <summary>
    /// Additional details to display in the card body.
    /// </summary>
    [Parameter]
    public RenderFragment ExtraDetails { get; set; }
    
    /// <summary>
    /// Custom content for displaying jobs associated with this item.
    /// </summary>
    [Parameter]
    public RenderFragment JobList { get; set; }

    /// <summary>
    /// Data manager service for accessing and manipulating data.
    /// Used by derived classes to subscribe to relevant entity events.
    /// </summary>
    [Inject] 
    protected DataManager DataManager { get; set; }
    
    /// <summary>
    /// Session service that maintains application state.
    /// Used by derived classes to handle navigation between different views.
    /// For example, SpaceCard uses Session.NavigateToAsync to navigate to the Space view.
    /// </summary>
    [Inject] 
    protected RelaySession Session { get; set; }

    /// <summary>
    /// List of event subscriptions that need to be tracked for proper cleanup.
    /// Derived classes add subscriptions here in their SubscribeToEvents implementation.
    /// </summary>
    protected readonly List<GroupEventSubscription> _subscriptions = new();

    /// <summary>
    /// URL for navigating to this item's detail view.
    /// Computed from GetNavigationUrl() when the item changes.
    /// </summary>
    protected string NavigationUrl { get; private set; }

    /// <summary>
    /// Computes the navigation URL for this card's item.
    /// Implemented by derived classes (ProjectCard, SpaceCard, ViewCard).
    /// </summary>
    protected abstract string GetNavigationUrl();

    /// <summary>
    /// Responds to parameter changes and manages event subscriptions.
    /// When the item changes, it clears old subscriptions and sets up new ones.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(Item, _item))
        {
            _item = Item;

            // Clear and reset subscriptions when the item changes
            foreach (var sub in _subscriptions)
                sub.Unsubscribe();
            _subscriptions.Clear();

            if (_item != null)
            {
                NavigationUrl = GetNavigationUrl();
                SubscribeToEvents();
            }
        }
    }

    /// <summary>
    /// Handles header click events for navigation to the item's detail view.
    /// Concrete implementations typically use Session.NavigateToAsync with
    /// appropriate parameters based on the Item's properties. For example:
    /// - ProjectCard navigates to the project's space listing
    /// - SpaceCard navigates to the space's view listing
    /// - ViewCard navigates to the view's job graph
    /// </summary>
    /// <param name="args">Mouse event arguments.</param>
    protected abstract Task HeaderClick(MouseEventArgs args);
    
    /// <summary>
    /// Sets up event subscriptions for the component based on the current Item.
    /// Concrete implementations typically subscribe to:
    /// - Entity update/delete events (e.g., DataManager.SpaceUpdated)
    /// - Child entity events (e.g., job creation/updates within a space)
    /// 
    /// These subscriptions trigger UI refreshes when the underlying data changes.
    /// </summary>
    protected abstract void SubscribeToEvents();
    
    /// <summary>
    /// Performs cleanup by unsubscribing from all tracked event subscriptions.
    /// This prevents memory leaks from lingering event handlers.
    /// </summary>
    public virtual void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
    }
}