using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Utils;
using Warp.Tools;

namespace Refund.Services.Core.Session;

/// <summary>
/// Central service that manages the application state, navigation, and UI context.
/// Provides an interface for accessing current selection, window dimensions, theme preferences,
/// and handles navigation between different parts of the application.
/// </summary>
/// <remarks>
/// RelaySession acts as the central state container for the Relay application, tracking:
/// - Current selection (project, space, view, job)
/// - Window dimensions and responsive layout calculations
/// - Theme preferences (light/dark mode)
/// - User authentication state
/// - Navigation context and URL management
/// 
/// It also handles interop with JavaScript for window resizing, theme detection, and mouse position tracking.
/// </remarks>
public class RelaySession : IAsyncDisposable
{
    private readonly NavigationManager _navigationManager;
    private readonly IJSRuntime _jsRuntime;
    private readonly IToastService _toastService;
    private readonly DataManager.DataManager _dataManager;
    private DotNetObjectReference<RelaySession> _objectReference;
    
    /// <summary>
    /// Gets the detected operating system of the client.
    /// </summary>
    public ClientOs ClientOs { get; private set; }

    /// <summary>
    /// Gets the current window width in pixels.
    /// </summary>
    public int WindowWidth { get; private set; }
    
    /// <summary>
    /// Gets the current window height in pixels.
    /// </summary>
    public int WindowHeight { get; private set; }
    
    /// <summary>
    /// The fixed width of the left panel in pixels.
    /// </summary>
    public const int LeftPanelWidth = 48;
    
    /// <summary>
    /// The fixed height of the top panel in pixels.
    /// </summary>
    public const int TopPanelHeight = 48;
    
    /// <summary>
    /// The fixed width of the right panel in pixels.
    /// </summary>
    public const int RightPanelWidth = 320;
    
    /// <summary>
    /// The width of the divider between panels in pixels.
    /// </summary>
    public const int DividerWidth = 8;

    #region State
    
    /// <summary>
    /// Gets the current color theme (light or dark mode).
    /// </summary>
    public ColorTheme ColorTheme { get; private set; } = ColorTheme.Light;

    private bool _isRightPanelCollapsed = false;

    /// <summary>
    /// Gets a value indicating whether the user is authenticated.
    /// </summary>
    public bool IsAuthenticated { get; private set; } = false;
    
    /// <summary>
    /// Gets the currently authenticated user, or null if not authenticated.
    /// </summary>
    public ReadOnlyUser User { get; private set; }
    
    /// <summary>
    /// Gets the ID of the currently selected project, or null if no project is selected.
    /// </summary>
    public int? ProjectId { get; private set; }
    
    /// <summary>
    /// Gets the ID of the currently selected space, or null if no space is selected.
    /// </summary>
    public int? SpaceId { get; private set; }
    
    /// <summary>
    /// Gets the ID of the currently selected view, or null if no view is selected.
    /// </summary>
    public int? ViewId { get; private set; }
    
    /// <summary>
    /// Gets the ID of the currently selected job, or null if no job is selected.
    /// </summary>
    public int? JobId { get; private set; }

    /// <summary>
    /// Gets the ID of the currently navigated folder, or null if at root level.
    /// </summary>
    public int? FolderId { get; private set; }

    /// <summary>
    /// Gets the ID of the currently selected factory definition, or null if none is selected.
    /// </summary>
    public int? FactoryDefinitionId { get; private set; }

    /// <summary>
    /// Gets the ID of the currently selected factory instance, or null if none is selected.
    /// </summary>
    public int? FactoryInstanceId { get; private set; }

    /// <summary>
    /// Gets the currently selected project object, or null if no project is selected.
    /// </summary>
    public ReadOnlyProject Project => ProjectId != null ? _dataManager.FindProject(ProjectId.Value) : null;

    /// <summary>
    /// Gets the currently selected space object, or null if no space is selected.
    /// </summary>
    public ReadOnlySpace Space => SpaceId != null ? Project?.FindSpace(SpaceId.Value) : null;

