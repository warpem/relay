using Microsoft.AspNetCore.Components;
using Refund.Components.SingleAxisScatter;
using Refund.Services;
using Refund.Utils;

namespace Refund.Components.Jobs;

public partial class FsMiniItem : ComponentBase, IDisposable
{
    [Parameter]
    public int ThumbnailSize { get; set; }
    
    [Parameter]
    public Func<string, string> GetThumbnail { get; set; }
    
    [Inject] 
    private ScatterHighlightService HighlightService { get; set; }
    
    [Inject]
    private FileService FileService { get; set; }
    
    ScatterPoint? _item;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        HighlightService.HighlightChanged += HandleHighlightChanged;
    }

    private async Task HandleHighlightChanged(object sender, int index, ScatterPoint? item)
    {
        if (item != null && item.Value.Metadata is WarpTools.MiniJsonFsItem)
            _item = item;
        else
            _item = null;
        
        await InvokeAsync(StateHasChanged);
    }
    
    public void Dispose()
    {
        HighlightService.HighlightChanged -= HandleHighlightChanged;
    }
}