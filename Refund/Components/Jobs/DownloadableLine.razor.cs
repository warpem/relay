using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.Services;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Components.Jobs;

/// <summary>
/// Component that renders a clickable line item for a downloadable file resource.
/// This provides a consistent UI pattern for file downloads across the application.
/// </summary>
public partial class DownloadableLine : ComponentBase
{
    /// <summary>
    /// Icon used for the download button
    /// </summary>
    private readonly Icon _downloadIcon = new Icons.Regular.Size16.CloudArrowDown();

    private bool _downloading;

    /// <summary>
    /// The downloadable resource to display and provide access to
    /// </summary>
    [Parameter]
    public Downloadable Downloadable { get; set; }
    
    /// <summary>
    /// Font size for the line item text
    /// </summary>
    [Parameter]
    public string FontSize { get; set; } = "0.75rem";
    
    /// <summary>
    /// Service for handling secure file access
    /// </summary>
    [Inject]
    private FileService FileService { get; set; }
    
    /// <summary>
    /// JavaScript runtime for browser interactions
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; }
    
    /// <summary>
    /// Handles the click event to initiate the file download by:
    /// 1. Getting a secure URL for the file path
    /// 2. Triggering the browser download action via JavaScript
    /// </summary>
    private async Task HandleClick()
    {
        if (_downloading || string.IsNullOrEmpty(Downloadable.ServerPath))
            return;

        _downloading = true;
        StateHasChanged();

        try
        {
            string fileUrl = FileService.GetUrl(Downloadable.ServerPath);
            await JSRuntime.InvokeVoidAsync("downloadFile", fileUrl, Path.GetFileName(Downloadable.ServerPath));
        }
        finally
        {
            _downloading = false;
            StateHasChanged();
        }
    }
}