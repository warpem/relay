using Microsoft.AspNetCore.Components;
using Refund.Services.Core.Session;

namespace Relay.Panels.Top;

/// <summary>
/// Top application bar component that provides global application controls and information.
/// Includes theme switching functionality and hosts other top-level navigation elements.
/// </summary>
public partial class TopBar : ComponentBase, IDisposable
{
    /// <summary>
    /// Session service for tracking user state and theme preferences.
    /// </summary>
    [Inject]
    public RelaySession Session { get; set; }

    /// <summary>
    /// Initializes the component and subscribes to theme change events.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();

        Session.OnThemeChanged += HandleThemeChanged;
        Session.OnRightPanelCollapsedChanged += HandleRightPanelCollapsedChanged;
    }

    /// <summary>
    /// Event handler for theme changes that forces a UI refresh.
    /// </summary>
    private async Task HandleThemeChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Toggles between light and dark application themes.
    /// Connected to the theme toggle button in the UI.
    /// </summary>
    private async Task OnThemeButtonClick()
    {
        if (Session.ColorTheme == ColorTheme.Light)
            await Session.SetColorTheme(ColorTheme.Dark);
        else
            await Session.SetColorTheme(ColorTheme.Light);
    }

    private async Task OnToggleRightPanel()
    {
        await Session.SetRightPanelCollapsed(Session.IsRightPanelExpanded());
    }

    /// <summary>
    /// Fallback action of the breadcrumbs' error boundary. TopBar is outside that boundary, so this must not throw.
    /// </summary>
    private async Task GoHomeAsync()
    {
        try
        {
            await Session.NavigateToAsync(new());
        }
        catch (Exception exc)
        {
            Serilog.Log.ForContext<TopBar>().Error(exc, "Couldn't navigate home from the navigation fallback");
        }
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
        Session.OnRightPanelCollapsedChanged -= HandleRightPanelCollapsedChanged;
    }
}