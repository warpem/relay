using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Services;

namespace Refund.Components.FileBrowser;

public partial class SlicePreviewJs : ComponentBase, IAsyncDisposable
{
    private readonly string _elementId = "slice-preview-" + Guid.NewGuid().ToString("N");
    private ElementReference _containerElement;
    private IJSObjectReference _module;
    private bool _initialized;
    private string _filePath;

    [Inject]
    private IJSRuntime JsRuntime { get; set; } = default!;

    [Inject]
    private FileService FileService { get; set; } = default!;

    [Parameter]
    public string FilePath { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/FileBrowser/SlicePreviewJs.razor.js");

            string fileUrl = null;
            if (!string.IsNullOrEmpty(FilePath))
            {
                fileUrl = FileService.GetUrl(FilePath);
                _filePath = FilePath;
            }

            await _module.InvokeVoidAsync("initialize", _containerElement, _elementId, fileUrl);
            _initialized = true;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized) return;

        if (FilePath != _filePath)
        {
            _filePath = FilePath;
            if (!string.IsNullOrEmpty(FilePath))
            {
                var url = FileService.GetUrl(FilePath);
                await _module.InvokeVoidAsync("loadByUrl", _elementId, url);
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
    }
}
