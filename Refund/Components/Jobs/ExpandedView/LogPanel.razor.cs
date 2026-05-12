using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Refund.Services;

namespace Refund.Components.Jobs.ExpandedView;

/// <summary>
/// Component for displaying job logs and errors in the expanded job view.
/// Provides toggleable panels for viewing output logs and error messages,
/// with synchronized scrolling between log content and job iterations.
/// </summary>
public partial class LogPanel : IAsyncDisposable
{
    /// <summary>
    /// JavaScript runtime for browser interactions.
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; }
    
    /// <summary>
    /// Service for expanded job view state and operations.
    /// </summary>
    [Inject]
    private ExpandedJobViewService Service { get; set; }
    
    /// <summary>
    /// Logger for component operations.
    /// </summary>
    [Inject]
    private ILogger<LogPanel> Logger { get; set; } = default!;

    /// <summary>
    /// Reference to the DOM element containing log content.
    /// Used for JavaScript interop scrolling operations.
    /// </summary>
    private ElementReference _logContentElement;
    
    /// <summary>
    /// .NET object reference for JavaScript callbacks.
    /// </summary>
    private DotNetObjectReference<LogPanel> _objRef;
    
    /// <summary>
    /// Indicates whether the JavaScript module has been initialized.
    /// </summary>
    private bool _initialized;
    
    /// <summary>
    /// Reference to the imported JavaScript module.
    /// </summary>
    private IJSObjectReference _module;
    
    /// <summary>
    /// Determines if the staging panel is currently visible.
    /// </summary>
    private bool _showingStaging => Service.IsLogPanelExpanded && Service.CurrentSection == LogSection.Staging;
    
    /// <summary>
    /// Determines if the logs panel is currently visible.
    /// </summary>
    private bool _showingLogs => Service.IsLogPanelExpanded && Service.CurrentSection == LogSection.Output;
    
    /// <summary>
    /// Determines if the errors panel is currently visible.
    /// </summary>
    private bool _showingErrors => Service.IsLogPanelExpanded && Service.CurrentSection == LogSection.Errors;
    
    /// <summary>
    /// Flag to prevent scroll-position feedback loops between UI and JavaScript.
    /// </summary>
    private bool _scrollingInProgress;

    /// <summary>
    /// Initializes the component and subscribes to service events.
    /// </summary>
    protected override void OnInitialized()
    {
        _objRef = DotNetObjectReference.Create(this);
        Service.OnLogsUpdated += HandleLogsUpdated;
        Service.OnErrorsUpdated += HandleErrorsUpdated;
        Service.OnStagingUpdated += HandleStagingUpdated;
        Service.OnIterationChanged += HandleIterationChanged;
        Service.OnLogPanelStateChanged += HandleLogPanelStateChanged;
    }

    /// <summary>
    /// Initializes the JavaScript module and registers event handlers after the component renders.
    /// </summary>
    /// <param name="firstRender">True if this is the first time the component has been rendered</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try 
            {
                _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/Jobs/ExpandedView/LogPanel.razor.js");
                await _module.InvokeVoidAsync("initialize", _objRef, _logContentElement);
                _initialized = true;

                await HandleLogsUpdated();
            }
            catch (JSException ex)
            {
                Logger.LogError(ex, "Error initializing log panel JS");
            }
        }
    }

    /// <summary>
    /// Handles updates to log content by refreshing the UI and updating scroll positions.
    /// Automatically scrolls to the end when viewing the latest iteration.
    /// </summary>
    private async Task HandleLogsUpdated()
    {
        if (!Service.IsLogPanelExpanded || 
            Service.CurrentSection != LogSection.Output)
            return;
    
        await InvokeAsync(async () =>
        {
            StateHasChanged();

            await Task.Delay(100);
        
            if (_initialized)
                try
                {
                    await _module.InvokeVoidAsync("updateLogHeights", _logContentElement);
                
                    // Only auto-scroll if it's the latest iteration
                    if (Service.CurrentIteration == Service.AvailableIterations.Max())
                        await _module.InvokeVoidAsync("scrollToIteration", 
                                                      _logContentElement, 
                                                      Service.CurrentIteration,
                                                      true,   // Scroll to end
                                                      false); // Use smooth scroll
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error scrolling log panel to bottom for job {JobId}", Service.CurrentJob?.Id);
                }
        });
    }

    /// <summary>
    /// Handles updates to error content by refreshing the UI.
    /// </summary>
    /// <param name="_">The error message (unused)</param>
    private async Task HandleErrorsUpdated(string _)
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles updates to staging content by refreshing the UI.
    /// </summary>
    /// <param name="_">The staging message (unused)</param>
    private async Task HandleStagingUpdated(string _)
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Toggles the visibility of a log section (either output logs or errors).
    /// When opening the output logs, ensures proper scrolling to the current iteration.
    /// </summary>
    /// <param name="section">The log section to toggle</param>
    private async Task TogglePanel(LogSection section)
    {
        if (Service.IsLogPanelExpanded && Service.CurrentSection == section)
            await Service.CloseLogPanel();
        else
        {
            await Service.OpenLogPanel(section);
        
            // When opening output log panel, update heights and then scroll
            if (section == LogSection.Output)
            {
                await InvokeAsync(async () =>
                {
                    // First render the content
                    StateHasChanged();
                
                    // Wait for render to complete
                    await Task.Delay(100);
                
                    if (_initialized)
                    {
                        try
                        {
                            // Update section heights
                            await _module.InvokeVoidAsync("updateLogHeights", _logContentElement);
                        
                            // Immediately scroll to current iteration
                            bool isLatestIteration = Service.AvailableIterations.Any() && 
                                                   Service.CurrentIteration == Service.AvailableIterations.Max();
                            await _module.InvokeVoidAsync("scrollToIteration", 
                                                          _logContentElement, 
                                                          Service.CurrentIteration,
                                                          isLatestIteration,
                                                          true);  // Use instant scroll
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Error updating scroll position for log panel in job {JobId}", Service.CurrentJob?.Id);
                        }
                    }
                });
            }
        }
    }
    
    /// <summary>
    /// Handles log panel state changes by refreshing the UI.
    /// </summary>
    private async Task HandleLogPanelStateChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Collapses the currently expanded log panel.
    /// </summary>
    private async Task CollapsePanel()
    {
        await Service.CloseLogPanel();
    }

    /// <summary>
    /// Handles iteration changes by updating the scroll position to show the selected iteration's logs.
    /// Only acts if the change was not triggered by scrolling (to prevent feedback loops).
    /// </summary>
    /// <param name="iteration">The new iteration number</param>
    private async Task HandleIterationChanged(int iteration)
    {
        // Only scroll if change wasn't triggered by scrolling
        if (!_scrollingInProgress && _initialized && 
            Service.IsLogPanelExpanded && 
            Service.CurrentSection == LogSection.Output)
        {
            try 
            {
                bool isLatestIteration = iteration == Service.AvailableIterations.Max();
                bool useInstantScroll = !Service.IsLogPanelExpanded; // Instant scroll when panel opens
                
                await _module.InvokeVoidAsync("scrollToIteration", 
                                              _logContentElement, 
                                              iteration,
                                              isLatestIteration,  // Scroll to end if latest iteration
                                              useInstantScroll);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error scrolling to iteration {Iteration} for job {JobId}", iteration, Service.CurrentJob?.Id);
            }
        }
    }

    /// <summary>
    /// Method invoked from JavaScript when the user scrolls the log panel.
    /// Updates the current iteration based on scroll position.
    /// </summary>
    /// <param name="iteration">The iteration corresponding to the current scroll position</param>
    [JSInvokable]
    public async Task HandleScroll(int iteration)
    {
        if (!Service.IsLogPanelExpanded || 
            Service.CurrentSection != LogSection.Output)
            return;
        
        if (Service.CurrentIteration != iteration)
        {
            _scrollingInProgress = true;
            await Service.SetIterationAsync(iteration);
            _scrollingInProgress = false;
        }
    }

    /// <summary>
    /// Cleans up resources when the component is disposed.
    /// Removes event subscriptions and disposes JavaScript references.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _logContentElement);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Ignore JavaScript interop errors during disposal
            }
        }

        Service.OnLogsUpdated -= HandleLogsUpdated;
        Service.OnErrorsUpdated -= HandleErrorsUpdated;
        Service.OnStagingUpdated -= HandleStagingUpdated;
        Service.OnIterationChanged -= HandleIterationChanged;
        Service.OnLogPanelStateChanged -= HandleLogPanelStateChanged;
        
        if (_objRef != null)
        {
            _objRef.Dispose();
            _objRef = null;
        }
    }
}