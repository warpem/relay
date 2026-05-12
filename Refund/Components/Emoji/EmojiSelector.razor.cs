using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Relay.Emoji;

namespace Refund.Components.Emoji;

/// <summary>
/// A virtualized emoji picker component that displays a scrollable grid of emojis with search functionality.
/// Implements UI virtualization to efficiently render only the visible emojis, improving performance with large emoji sets.
/// 
/// This component is primarily used by EmojiSelectionButton as a dropdown selector, which in turn is used extensively
/// throughout the application for assigning emojis to various entities like Projects, Spaces, and Views. It's also
/// integrated into the UiEmojiView field component for use in job properties and configuration forms.
/// </summary>
public partial class EmojiSelector : IAsyncDisposable
{
    /// <summary>
    /// Event callback that is triggered when the user selects an emoji.
    /// The selected EmojiInfo object is passed as the parameter.
    /// 
    /// In EmojiSelectionButton, this is used to handle the emoji selection and update the parent component:
    /// <code>
    /// private async Task HandleEmojiChanged(EmojiInfo emoji)
    /// {
    ///     _isEmojiSelectorOpen = false;
    ///     await GlyphChanged.InvokeAsync(emoji.Glyph);
    ///     
    ///     await InvokeAsync(StateHasChanged);
    /// }
    /// </code>
    /// </summary>
    [Parameter]
    public EventCallback<EmojiInfo> OnEmojiSelected { get; set; }

    /// <summary>
    /// Controls whether the emoji selector is currently visible.
    /// When set from outside, the selector will open or close accordingly.
    /// 
    /// This parameter works in conjunction with IsOpenChanged to implement two-way binding
    /// for controlling the selector's visibility state from parent components. Used by
    /// EmojiSelectionButton to toggle the dropdown visibility.
    /// </summary>
    [Parameter] 
    public bool? IsOpen { get; set; }

