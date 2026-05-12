using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.FileBrowser;
using Refund.Configuration.Constants;
using Refund.Configuration;
using Refund.Services.Core.Session;
using Relay.Services;

namespace Relay.Panels.Left;

/// <summary>
/// Left sidebar component that provides main navigation and global actions for the application.
/// Contains buttons for accessing settings, job queues, and user account actions like logout.
/// </summary>
public partial class LeftBar : ComponentBase, IDisposable
{
    /// <summary>
    /// Flag indicating whether the sign out confirmation dialog is currently displayed.
    /// </summary>
    private bool _isSignOutOpen = false;

    /// <summary>
    /// The last folder visited in the file browser, persisted across sessions.
    /// </summary>
    private string _lastBrowsedFolder;

    private const string LastBrowsedFolderKey = "fileBrowser.lastFolder";

    /// <summary>
    /// Authentication configuration that determines the authentication type 
    /// (Native or SSO) used by the application.
    /// </summary>
    [Inject]
    private AuthenticationConfiguration AuthenticationConfiguration { get; set; }

    /// <summary>
    /// HTTP client for making requests to the server.
    /// </summary>
    [Inject]
    public HttpClient HttpClient { get; set; }

    /// <summary>
    /// Navigation manager for handling client-side navigation.
    /// </summary>
    [Inject]
    public NavigationManager Navigation { get; set; }
    
    /// <summary>
    /// Session service for tracking user state and navigation context.
    /// </summary>
    [Inject]
    public RelaySession Session { get; set; }

    /// <summary>
    /// Dialog service for showing modal dialogs.
    /// </summary>
    [Inject]
    private IDialogService DialogService { get; set; }

    /// <summary>
    /// Local storage service for persisting user preferences.
    /// </summary>
    [Inject]
    private ILocalStorageService LocalStorage { get; set; }
    
    /// <summary>
    /// Initializes the component and sets up event handlers.
    /// Subscribes to session state changes and loads persisted state.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        Session.OnStateChanged += HandleStateChanged;
        _lastBrowsedFolder = await LocalStorage.GetItemAsStringAsync(LastBrowsedFolderKey);
    }

    /// <summary>
    /// Handles changes to the session state by triggering a UI refresh.
    /// </summary>
    private async Task HandleStateChanged()
    {
        await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Opens the Queues overlay screen when the queues button is clicked.
    /// Maintains the current navigation context while displaying the overlay.
    /// </summary>
    private async Task OnQueuesButtonClick()
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = Session.ViewId,
            JobId = Session.JobId,
            Overlay = OverlayScreenType.Queues
        });
    }
    
    /// <summary>
    /// Opens the Settings overlay screen when the settings button is clicked.
    /// Maintains the current navigation context while displaying the overlay.
    /// </summary>
    private async Task OnSettingsButtonClick()
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = Session.ViewId,
            JobId = Session.JobId,
            Overlay = OverlayScreenType.Settings
        });
    }

    /// <summary>
    /// Opens the file browser dialog in browse-only mode.
    /// Restores the last visited folder and tracks navigation.
    /// </summary>
    private async Task OnBrowseFilesClick()
    {
        await FileBrowserDialog.Show(
            DialogService,
            this,
            HandleFileBrowserDialog,
            "Browse Files",
            currentFolder: _lastBrowsedFolder,
            showSelectionButtons: false,
            onCurrentFolderChanged: async folder =>
            {
                _lastBrowsedFolder = folder;
                await LocalStorage.SetItemAsStringAsync(LastBrowsedFolderKey, folder);
            });
    }

    /// <summary>
    /// Handles the file browser dialog result. No action needed for browse-only mode.
    /// </summary>
    private Task HandleFileBrowserDialog(DialogResult result)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs cleanup by unsubscribing from session state change events.
    /// </summary>
    public void Dispose()
    {
        Session.OnStateChanged -= HandleStateChanged;
    }

    /// <summary>
    /// Logs the user out of the application.
    /// Handles both native authentication and SSO authentication types by
    /// redirecting to the appropriate logout endpoint.
    /// </summary>
    private async Task Logout()
    {
        if (AuthenticationConfiguration.AuthenticationType == AuthenticationConfigurationConstants.AuthenticationTypeNative)
        {
            Navigation.NavigateTo("/process-logout", forceLoad: true);
        }
        if (AuthenticationConfiguration.AuthenticationType == AuthenticationConfigurationConstants.AuthenticationTypeSSO)
        {
            Navigation.NavigateTo("/process-logout", forceLoad: true);
        }
    }
}