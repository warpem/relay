using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Services;

namespace Refund.Components.IsosurfaceViewer;

public partial class IsosurfaceViewer : IAsyncDisposable
{
    private readonly string _elementId = "isosurface-viewer-" + Guid.NewGuid().ToString("N");
    private ElementReference _containerElement;
    private DotNetObjectReference<IsosurfaceViewer> _dotNetRef;
    private IJSObjectReference _module;
    private bool _initialized;

    private string _volumePath;

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    [Inject]
    private FileService FileService { get; set; }

    [Parameter]
    public string VolumePath { get; set; }

    [Parameter]
    public int MinWidth { get; set; } = 400;

    [Parameter]
    public int MinHeight { get; set; } = 400;

    [Parameter]
    public bool ShowEulerAngles { get; set; } = true;

    [Parameter]
    public bool MiniMode { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/IsosurfaceViewer/IsosurfaceViewer.razor.js");

            string fileUrl = null;
            if (!string.IsNullOrEmpty(VolumePath))
            {
                fileUrl = FileService.GetUrl(VolumePath);
                _volumePath = VolumePath;
            }

            await _module.InvokeVoidAsync("initialize", _containerElement, _dotNetRef, fileUrl,
                new
                {
                    minWidth = MinWidth,
                    minHeight = MinHeight,
                    showEulerAngles = ShowEulerAngles,
                    miniMode = MiniMode,
                    storageKey = VolumePath
                });

            _initialized = true;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized) return;

        if (VolumePath != _volumePath)
        {
            _volumePath = VolumePath;
            if (!string.IsNullOrEmpty(VolumePath))
            {
                var url = FileService.GetUrl(VolumePath);
                await _module.InvokeVoidAsync("loadVolumeByUrl", _elementId, url, VolumePath);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module != null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _elementId);
                await _module.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors (e.g. circuit disconnected)
            }
        }

        _dotNetRef?.Dispose();
    }
}