    /// <summary>
    /// Event callback that is triggered when the open state of the selector changes.
    /// Used to implement two-way binding with IsOpen.
    /// 
    /// Enables parent components like EmojiSelectionButton to be notified when the
    /// selector is opened or closed, allowing for synchronized state management.
    /// </summary>
    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }
    
    /// <summary>
    /// Reference to the JavaScript runtime for interop operations.
    /// Used to interact with browser APIs for scroll handling and measurements.
    /// </summary>
    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    /// <summary>
    /// Reference to the scrollable grid container DOM element.
    /// </summary>
    private ElementReference gridContainer;
    
    /// <summary>
    /// Reference to the imported JavaScript module.
    /// </summary>
    private IJSObjectReference _module;
    
    /// <summary>
    /// Flag indicating whether the JavaScript module has been loaded.
    /// </summary>
    private bool _moduleLoaded = false;
    
    /// <summary>
    /// .NET object reference used for JavaScript interop callbacks.
    /// </summary>
    private DotNetObjectReference<EmojiSelector> _dotNetHelper;

    /// <summary>
    /// Number of columns in the emoji grid.
    /// </summary>
    private const int GRID_COLUMNS = 7;
    
    /// <summary>
    /// Number of visible rows in the emoji grid viewport.
    /// </summary>
    private const int GRID_ROWS = 7;
    
    /// <summary>
    /// Number of additional rows to render above and below the visible viewport
    /// to provide smooth scrolling.
    /// </summary>
    private const int BUFFER_ROWS = 2;
    
    /// <summary>
    /// Size of each emoji item in pixels.
    /// </summary>
    private const int ITEM_SIZE = 40;
    
    /// <summary>
    /// Spacing between emoji items in pixels.
    /// </summary>
    private const int ITEM_SPACING = 2;
    
    /// <summary>
    /// Left offset for the grid to enable centering.
    /// </summary>
    private const int GRID_LEFT_OFFSET = 0; // Can be adjusted to center the grid

    /// <summary>
    /// Current search text entered by the user.
    /// </summary>
    private string _searchText = "";
    
    /// <summary>
    /// Complete list of all emojis available in the library.
    /// </summary>
    private List<EmojiInfo> _allEmoji;
    
    /// <summary>
    /// Filtered list of emojis based on the current search text.
    /// </summary>
    private List<EmojiInfo> _filteredEmoji => GetFilteredEmoji();
    
    /// <summary>
    /// List of emoji items that are currently visible in the viewport.
    /// </summary>
    private List<VisibleItem> VisibleItems = new();

    /// <summary>
    /// Represents an emoji item positioned in the virtual grid.
    /// Contains information about both the emoji and its visual position.
    /// </summary>
    private class VisibleItem
    {
        /// <summary>
        /// The emoji information.
        /// </summary>
        public EmojiInfo Emoji { get; set; }
        
        /// <summary>
        /// X-coordinate position in pixels.
        /// </summary>
        public int X { get; set; }
        
        /// <summary>
        /// Y-coordinate position in pixels.
        /// </summary>
        public int Y { get; set; }
    }

    /// <summary>
    /// Filters the emoji list based on the current search text.
    /// Returns all emojis if no search text is provided, otherwise returns
    /// only emojis that have keywords matching the search text.
    /// 
    /// This filtering approach enables users to quickly find specific emojis
    /// in the large emoji library by searching for descriptive keywords rather
    /// than having to browse through all categories. The filtering is case-insensitive
    /// and supports partial keyword matches.
    /// 
    /// For example, searching for "smile" would match emojis with keywords like
    /// "smile", "smiling", "smiling face", etc.
    /// </summary>
    /// <returns>Filtered list of emojis based on the current search text</returns>
    private List<EmojiInfo> GetFilteredEmoji()
    {
        if (string.IsNullOrWhiteSpace(_searchText))
            return _allEmoji;

        var searchText = _searchText.ToLower();
        var filtered = _allEmoji.Where(e => e.Keywords.Any(k => k.Contains(searchText))).ToList();
        
        return filtered;
    }

    /// <summary>
    /// Total number of emoji items after filtering.
    /// </summary>
    private int TotalItems => _filteredEmoji?.Count ?? 0;
    
    /// <summary>
    /// Total number of rows needed to display all emoji items.
    /// </summary>
    private int TotalRows => (int)Math.Ceiling((double)TotalItems / GRID_COLUMNS);
    
    /// <summary>
    /// Total scroll height in pixels required to display all rows.
    /// </summary>
    private int ScrollHeight => TotalRows * (ITEM_SIZE + ITEM_SPACING);
    
    /// <summary>
    /// Scroll height formatted as a CSS pixel value.
    /// </summary>
    private string ScrollHeightPx => $"{ScrollHeight}px";

    /// <summary>
    /// Initializes the component by loading the emoji library.
    /// </summary>
    protected override void OnInitialized()
    {
        // Create flat list of all emoji
        _allEmoji = EmojiLibrary.Groups.SelectMany(group => EmojiLibrary.GetByGroup(group).OrderBy(e => e.Unicode))
                                .ToList();
    }

    /// <summary>
    /// Sets up JavaScript interop after the component is rendered.
    /// Initializes scroll tracking and loads the initial set of visible emojis.
    /// </summary>
    /// <param name="firstRender">True if this is the first render of the component</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Load JS module
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/Emoji/EmojiSelector.razor.js");
            _moduleLoaded = true;

            _dotNetHelper = DotNetObjectReference.Create(this);

            // Initialize scroll tracking
            await _module.InvokeVoidAsync("initializeScroll", gridContainer, _dotNetHelper);
            
            await UpdateVisibleItems();
        }
    }

    /// <summary>
    /// Called from JavaScript when the user scrolls the emoji grid.
    /// Updates the list of visible emoji items based on the new scroll position.
    /// 
    /// This method implements the core virtualization logic that makes the EmojiSelector
    /// efficient even with thousands of emoji. It calculates which emoji items should be
    /// rendered based on the current scroll position and viewport size, positioning
    /// them absolutely within the scrollable container.
    /// 
    /// The virtualization approach used here is:
    /// 1. Calculate which rows are currently visible based on scroll position
    /// 2. Add buffer rows above and below for smooth scrolling
    /// 3. Calculate the actual emoji items that should be rendered
    /// 4. Position each emoji item with absolute positioning
    /// 5. Update the component state to re-render only the visible items
    /// </summary>
    /// <param name="scrollTop">Current scroll position in pixels</param>
    [JSInvokable]
    public async Task OnScrollUpdate(double scrollTop)
    {
        var filteredItems = _filteredEmoji;
        if (filteredItems == null) return;

        // Calculate visible range based on scroll position
        int firstVisibleRow = (int)Math.Floor(scrollTop / (ITEM_SIZE + ITEM_SPACING));
        firstVisibleRow = Math.Max(0, firstVisibleRow - BUFFER_ROWS);

        // Calculate how many rows we need
        int lastVisibleRow = firstVisibleRow + GRID_ROWS + (BUFFER_ROWS * 2);
        
        // Get items for these rows
        int startIndex = firstVisibleRow * GRID_COLUMNS;
        int endIndex = Math.Min((lastVisibleRow * GRID_COLUMNS), filteredItems.Count);
        int itemsToShow = endIndex - startIndex;

        // Create positioned items
        VisibleItems = new List<VisibleItem>();
        
        for (int i = 0; i < itemsToShow; i++)
        {
            int itemIndex = startIndex + i;
            if (itemIndex >= filteredItems.Count) break;

            int row = (itemIndex / GRID_COLUMNS); // Relative to viewport
            int col = itemIndex % GRID_COLUMNS;

            VisibleItems.Add(new VisibleItem
            {
                Emoji = filteredItems[itemIndex],
                X = GRID_LEFT_OFFSET + (col * (ITEM_SIZE + ITEM_SPACING)),
                Y = row * (ITEM_SIZE + ITEM_SPACING)
            });
        }

        StateHasChanged();
    }

    /// <summary>
    /// Updates the list of visible emoji items based on the current scroll position.
    /// Called when the component needs to refresh its view, such as after filtering.
    /// </summary>
    private async Task UpdateVisibleItems()
    {
        if (!_moduleLoaded) return;
        
        var scrollInfo = await _module.InvokeAsync<ScrollInfo>("getScrollInfo", gridContainer);
        await OnScrollUpdate(scrollInfo.ScrollTop);
    }
    
    /// <summary>
    /// Handles changes to the search text input.
    /// Updates the filtered emoji list and refreshes the visible items.
    /// </summary>
    /// <param name="searchText">New search text value</param>
    private async Task HandleSearchChanged(string searchText)
    {
        _searchText = searchText;
        await UpdateVisibleItems();
    }

    /// <summary>
    /// Handles emoji selection events.
    /// Invokes the OnEmojiSelected callback with the selected emoji.
    /// 
    /// This method is triggered when a user clicks on an emoji in the grid:
    /// <code>
    /// <button type="button"
    ///         class="emoji-button"
    ///         style="left: @(item.X)px; top: @(item.Y)px"
    ///         @onclick="() => OnSelect(item.Emoji)"
    ///         title="@item.Emoji.Name">
    ///     <FluentEmoji Value="@item.Emoji.FluentEmoji"/>
    /// </button>
    /// </code>
    /// 
    /// The selected emoji is then passed back to the parent component through
    /// the OnEmojiSelected callback (typically HandleEmojiChanged in EmojiSelectionButton),
    /// which extracts the emoji glyph and updates the parent component's state.
    /// </summary>
    /// <param name="emoji">The selected emoji information object</param>
    private Task OnSelect(EmojiInfo emoji)
    {
        return OnEmojiSelected.InvokeAsync(emoji);
    }

    /// <summary>
    /// Cleans up resources when the component is disposed.
    /// Disposes the JavaScript module and .NET object reference.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module != null)
                await _module.DisposeAsync();

            _dotNetHelper?.Dispose();
        }
        catch {}
    }

    /// <summary>
    /// Information about the scroll container's dimensions and position.
    /// Retrieved from JavaScript to help with virtualization calculations.
    /// </summary>
    private class ScrollInfo
    {
        /// <summary>
        /// Current scroll position from the top in pixels.
        /// </summary>
        public double ScrollTop { get; set; }
        
        /// <summary>
        /// Visible height of the scroll container in pixels.
        /// </summary>
        public double ClientHeight { get; set; }
        
        /// <summary>
        /// Total scrollable height of the container's content in pixels.
        /// </summary>
        public double ScrollHeight { get; set; }
    }
}