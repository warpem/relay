using Microsoft.AspNetCore.Components;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Shared;

/// <summary>
/// Main layout component that structures the entire application UI.
/// Manages the top, left, center, and right panels based on authentication state and user preferences.
/// </summary>
public partial class MainLayout
{
    /// <summary>
    /// Flag indicating whether the component has completed initialization.
    /// </summary>
    private bool _initialized = false;
    
    /// <summary>
    /// CSS class that changes based on authentication state.
    /// </summary>
    private string _layoutClass => Session.IsAuthenticated ? "authenticated" : "unauthenticated";
    
    /// <summary>
    /// Generates the CSS grid template for the main container.
    /// Adjusts column and row dimensions based on panel sizes and visibility state.
    /// </summary>
    private string _containerStyle => Session.IsAuthenticated ?
                                          $"grid-template-columns: {RelaySession.LeftPanelWidth}px 1fr {(Session.IsRightPanelExpanded() ? $"{RelaySession.DividerWidth}px {RelaySession.RightPanelWidth}px" : "0")};" +
                                          $"grid-template-rows: {RelaySession.TopPanelHeight}px 1fr;" :
                                          "";
        
    /// <summary>
    /// CSS style for the left panel with fixed width.
    /// </summary>
    private string _leftPanelStyle => $"width: {RelaySession.LeftPanelWidth}px";
    
    /// <summary>
    /// CSS style for the top panel with fixed height.
    /// </summary>
    private string _topPanelStyle => $"height: {RelaySession.TopPanelHeight}px";
    
    /// <summary>
    /// CSS style for the center panel, adapting to right panel visibility.
    /// Adjusts height, border radius, and border style based on whether the right panel is expanded.
    /// </summary>
    private string _centerPanelStyle => $"height: {Session.GetCenterPanelHeight()}px; " +
                                        $"border-top-right-radius: {(Session.IsRightPanelExpanded() ? "6px" : "0px")}; " +
                                        $"border-right: {(Session.IsRightPanelExpanded() ? "1px" : "0px")} solid var(--neutral-stroke-rest);";

    private string _folderPath
    {
        get
        {
            if (Session.Folder == null) return null;
            var parts = new List<string>();
            var f = Session.Folder;
            while (f != null)
            {
                parts.Add(f.Alias);
                f = f.Parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }

    /// <summary>
    /// CSS style for the right panel with width, height, and margin adjustments.
    /// </summary>
    private string _rightPanelStyle => $"width: {RelaySession.RightPanelWidth}px; " +
                                       $"height: {Session.GetCenterPanelHeight()}px; " +
                                       $"margin-left: {RelaySession.DividerWidth}px;";


    
    /// <summary>
    /// Initializes the component after the first render.
    /// Sets up authentication state, session initialization, and event subscriptions.
    /// </summary>
    /// <param name="firstRender">Whether this is the first time the component has been rendered.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Initialize the session
            await Session.InitializeAsync();

            // Check authentication state from HTTP context
            var isAuthenticated = HttpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
            if (isAuthenticated)
            {
                // Find the user based on the authenticated username
                var username = HttpContextAccessor.HttpContext.User.Identity.Name;
                var user = DataManager.FindUser(username);
                if (user != null)
                    await Session.SetAuthenticated(true, user);
            }
            else
            {
                // Redirect to login if not authenticated
                NavigationManager.NavigateTo("/login");
            }

            // Subscribe to session events
            Session.OnThemeChanged += HandleThemeChanged;
            Session.OnStateChanged += HandleStateChanged;
            Session.OnWindowResized += HandleWindowResized;
            Session.OnRightPanelCollapsedChanged += HandleRightPanelCollapsedChanged;

            _initialized = true;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Handles theme change events by refreshing the UI.
    /// </summary>
    private async Task HandleThemeChanged()
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Handles session state change events by refreshing the UI.
    /// </summary>
    private async Task HandleStateChanged()
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Handles window resize events by refreshing the UI.
    /// </summary>
    private async Task HandleWindowResized()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleRightPanelCollapsedChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Performs cleanup by unsubscribing from session events.
    /// </summary>
    public void Dispose()
    {
        Session.OnThemeChanged -= HandleThemeChanged;
        Session.OnStateChanged -= HandleStateChanged;
        Session.OnWindowResized -= HandleWindowResized;
        Session.OnRightPanelCollapsedChanged -= HandleRightPanelCollapsedChanged;
    }
}