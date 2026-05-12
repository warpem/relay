using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Services;

namespace Refund.Components.GlobalTooltip;

public partial class GlobalTooltipContainer : IDisposable
{
    [Inject] GlobalTooltipService TooltipService { get; set; } 
    [Inject] IJSRuntime JsRuntime { get; set; }
    
    private IJSObjectReference? _module;
    private bool _isInitialized;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/GlobalTooltip/GlobalTooltipContainer.razor.js");
            
            _isInitialized = true;
            await UpdateTooltipPositions();
        }
    }

    protected override void OnInitialized()
    {
        TooltipService.TooltipsChanged += OnTooltipsChanged;
    }

    private async Task OnTooltipsChanged(object? sender, EventArgs e)
    {
        // First, update the UI
        await InvokeAsync(StateHasChanged);
        
        if (_isInitialized)
        {
            // Then immediately update positions without delay
            // The JS will handle visibility/measurement correctly
            await InvokeAsync(UpdateTooltipPositions);
        }
    }

    private async Task UpdateTooltipPositions()
    {
        if (_module != null)
        {
            await _module.InvokeVoidAsync("updateTooltipPositions");
        }
    }

    public void Dispose()
    {
        TooltipService.TooltipsChanged -= OnTooltipsChanged;
        if (_module != null)
        {
            Task.Run(async () => {
                try
                {
                    // Cleanup the tooltip tracking when component is disposed
                    await _module.InvokeVoidAsync("cleanupTooltipTracking");
                }
                catch (Exception)
                {
                    // Ignore exceptions during disposal
                }
                finally
                {
                    await _module.DisposeAsync();
                }
            });
        }
    }
}