    /// <summary>
    /// Gets the currently selected view object, or null if no view is selected.
    /// </summary>
    public ReadOnlyView View => ViewId != null ? Space?.FindView(ViewId.Value) : null;

    /// <summary>
    /// Gets the currently selected job object, or null if no job is selected.
    /// </summary>
    public ReadOnlyJob Job => JobId != null ? View?.FindJob(JobId.Value) : null;

    /// <summary>
    /// Gets the currently navigated folder, or null if at root level.
    /// </summary>
    public ReadOnlyFolder Folder => FolderId.HasValue ? View?.FindFolder(FolderId.Value) : null;

    /// <summary>
    /// Gets the currently selected factory definition, or null if none is selected.
    /// </summary>
    public ReadOnlyFactoryDefinition FactoryDefinition =>
        FactoryDefinitionId.HasValue ? Space?.FindFactoryDefinition(FactoryDefinitionId.Value) : null;

    /// <summary>
    /// Gets the currently selected factory instance, or null if none is selected.
    /// </summary>
    public ReadOnlyFactoryInstance FactoryInstance =>
        FactoryInstanceId.HasValue ? View?.FindFactoryInstance(FactoryInstanceId.Value) : null;
    
    /// <summary>
    /// Gets the type of main screen that should be displayed based on the current selection.
    /// </summary>
    /// <remarks>
    /// The main screen type is determined by the deepest level of hierarchy that has a valid selection:
    /// - View screen if project, space, and view are selected
    /// - Space screen if project and space are selected
    /// - Project screen if only project is selected
    /// - Home screen if nothing is selected
    /// </remarks>
    public MainScreenType CurrentMain
    {
        get
        {
            if (ProjectId.HasValue && SpaceId.HasValue && FactoryDefinitionId.HasValue)
                return MainScreenType.FactoryBuilder;
            if (ProjectId.HasValue && SpaceId.HasValue && ViewId.HasValue)
                return MainScreenType.View;
            if (ProjectId.HasValue && SpaceId.HasValue)
                return MainScreenType.Space;
            if (ProjectId.HasValue)
                return MainScreenType.Project;
            return MainScreenType.Home;
        }
    }
    
    /// <summary>
    /// Gets the current overlay screen type, or None if no overlay is active.
    /// </summary>
    public OverlayScreenType CurrentOverlay { get; private set; }
    
    #endregion
    
    #region Events

    /// <summary>
    /// Event raised when the application theme (light/dark mode) changes.
    /// </summary>
    public event Func<Task> OnThemeChanged;
    
    /// <summary>
    /// Event raised when any state in the session changes.
    /// This is a general-purpose event that fires for all state changes.
    /// </summary>
    public event Func<Task> OnStateChanged;
    
    /// <summary>
    /// Event raised when the browser window is resized.
    /// </summary>
    public event Func<Task> OnWindowResized;
    
    /// <summary>
    /// Event raised when the selected project changes.
    /// </summary>
    public event Func<Task> OnProjectChanged;
    
    /// <summary>
    /// Event raised when the selected space changes.
    /// </summary>
    public event Func<Task> OnSpaceChanged;
    
    /// <summary>
    /// Event raised when the selected view changes.
    /// </summary>
    public event Func<Task> OnViewChanged;
    
    /// <summary>
    /// Event raised when the selected job changes.
    /// </summary>
    public event Func<Task> OnJobChanged;

    /// <summary>
    /// Event raised when the navigated folder changes.
    /// </summary>
    public event Func<Task> OnFolderChanged;

    /// <summary>
    /// Event raised when the selected factory definition changes.
    /// </summary>
    public event Func<Task> OnFactoryDefinitionChanged;

    /// <summary>
    /// Event raised when the selected factory instance changes.
    /// </summary>
    public event Func<Task> OnFactoryInstanceChanged;

    /// <summary>
    /// Event raised when the main screen type changes.
    /// </summary>
    public event Func<Task> OnMainChanged;
    
    /// <summary>
    /// Event raised when the overlay screen type changes.
    /// </summary>
    public event Func<Task> OnOverlayChanged;

