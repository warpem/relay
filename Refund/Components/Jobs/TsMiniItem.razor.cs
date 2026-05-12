using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.SingleAxisScatter;
using Refund.Services;
using Refund.Utils;
using System.Collections.Generic;
using System.Linq;

namespace Refund.Components.Jobs;

public partial class TsMiniItem : ComponentBase, IDisposable
{
    [Parameter]
    public int ThumbnailSize { get; set; }
    
    [Parameter]
    public Func<string, string> GetThumbnail { get; set; }
    
    [Inject] 
    private ScatterHighlightService HighlightService { get; set; }
    
    [Inject]
    private FileService FileService { get; set; }
    
    [Inject]
    private IJSRuntime JsRuntime { get; set; }
    
    private ElementReference AnimationElement;
    private ScatterPoint? _item;
    private bool _isAnimating = false;
    private const int AnimationFps = 20;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        HighlightService.HighlightChanged += HandleHighlightChanged;
    }

    private async Task HandleHighlightChanged(object sender, int index, ScatterPoint? item)
    {
        // Stop any existing animation when switching items
        if (_isAnimating)
        {
            await StopAnimation();
        }
        
        if (item != null && item.Value.Metadata is WarpTools.MiniJsonTsItem)
        {
            _item = item;
            await InvokeAsync(StateHasChanged);
            
            // Start animation after the component has been rendered
            await StartAnimation();
        }
        else
        {
            _item = null;
            await InvokeAsync(StateHasChanged);
        }
    }
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && _item != null && _item.Value.Metadata is WarpTools.MiniJsonTsItem)
        {
            // Start animation on first render
            await StartAnimation();
        }
    }
    
    private async Task StartAnimation()
    {
        if (_item == null || _item.Value.Metadata is not WarpTools.MiniJsonTsItem miniTs || 
            miniTs.TiltMoviePaths == null || miniTs.TiltMoviePaths.Length <= 1 || GetThumbnail == null)
            return;

        // Create animation sequence similar to ImportDataSetTsExpandedView
        List<int> animationIndices = new();
        var middleTilt = miniTs.TiltMoviePaths.Length / 2;
        
        // Add frames going from middle to end
        animationIndices.AddRange(Enumerable.Range(middleTilt, miniTs.TiltMoviePaths.Length - middleTilt));
        
        // Add frames going from end-1 back to beginning
        animationIndices.AddRange(Enumerable.Range(0, miniTs.TiltMoviePaths.Length - 1).Reverse());
        
        // Add frames going from beginning+1 to middle
        animationIndices.AddRange(Enumerable.Range(1, middleTilt - 1));
        
        // Convert indices to file paths and then to URLs
        var imageUrls = animationIndices
            .Select(i => FileService.GetUrl(GetThumbnail(miniTs.TiltMoviePaths[i])))
            .ToArray();
        
        if (imageUrls.Length > 0)
        {
            _isAnimating = true;
            await JsRuntime.InvokeVoidAsync("startThumbnailAnimation", AnimationElement, imageUrls, AnimationFps);
        }
    }
    
    private async Task StopAnimation()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            await JsRuntime.InvokeVoidAsync("stopThumbnailAnimation", AnimationElement);
        }
    }
    
    public void Dispose()
    {
        // Stop animation
        if (_isAnimating)
        {
            // Since we can't await in Dispose, use InvokeVoidAsync without awaiting
            JsRuntime.InvokeVoidAsync("stopThumbnailAnimation", AnimationElement);
            _isAnimating = false;
        }
        
        HighlightService.HighlightChanged -= HandleHighlightChanged;
    }
}