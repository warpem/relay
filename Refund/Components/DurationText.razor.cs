using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Refund.Components;

/// <summary>
/// Component that displays a time duration relative to a provided timestamp.
/// Automatically updates the display as time progresses (e.g., "5 minutes ago", "just now", etc.).
/// Useful for displaying creation times, update times, or activity timestamps.
/// </summary>
public partial class DurationText : ComponentBase, IAsyncDisposable
{
    /// <summary>
    /// The reference timestamp from which to calculate the duration.
    /// Required parameter.
    /// </summary>
    [Parameter, EditorRequired] 
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// JavaScript runtime for interop functionality.
    /// </summary>
    [Inject] 
    public IJSRuntime JSRuntime { get; set; }
    
    /// <summary>
    /// Unique identifier for the element to target with JavaScript.
    /// </summary>
    private string elementId = $"duration-{Guid.NewGuid()}";
    
    /// <summary>
    /// Reference to this component for JavaScript callbacks.
    /// </summary>
    private DotNetObjectReference<DurationText> _objectReference;
    
    /// <summary>
    /// Reference to the imported JavaScript module.
    /// </summary>
    private IJSObjectReference _module;
    
    /// <summary>
    /// Tracks the previously rendered timestamp to detect changes.
    /// </summary>
    private DateTime? _previousTimestamp;
    
    /// <summary>
    /// Indicates whether the JavaScript module has been initialized.
    /// </summary>
    private bool _isModuleInitialized;
    
    /// <summary>
    /// Tracks whether a timestamp update is pending JavaScript module initialization.
    /// </summary>
    private bool _pendingTimestampUpdate;

    /// <summary>
    /// Handles parameter changes by updating the timer when the timestamp changes.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // Only proceed if timestamp has changed
        if (_previousTimestamp == Timestamp)
            return;

        _previousTimestamp = Timestamp;

        // If module is initialized, update the timer
        // Otherwise, mark for update after module init
        if (_isModuleInitialized)
        {
            await UpdateTimer();
        }
        else
        {
            _pendingTimestampUpdate = true;
        }
    }

    /// <summary>
    /// Initializes the JavaScript module after the component is rendered.
    /// Sets up the timer for automatic updates.
    /// </summary>
    /// <param name="firstRender">Whether this is the first time the component has rendered</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _objectReference = DotNetObjectReference.Create(this);
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/DurationText.razor.js");
            _isModuleInitialized = true;

            // If there was a pending update (timestamp changed before module was ready)
            // or if this is the first render, initialize the timer
            if (_pendingTimestampUpdate || firstRender)
            {
                await UpdateTimer();
                _pendingTimestampUpdate = false;
            }
        }
    }

    /// <summary>
    /// Updates the timer by calling into JavaScript.
    /// This initializes or resets the duration updater with the current timestamp.
    /// </summary>
    private async Task UpdateTimer()
    {
        if (_module != null)
        {
            await _module.InvokeVoidAsync("initializeDurationUpdater", elementId, Timestamp);
        }
    }

    /// <summary>
    /// Cleans up resources when the component is disposed.
    /// Stops JavaScript timer intervals and disposes references.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try 
        {
            if (_module != null)
            {
                await _module.InvokeVoidAsync("cleanupDurationUpdater", elementId);
                await _module.DisposeAsync();
            }
            _objectReference?.Dispose();
        }
        catch (JSDisconnectedException)
        {
            // Safely ignore JS disconnection errors during disposal
        }
    }
}