    /// <summary>
    /// Event raised when the right panel collapsed state changes.
    /// </summary>
    public event Func<Task> OnRightPanelCollapsedChanged;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="RelaySession"/> class.
    /// </summary>
    /// <param name="navigationManager">The Blazor navigation manager.</param>
    /// <param name="jsRuntime">The JavaScript runtime for interop.</param>
    /// <param name="toastService">The toast notification service.</param>
    /// <param name="dataManager">The data manager to access application data.</param>
    public RelaySession(NavigationManager navigationManager, IJSRuntime jsRuntime, IToastService toastService, DataManager.DataManager dataManager)
    {
        _navigationManager = navigationManager;
        _jsRuntime = jsRuntime;
        _toastService = toastService;
        _dataManager = dataManager;
        _objectReference = DotNetObjectReference.Create(this);

        try
        {
            _navigationManager.LocationChanged += HandleLocationChanged;
        }
        catch{}
    }
    
    private bool _initialized = false;
    
    /// <summary>
    /// Asynchronously initializes the session with browser-specific information and state.
    /// </summary>
    /// <remarks>
    /// This method performs several tasks:
    /// - Sets up JavaScript interop for window resize events
    /// - Detects client OS and window dimensions
    /// - Determines initial theme preference from system
    /// - Parses the current URL to set initial navigation state
    /// - Triggers initial events
    /// </remarks>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        
        // Set up JS interop for window resize events
        await _jsRuntime.InvokeVoidAsync("relaySessionInterop.initialize", _objectReference);
        await UpdateWindowSizeAsync();
        
        // Get client OS
        string os = await _jsRuntime.InvokeAsync<string>("relaySessionInterop.getClientOS");
        switch (os)
        {
            case "Windows":
                ClientOs = ClientOs.Windows;
                break;
            case "MacOS":
                ClientOs = ClientOs.Mac;
                break;
            case "Linux":
                ClientOs = ClientOs.Linux;
                break;
            default:
                ClientOs = ClientOs.Unknown;
                break;
        }

        // Get initial system theme preference (this resets on each session start)
        bool isDarkMode = await _jsRuntime.InvokeAsync<bool>("relaySessionInterop.getSystemThemePreference");
        ColorTheme = isDarkMode ? ColorTheme.Dark : ColorTheme.Light;
        _hasManualThemeOverride = false; // Reset manual override flag on session start
        
        // Process initial URL to set navigation state
        HandleLocationChanged(null, null);
        _initialized = true;
        
