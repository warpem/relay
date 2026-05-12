using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Refund.Components.ThumbnailPanel;

public partial class Thumbnail : ComponentBase, IDisposable
{
    [Parameter]
    public ThumbnailData Data { get; set; }

    [Parameter]
    public bool IsSelected { get; set; }

    [Parameter]
    public EventCallback<ThumbnailData> OnSelected { get; set; }

    [Parameter]
    public int PositionLeft { get; set; }
    
    [Parameter]
    public int ThumbnailSize { get; set; }

    [Parameter]
    public bool ShowStatus { get; set; } = true;
    
    [Parameter]
    public bool ShowIndex { get; set; } = true;

    [Parameter]
    public CheckMode CheckMode { get; set; } = CheckMode.None;

    [Parameter]
    public bool? Check { get; set; }

    [Parameter]
    public EventCallback<ThumbnailData> CheckChanged { get; set; }

    public bool IsLoaded { get; set; } = false;
    public string ImageDataUrl { get; set; }
    public bool HasAnimation => Data?.AnimationPaths != null && Data.AnimationPaths.Length > 0;

    private ElementReference ThumbnailElement;
    private ElementReference AnimationElement;
    private string _previousImagePath;
    private bool _isAnimating = false;
    private const int AnimationFps = 20;

    protected override async Task OnParametersSetAsync()
    {
        if (_previousImagePath != Data.ImagePath)
        {
            _previousImagePath = Data.ImagePath;
            IsLoaded = true;
            //await LoadThumbnailImageAsync();
        }
    }

    //private async Task LoadThumbnailImageAsync()
    //{
    //    try
    //    {
    //        byte[] imageBytes = await File.ReadAllBytesAsync(Data.ImagePath);
    //        string base64String = Convert.ToBase64String(imageBytes);
    //        string mimeType = GetMimeType(Data.ImagePath);
    //        ImageDataUrl = $"data:{mimeType};base64,{base64String}";
    //        IsLoaded = true;
    //        StateHasChanged();
    //    }
    //    catch (Exception ex)
    //    {
    //        IsLoaded = true;
    //        // Provide a valid placeholder image
    //        ImageDataUrl = "";
    //    }
    //}

    private string GetMimeType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
    }

    private async Task OnThumbnailClicked()
    {
        await OnSelected.InvokeAsync(Data);
    }

    private async Task OnCheckChanged(bool? value)
    {
        Data.Check = value;
        await CheckChanged.InvokeAsync(Data);
    }

    private async Task OnCheckChanged(bool value)
    {
        Data.Check = value;
        await CheckChanged.InvokeAsync(Data);
    }

    private async Task OnMouseEnter()
    {
        if (HasAnimation && !_isAnimating)
        {
            _isAnimating = true;
            
            // Convert file paths to URLs
            var imageUrls = Data.AnimationPaths.Select(p => FileService.GetUrl(p)).ToArray();
            
            await JsRuntime.InvokeVoidAsync("startThumbnailAnimation", AnimationElement, imageUrls, AnimationFps);
        }
    }

    private async Task OnMouseLeave()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            await JsRuntime.InvokeVoidAsync("stopThumbnailAnimation", AnimationElement);
        }
    }

    public void Dispose()
    {
        if (_isAnimating)
        {
            // Since we can't await in Dispose, use InvokeVoidAsync without awaiting
            // The browser will still process this request
            JsRuntime.InvokeVoidAsync("stopThumbnailAnimation", AnimationElement);
            _isAnimating = false;
        }
    }
}