using Microsoft.AspNetCore.Components;

namespace Relay.Pages;

/// <summary>
/// Main application page that serves as the container for all application screens.
/// Handles routing parameters to determine which content to display based on the current navigation state.
/// </summary>
public partial class Main : ComponentBase, IDisposable
{
    /// <summary>
    /// Gets or sets the project ID from the route parameter.
    /// Used to determine which project to load in the current view.
    /// </summary>
    [Parameter]
    public string ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the space ID from the route parameter.
    /// Used to determine which space to load within the selected project.
    /// </summary>
    [Parameter]
    public string SpaceId { get; set; }

    /// <summary>
    /// Gets or sets the view ID from the route parameter.
    /// Used to determine which view to display within the selected space.
    /// </summary>
    [Parameter]
    public string ViewId { get; set; }

    /// <summary>
    /// Gets or sets the folder ID from the route parameter.
    /// Used to determine which folder to display within the selected view.
    /// </summary>
    [Parameter]
    public int? FolderId { get; set; }

    /// <summary>
    /// Gets or sets the job ID from the route parameter.
    /// Used to determine which job to focus on within the selected view.
    /// </summary>
    [Parameter]
    public string JobId { get; set; }

    [Parameter]
    public string FactoryDefinitionId { get; set; }

    [Parameter]
    public int? FactoryInstanceId { get; set; }

    /// <summary>
    /// Initializes the component and subscribes to session change events.
    /// </summary>
    protected override void OnInitialized()
    {
        // Subscribe to session changes to refresh the UI when navigation state changes
        Session.OnMainChanged += HandleMainChanged;
    }
    
    /// <summary>
    /// Handles changes to the main session state.
    /// Ensures UI updates are performed within the Blazor synchronization context.
    /// </summary>
    private async Task HandleMainChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Performs cleanup by unsubscribing from session events when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        Session.OnMainChanged -= HandleMainChanged;
    }
}