        // Notify about initial theme
        await OnThemeChanged.InvokeAllAsync();
    }
    
    #region Dimensions

    /// <summary>
    /// Handles window resize events from JavaScript.
    /// Called by JavaScript interop when the browser window size changes.
    /// </summary>
    [JSInvokable]
    public async Task HandleWindowResize()
    {
        await UpdateWindowSizeAsync();
        await OnWindowResized.InvokeAllAsync();
    }

    /// <summary>
    /// Updates the current window dimensions from the browser.
    /// </summary>
    private async Task UpdateWindowSizeAsync()
    {
        WindowWidth = await _jsRuntime.InvokeAsync<int>("relaySessionInterop.getWindowWidth");
        WindowHeight = await _jsRuntime.InvokeAsync<int>("relaySessionInterop.getWindowHeight");
    }

    /// <summary>
    /// Calculates the width of the center panel based on window dimensions and panel states.
    /// </summary>
    /// <returns>The width of the center panel in pixels.</returns>
    public int GetCenterPanelWidth() => WindowWidth - LeftPanelWidth -
        (IsRightPanelExpanded() ? (RightPanelWidth + DividerWidth) : 0);
    
    /// <summary>
    /// Calculates the height of the center panel based on window dimensions.
    /// </summary>
    /// <returns>The height of the center panel in pixels.</returns>
    public int GetCenterPanelHeight() => WindowHeight - TopPanelHeight;
    
    /// <summary>
    /// Determines whether the right panel should be expanded.
    /// </summary>
    /// <returns>True if the right panel should be expanded; otherwise, false.</returns>
    public bool IsRightPanelExpanded() => !_isRightPanelCollapsed;
    
    #endregion
    
    #region Mouse position

    /// <summary>
    /// Gets the mouse position relative to a specific element.
    /// </summary>
    /// <param name="args">The mouse event arguments.</param>
    /// <param name="elementId">The ID of the element to calculate position relative to.</param>
    /// <returns>
    /// A <see cref="float2"/> containing the X and Y coordinates relative to the element,
    /// or null if the element couldn't be found.
    /// </returns>
    /// <remarks>
    /// This method uses JavaScript interop to calculate coordinates relative to a specific element.
    /// If the JavaScript call fails, it falls back to returning the absolute client coordinates.
    /// </remarks>
    public async Task<float2?> GetRelativeMousePosition(MouseEventArgs args, string elementId)
    {
        try
        {
            var position = await _jsRuntime.InvokeAsync<dynamic>("relaySessionInterop.getRelativeMousePosition",
                                                                 new { args.ClientX, args.ClientY },
                                                                 elementId);

            if (position == null)
                return null;

            return new float2((float)position.x, (float)position.y);
        }
        catch (JSException)
        {
            // Fallback to absolute coordinates if the JavaScript call fails
            return new float2((float)args.ClientX, (float)args.ClientY);
        }
        catch (ObjectDisposedException)
        {
            // Fallback if the JavaScript runtime is already disposed
            return new float2((float)args.ClientX, (float)args.ClientY);
        }
    }

    #endregion
    
    #region Authentication

    /// <summary>
    /// Sets the authentication state of the session.
    /// </summary>
    /// <param name="authenticated">True if the user is authenticated; otherwise, false.</param>
    /// <param name="user">The authenticated user, or null if not authenticated.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method updates the authentication state and triggers the state changed event.
    /// It's used when a user logs in, logs out, or when the session initializes with a remembered user.
    /// </remarks>
    public async Task SetAuthenticated(bool authenticated, ReadOnlyUser user)
    {
        IsAuthenticated = authenticated;
        User = user;
        await OnStateChanged.InvokeAllAsync();
    }
    
    #endregion
    
    #region Theme
    
    private readonly bool _useSystemTheme = true;
    private bool _hasManualThemeOverride = false;
    
    /// <summary>
    /// Sets the color theme for the application.
    /// </summary>
    /// <param name="theme">The color theme to set.</param>
    /// <param name="isManualChange">True if the change was initiated by the user; otherwise, false for system changes.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// When a user manually changes the theme, it overrides the system theme preference.
    /// The override persists until the session is reinitialized.
    /// </remarks>
    public async Task SetColorTheme(ColorTheme theme, bool isManualChange = true)
    {
        if (isManualChange)
            _hasManualThemeOverride = true; // Mark that user has manually changed the theme
        
        if (ColorTheme != theme)
        {
            ColorTheme = theme;
            await OnThemeChanged.InvokeAllAsync();
        }
    }
    
    /// <summary>
    /// Handles system theme change events from JavaScript.
    /// </summary>
    /// <param name="isDarkMode">True if the system theme is dark mode; otherwise, false.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method is called by JavaScript interop when the system theme changes.
    /// It only applies the system theme if the user hasn't manually overridden it.
    /// </remarks>
    [JSInvokable]
    public async Task HandleSystemThemeChange(bool isDarkMode)
    {
        // Only react to system theme changes if user hasn't manually overridden
        if (!_hasManualThemeOverride)
            await SetColorTheme(isDarkMode ? ColorTheme.Dark : ColorTheme.Light, false);
    }
    
    #endregion

    #region Right Panel

    /// <summary>
    /// Sets the collapsed state of the right panel.
    /// </summary>
    /// <param name="collapsed">True to collapse the right panel; false to expand it.</param>
    public async Task SetRightPanelCollapsed(bool collapsed)
    {
        if (_isRightPanelCollapsed != collapsed)
        {
            _isRightPanelCollapsed = collapsed;
            await OnRightPanelCollapsedChanged.InvokeAllAsync();
        }
    }

    #endregion

    #region Navigation

    /// <summary>
    /// Navigates to a new state in the application based on the provided request.
    /// </summary>
    /// <param name="request">The navigation request containing target IDs and overlay state.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method:
    /// 1. Updates the internal state based on the request
    /// 2. Corrects any invalid state (e.g., referenced objects not found)
    /// 3. Updates the URL to reflect the new state
    /// 4. Triggers appropriate change events based on what changed
    /// 
    /// It's the primary entry point for navigation within the application.
    /// </remarks>
    public async Task NavigateToAsync(NavigationRequest request)
    {
        bool projectChanged = ProjectId != request.ProjectId;
        bool spaceChanged = SpaceId != request.SpaceId;
        bool viewChanged = ViewId != request.ViewId;
        bool jobChanged = JobId != request.JobId;
        bool folderChanged = FolderId != request.FolderId;
        bool factoryDefinitionChanged = FactoryDefinitionId != request.FactoryDefinitionId;
        bool factoryInstanceChanged = FactoryInstanceId != request.FactoryInstanceId;
        bool overlayChanged = CurrentOverlay != request.Overlay;

        // Update state
        ProjectId = request.ProjectId;
        SpaceId = request.SpaceId;
        ViewId = request.ViewId;
        JobId = request.JobId;
        FolderId = request.FolderId;
        FactoryDefinitionId = request.FactoryDefinitionId;
        FactoryInstanceId = request.FactoryInstanceId;
        CurrentOverlay = request.Overlay;

        // Ensure state is valid (e.g., objects exist)
        bool correctionMade = CorrectState();

        // Build URL from current state
        var url = BuildUrl();

        // Navigate without reload
        _navigationManager.NavigateTo(url, false);

        // Trigger change events
        await OnStateChanged.InvokeAllAsync();

        if (projectChanged)
            await OnProjectChanged.InvokeAllAsync();
        if (spaceChanged)
            await OnSpaceChanged.InvokeAllAsync();
        if (viewChanged)
            await OnViewChanged.InvokeAllAsync();
        if (jobChanged)
            await OnJobChanged.InvokeAllAsync();
        if (folderChanged)
            await OnFolderChanged.InvokeAllAsync();
        if (factoryDefinitionChanged)
            await OnFactoryDefinitionChanged.InvokeAllAsync();
        if (factoryInstanceChanged)
            await OnFactoryInstanceChanged.InvokeAllAsync();

        if (projectChanged || spaceChanged || viewChanged || jobChanged || factoryDefinitionChanged)
            await OnMainChanged.InvokeAllAsync();

        if (overlayChanged)
            await OnOverlayChanged.InvokeAllAsync();
    }

    /// <summary>
    /// Handles URL changes in the browser and updates the application state to match.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event arguments.</param>
    /// <remarks>
    /// This method is called when the URL changes through browser navigation (back/forward buttons)
    /// or external linking. It:
    /// 1. Parses the URL to extract IDs
    /// 2. Updates application state if it differs from the URL
    /// 3. Corrects any invalid state
    /// 4. Triggers appropriate change events
    /// 
    /// This enables deep linking and proper browser history integration.
    /// </remarks>
    private void HandleLocationChanged(object sender, LocationChangedEventArgs e)
    {
        // Parse URL and update state if navigation happened externally
        var segments = _navigationManager.Uri.Split('/')
            .Skip(3) // Skip protocol and domain
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();

        var request = new NavigationRequest();
        
        // Parse URL segments into a navigation request
        foreach (string segment in segments)
            if (segment.StartsWith("P") && int.TryParse(segment[1..], out var projectId))
                request.ProjectId = projectId;
            else if (segment.StartsWith("S") && int.TryParse(segment[1..], out var spaceId))
                request.SpaceId = spaceId;
            else if (segment.StartsWith("V") && int.TryParse(segment[1..], out var viewId))
                request.ViewId = viewId;
            // FD and FI checks must come BEFORE F to avoid "F" matching "FD3" or "FI5"
            else if (segment.StartsWith("FD") && int.TryParse(segment[2..], out var factoryDefinitionId))
                request.FactoryDefinitionId = factoryDefinitionId;
            else if (segment.StartsWith("FI") && int.TryParse(segment[2..], out var factoryInstanceId))
                request.FactoryInstanceId = factoryInstanceId;
            else if (segment.StartsWith("F") && int.TryParse(segment[1..], out var folderId))
                request.FolderId = folderId;
            else if (segment.StartsWith("J") && int.TryParse(segment[1..], out var jobId))
                request.JobId = jobId;

        // Only update if state actually changed
        if (ProjectId != request.ProjectId || SpaceId != request.SpaceId ||
            ViewId != request.ViewId || JobId != request.JobId || FolderId != request.FolderId ||
            FactoryDefinitionId != request.FactoryDefinitionId ||
            FactoryInstanceId != request.FactoryInstanceId)
        {
            bool projectChanged = ProjectId != request.ProjectId;
            bool spaceChanged = SpaceId != request.SpaceId;
            bool viewChanged = ViewId != request.ViewId;
            bool jobChanged = JobId != request.JobId;
            bool folderChanged = FolderId != request.FolderId;
            bool factoryDefinitionChanged = FactoryDefinitionId != request.FactoryDefinitionId;
            bool factoryInstanceChanged = FactoryInstanceId != request.FactoryInstanceId;
            bool overlayChanged = CurrentOverlay != request.Overlay;

            ProjectId = request.ProjectId;
            SpaceId = request.SpaceId;
            ViewId = request.ViewId;
            JobId = request.JobId;
            FolderId = request.FolderId;
            FactoryDefinitionId = request.FactoryDefinitionId;
            FactoryInstanceId = request.FactoryInstanceId;

            // Ensure state is valid and URL is corrected if needed
            bool isCorrect = CorrectState();
            CorrectUrl();

            // Trigger change events synchronously since this is called from event handler
            OnStateChanged.InvokeAllAsync().Wait();

            if (projectChanged)
                OnProjectChanged.InvokeAllAsync().Wait();
            if (spaceChanged)
                OnSpaceChanged.InvokeAllAsync().Wait();
            if (viewChanged)
                OnViewChanged.InvokeAllAsync().Wait();
            if (jobChanged)
                OnJobChanged.InvokeAllAsync().Wait();
            if (folderChanged)
                OnFolderChanged.InvokeAllAsync().Wait();
            if (factoryDefinitionChanged)
                OnFactoryDefinitionChanged.InvokeAllAsync().Wait();
            if (factoryInstanceChanged)
                OnFactoryInstanceChanged.InvokeAllAsync().Wait();

            if (projectChanged || spaceChanged || viewChanged || jobChanged || factoryDefinitionChanged)
                OnMainChanged.InvokeAllAsync().Wait();

            if (overlayChanged)
                OnOverlayChanged.InvokeAllAsync().Wait();
        }
    }

    /// <summary>
    /// Builds a URL string from a navigation request.
    /// </summary>
    /// <param name="request">The navigation request containing target IDs.</param>
    /// <returns>A URL path string that represents the navigation target.</returns>
    /// <remarks>
    /// URLs follow a hierarchical pattern with segments for each level:
    /// - /P{ProjectId} for project
    /// - /P{ProjectId}/S{SpaceId} for space
    /// - /P{ProjectId}/S{SpaceId}/V{ViewId} for view
    /// - /P{ProjectId}/S{SpaceId}/V{ViewId}/J{JobId} for job
    /// </remarks>
    public static string BuildUrl(NavigationRequest request)
    {
        var url = "/";
        if (request.ProjectId.HasValue)
        {
            url += $"P{request.ProjectId}";
            if (request.SpaceId.HasValue)
            {
                url += $"/S{request.SpaceId}";

                // Factory definition is at space level (mutually exclusive with view)
                if (request.FactoryDefinitionId.HasValue)
                {
                    url += $"/FD{request.FactoryDefinitionId}";
                }
                else if (request.ViewId.HasValue)
                {
                    url += $"/V{request.ViewId}";
                    if (request.FolderId.HasValue)
                        url += $"/F{request.FolderId}";
                    if (request.FactoryInstanceId.HasValue)
                        url += $"/FI{request.FactoryInstanceId}";
                    if (request.JobId.HasValue)
                        url += $"/J{request.JobId}";
                }
            }
        }

        return url;
    }

    private string BuildUrl()
    {
        return BuildUrl(new NavigationRequest
        {
            ProjectId = ProjectId,
            SpaceId = SpaceId,
            ViewId = ViewId,
            FolderId = FolderId,
            JobId = JobId,
            FactoryDefinitionId = FactoryDefinitionId,
            FactoryInstanceId = FactoryInstanceId
        });
    }

    /// <summary>
    /// Opens the given navigation target in a new browser tab via JS interop.
    /// </summary>
    public async Task OpenInNewTabAsync(NavigationRequest request)
    {
        var url = BuildUrl(request);
        await _jsRuntime.InvokeVoidAsync("window.open", url, "_blank");
    }

    /// <summary>
    /// Validates and corrects the current navigation state, ensuring all referenced objects exist.
    /// </summary>
    /// <returns>True if the state was already correct; false if corrections were made.</returns>
    /// <remarks>
    /// This method checks if:
    /// - The referenced project, space, view, and job actually exist
    /// - The user has access to the referenced project
    /// - The hierarchy is consistent (e.g., space belongs to project)
    /// 
    /// If any inconsistencies are found, the state is corrected by nulling out the invalid references.
    /// </remarks>
    private bool CorrectState()
    {
        bool correctionNeeded = false;

        int? correctedProjectId;
        int? correctedSpaceId;
        int? correctedViewId;
        int? correctedJobId;
        int? correctedFolderId;

        // Check if user has access to project
        if (Project != null && User != null &&
            User.Id != Project.Owner?.Id &&
            Project.Members.All(m => m.Id != User.Id))
            correctedProjectId = null;

        // Get actual IDs from loaded objects to ensure they exist
        correctedProjectId = Project?.Id ?? null;
        correctedSpaceId = Space?.Id ?? null;
        correctedViewId = View?.Id ?? null;
        correctedJobId = Job?.Id ?? null;
        correctedFolderId = Folder?.Id ?? null;
        int? correctedFactoryDefinitionId = FactoryDefinition?.Id ?? null;
        int? correctedFactoryInstanceId = FactoryInstance?.Id ?? null;

        // Apply corrections if needed
        if (correctedProjectId != ProjectId || correctedSpaceId != SpaceId ||
            correctedViewId != ViewId || correctedJobId != JobId ||
            correctedFolderId != FolderId ||
            correctedFactoryDefinitionId != FactoryDefinitionId ||
            correctedFactoryInstanceId != FactoryInstanceId)
        {
            ProjectId = correctedProjectId;
            SpaceId = correctedSpaceId;
            ViewId = correctedViewId;
            JobId = correctedJobId;
            FolderId = correctedFolderId;
            FactoryDefinitionId = correctedFactoryDefinitionId;
            FactoryInstanceId = correctedFactoryInstanceId;
            correctionNeeded = true;
        }

        return !correctionNeeded;
    }

    /// <summary>
    /// Ensures the browser URL matches the current application state.
    /// </summary>
    /// <remarks>
    /// This method is called after correcting the state to make sure the URL
    /// reflects the corrected state. If a mismatch is found, it navigates to
    /// the correct URL and shows an error message.
    /// </remarks>
    private void CorrectUrl()
    {
        var currentUrl = '/' +
                         string.Join('/', _navigationManager.Uri.Split('/')
                                                            .Skip(3) // Skip protocol and domain
                                                            .Where(s => !string.IsNullOrEmpty(s))
                                                            .ToArray());
        var desiredUrl = BuildUrl();

        if (currentUrl != desiredUrl)
        {
            _toastService.ShowError($"Couldn't find requested page. Redirecting to {desiredUrl}");
            _navigationManager.NavigateTo(desiredUrl);
        }
    }
    
    /// <summary>
    /// Closes the current overlay screen.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CloseOverlayAsync()
    {
        CurrentOverlay = OverlayScreenType.None;
        await OnStateChanged.InvokeAllAsync();
    }
    
    #endregion

    /// <summary>
    /// Asynchronously releases the resources used by the session.
    /// </summary>
    /// <returns>A value task representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_initialized && _objectReference != null)
        {
            try 
            {
                await _jsRuntime.InvokeVoidAsync("relaySessionInterop.dispose");
                _objectReference.Dispose();
            }
            catch (InvalidOperationException) 
            {
                // Ignore JavaScript interop errors during prerendering
            }
            _objectReference = null;
        }
    }
}

