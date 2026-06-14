using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Refund.Components.ThumbnailPanel;
using Refund.DataModel.ReadOnly;
using Refund.Services;

namespace Refund.Jobs.FrameSeries.Extraction.ExtractParticles2D;

public partial class ExtractParticles2DExpandedView : IAsyncDisposable
{
    [Inject] private ExpandedJobViewService _expandedViewService { get; set; }
    
    private ReadOnlyExtractParticles2D _job;
    private List<ProcessedItem> _processedItems;
    private List<ThumbnailData> _allThumbnails = new();
    private ThumbnailData _selectedThumbnail;
    private string _selectedName;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        _expandedViewService.OnJobChanged += HandleJobChanged;
        _expandedViewService.OnJobUpdated += HandleJobUpdated;
        await HandleJobChanged(_expandedViewService.CurrentJob);
    }

    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlyExtractParticles2D extractParticles2D)
        {
            _job = extractParticles2D;
            _selectedThumbnail = null;
            _selectedName = null;
            await LoadData();
        }
        else
        {
            _job = null;
        }
        
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleJobUpdated()
    {
        await LoadData();
        await InvokeAsync(StateHasChanged);
    }

    private void SelectedThumbnailChanged(ThumbnailData data)
    {
        if (!_allThumbnails.Contains(data))
            return;

        _selectedThumbnail = data;
        _selectedName = _processedItems[_allThumbnails.IndexOf(data)].Path;
        StateHasChanged();
    }

    private async Task LoadData()
    {
        if (_job == null)
            return;

        try
        {
            var processedItemsJson = await File.ReadAllTextAsync(_job.ResProcessedItemsJson);
            _processedItems = JsonSerializer.Deserialize<List<ProcessedItem>>(processedItemsJson);

            var thumbnails = new List<ThumbnailData>();
            
            for (var i = 0; i < _processedItems.Count; i++)
            {
                thumbnails.Add(new ThumbnailData
                {
                    Index = i,
                    ImagePath = _job.VisThumbnail(_processedItems[i].Path),
                    Status = ProcessingStatus.Processed
                });
            }

            _allThumbnails = thumbnails;
        }
        catch { }
    }
    
    public async ValueTask DisposeAsync()
    {
        _expandedViewService.OnJobChanged -= HandleJobChanged;
        _expandedViewService.OnJobUpdated -= HandleJobUpdated;
    }
}

public class ProcessedItem
{
    public string Path { get; set; }
}