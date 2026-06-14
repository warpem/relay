using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.ThumbnailPanel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using ProcessingStatus = Refund.Components.ThumbnailPanel.ProcessingStatus;

namespace Refund.Jobs.Ts.Reconstruction.Denoising;

public partial class DenoisingExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<DenoisingExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tomogramThumbnailPanel;

    #endregion

    #region Tomogram Data
    
    private ReadOnlyDenoising _job;
    private TomogramSet _tomogramSet;
    private List<WarpTools.MiniJsonTsItem> _processedTomograms = [];
    private List<ThumbnailData> _allTomogramThumbnails = [];
    private ThumbnailData _selectedTomogramThumbnail;
    private string _selectedTomogramPath;
    
    #endregion

    #region Lifecycle Methods

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        ExpandedViewService.OnJobChanged += HandleJobChanged;
        ExpandedViewService.OnJobUpdated += HandleJobUpdated;
        await HandleJobChanged(ExpandedViewService.CurrentJob);
    }

    public async ValueTask DisposeAsync()
    {
        ExpandedViewService.OnJobChanged -= HandleJobChanged;
        ExpandedViewService.OnJobUpdated -= HandleJobUpdated;
    }

    #endregion

    #region Job Event Handlers

    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        if (job is ReadOnlyDenoising denoising)
        {
            _job = denoising;
            _processedTomograms = [];
            _allTomogramThumbnails = [];
            _selectedTomogramThumbnail = null;
            _selectedTomogramPath = null;

            // Get the tomogram resource from the job's output port
            _tomogramSet = _job.PortsOut[Denoising.PortOutTomogramSet].GetResource(0) as TomogramSet;
            
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

    #endregion
    
    #region Data Loading

    private async Task LoadData()
    {
        if (_job == null || _tomogramSet == null)
            return;

        try
        {
            // Load the list of processed tomograms from the JSON file
            if (File.Exists(_job.ResProcessedItemsJson))
            {
                var json = await File.ReadAllTextAsync(_job.ResProcessedItemsJson);
                _processedTomograms = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(json);
            }

            var thumbnails = new List<ThumbnailData>();
            
            for (var i = 0; i < _processedTomograms.Count; i++)
            {
                var tomogramName = _processedTomograms[i].Path;
                
                thumbnails.Add(new ThumbnailData
                {
                    Index = i,
                    ImagePath = _tomogramSet.ToTomogramThumbnailPath(tomogramName),
                    Status = ProcessingStatus.Processed
                });
            }

            _allTomogramThumbnails = thumbnails;
            
            // Select the first tomogram if available and none is currently selected
            if (_allTomogramThumbnails.Count > 0 && _selectedTomogramThumbnail == null)
            {
                await SelectTomogram(0, false);
            }
            
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading tomogram data for job {JobId}", _job?.Id);
            ToastService.ShowError($"Error loading tomogram data: {ex.Message}");
        }
    }

    #endregion
    
    #region Tomogram Selection

    private async Task SelectedTomogramThumbnailChanged(ThumbnailData data)
    {
        if (!_allTomogramThumbnails.Contains(data))
            return;

        int index = _allTomogramThumbnails.IndexOf(data);
        await SelectTomogram(index, false);
    }

    private async Task SelectTomogram(int index, bool scrollToItem)
    {
        if (index < 0 || index >= _processedTomograms.Count)
            return;
            
        _selectedTomogramThumbnail = _allTomogramThumbnails[index];
        var tomogramName = _processedTomograms[index].Path;
        
        // Set the full path to the tomogram file
        _selectedTomogramPath = _tomogramSet.ToTomogramDenoisedPath(tomogramName);

        if (scrollToItem && _tomogramThumbnailPanel != null)
            await _tomogramThumbnailPanel.SetSelectedThumbnailAsync(_selectedTomogramThumbnail);
        
        await InvokeAsync(StateHasChanged);
    }

    #endregion
}