/// <summary>
/// Defines the possible main screen types in the application.
/// </summary>
public enum MainScreenType
{
    /// <summary>
    /// The home screen showing projects list.
    /// </summary>
    Home,
    
    /// <summary>
    /// The project screen showing spaces within a project.
    /// </summary>
    Project,
    
    /// <summary>
    /// The space screen showing views within a space.
    /// </summary>
    Space,
    
    /// <summary>
    /// The view screen showing the job graph.
    /// </summary>
    View,

    /// <summary>
    /// The factory builder screen for editing factory definitions.
    /// </summary>
    FactoryBuilder
}

/// <summary>
/// Defines the possible overlay screen types in the application.
/// </summary>
public enum OverlayScreenType
{
    /// <summary>
    /// No overlay is shown.
    /// </summary>
    None,
    
    /// <summary>
    /// The queues management overlay.
    /// </summary>
    Queues,
    
    /// <summary>
    /// The application settings overlay.
    /// </summary>
    Settings,

    /// <summary>
    /// The current user's personal settings overlay (e.g. access tokens).
    /// </summary>
    Personal
}

/// <summary>
/// Defines the possible color themes for the application.
/// </summary>
public enum ColorTheme
{
    /// <summary>
    /// Light theme with bright background and dark text.
    /// </summary>
    Light,
    
    /// <summary>
    /// Dark theme with dark background and light text.
    /// </summary>
    Dark
}

/// <summary>
/// Defines the detected client operating system types.
/// </summary>
public enum ClientOs
{
    /// <summary>
    /// Unknown or unrecognized operating system.
    /// </summary>
    Unknown,
    
    /// <summary>
    /// Microsoft Windows operating system.
    /// </summary>
    Windows,
    
    /// <summary>
    /// Linux operating system.
    /// </summary>
    Linux,
    
    /// <summary>
    /// macOS operating system.
    /// </summary>
    Mac
}

/// <summary>
/// Represents a request to navigate to a specific location in the application.
/// Contains the IDs for project, space, view, and job, as well as the overlay type.
/// </summary>
public class NavigationRequest
{
    /// <summary>
    /// Gets or sets the ID of the project to navigate to, or null to navigate to the home screen.
    /// </summary>
    public int? ProjectId { get; set; }
    
    /// <summary>
    /// Gets or sets the ID of the space to navigate to, or null for project-level navigation.
    /// </summary>
    public int? SpaceId { get; set; }
    
    /// <summary>
    /// Gets or sets the ID of the view to navigate to, or null for space-level navigation.
    /// </summary>
    public int? ViewId { get; set; }

    /// <summary>
    /// Gets or sets the ID of the folder to navigate into, or null for root level.
    /// </summary>
    public int? FolderId { get; set; }

    /// <summary>
    /// Gets or sets the ID of the job to navigate to, or null for view-level navigation.
    /// </summary>
    public int? JobId { get; set; }

    /// <summary>
    /// Gets or sets the ID of the factory definition to navigate to, or null for space-level navigation.
    /// Factory definitions are at the space level, mutually exclusive with view.
    /// </summary>
    public int? FactoryDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the ID of the factory instance to navigate to, or null for view-level navigation.
    /// Factory instances live inside views.
    /// </summary>
    public int? FactoryInstanceId { get; set; }

    /// <summary>
    /// Gets or sets the overlay screen type to show.
    /// </summary>
    public OverlayScreenType Overlay { get; set; }
}