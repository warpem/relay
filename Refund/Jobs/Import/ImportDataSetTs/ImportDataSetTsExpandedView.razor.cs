using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.SingleAxisScatter;
using Refund.Components.ThumbnailPanel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Ts.DeselectTilts;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Color = System.Drawing.Color;

namespace Refund.Jobs.Import.ImportDataSetTs;

public partial class ImportDataSetTsExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private ScatterHighlightService HighlightService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<ImportDataSetTsExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tsThumbnailPanel;
    private ThumbnailPanel _fsThumbnailPanel;
    private FluentTabs _tabs;

    #endregion

    #region Constants

    private const string OverviewTabId = "tab-overview";
    private const string TsTabId = "tab-ts-details";
    private const string FsTabId = "tab-fs-details";

    #endregion

    #region Tilt Series Data
    
    private ReadOnlyImportDataSetTs _job;
    private List<WarpTools.MiniJsonTsItem> _processedTsItems;
    private List<ThumbnailData> _allTsThumbnails = new();
    private ThumbnailData _selectedTsThumbnail;
    private WarpTools.MiniJsonTsItem _selectedTsItem;
    private Warp.Star _selectedTomostar;
    
    private List<ScatterPoint> _pointsTsNTilts = new();

    #endregion

    #region Frame Series Data
    
    private MicrographSet _micrographs;
    private List<WarpTools.MiniJsonFsItem> _processedFsItems;
    private Dictionary<string, WarpTools.MiniJsonFsItem> _processedFsItemsDict;
    private List<WarpTools.MiniJsonFsItem> _seriesFsItems;
    private List<ThumbnailData> _allFsThumbnails = new();
    private ThumbnailData _selectedFsThumbnail;
    private WarpTools.MiniJsonFsItem _selectedFsItem;
    
    #endregion

    #region Scatter Plot Data
    
    private List<ScatterPoint> _pointsFsTiltAngle = new();
    private List<ScatterPoint> _pointsFsExposure = new();
    private List<ScatterPoint> _pointsFsIntensity = new();
    private List<ScatterPoint> _pointsFsDefocus = new();
    private List<ScatterPoint> _pointsFsCtfRes = new();
    private List<ScatterPoint> _pointsFsMotion = new();
    
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
        if (job is ReadOnlyImportDataSetTs importDataSetTs)
        {
            _job = importDataSetTs;
            _processedTsItems = null;
            _allTsThumbnails = new();
            _selectedTsThumbnail = null;
            _selectedTsItem = null;

            _micrographs = null;
            _processedFsItems = null;
            _allFsThumbnails = new();
            _selectedFsThumbnail = null;
            _selectedFsItem = null;
            
            _micrographs = _job.PortsIn[ImportDataSetTs.PortInMicrographs].GetSingleResource<MicrographSet>();
            _processedFsItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonFsItem>>(File.ReadAllText(_micrographs.ProcessedItemsJson));
            _processedFsItemsDict = _processedFsItems.ToDictionary(item => Warp.Tools.Helper.PathToName(item.Path), item => item);
            
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
        if (_job == null)
            return;

        try
        {
            _processedTsItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(await File.ReadAllTextAsync(_job.ResProcessedItemsJson));

            var thumbnails = new List<ThumbnailData>(_allTsThumbnails.Capacity);
            var pointsNTilts = new List<ScatterPoint>(_pointsTsNTilts.Capacity);

            for (var i = 0; i < _processedTsItems.Count; i++)
            {
                var item = _processedTsItems[i];

                List<int> animationIndices = new();
                animationIndices.AddRange(Warp.Tools.Helper.ArrayOfSequence(item.TiltMoviePaths.Length / 2, item.TiltMoviePaths.Length, 1));
                animationIndices.AddRange(Warp.Tools.Helper.ArrayOfSequence(item.TiltMoviePaths.Length - 2, -1, -1));
                animationIndices.AddRange(Warp.Tools.Helper.ArrayOfSequence(1, item.TiltMoviePaths.Length / 2, 1));

                thumbnails.Add(new ThumbnailData
                {
                    Index = i,
                    ImagePath = _micrographs.ToThumbnailPath(item.TiltMoviePaths[item.TiltMoviePaths.Length / 2]),
                    AnimationPaths = animationIndices.Select(j => _micrographs.ToThumbnailPath(item.TiltMoviePaths[j])).ToArray(),
                    Status = ProcessingStatus.Processed,
                    Check = !_job.DeselectedTiltSeries.Contains(Warp.Tools.Helper.PathToName(item.Path))
                });

                pointsNTilts.Add(new ScatterPoint
                {
                    Value = item.TiltMoviePaths.Length,
                    Color = Color.YellowGreen,
                    Metadata = item
                });
            }
            
            PopulateSeriesFsThumbnails();

            _allTsThumbnails = thumbnails;
            _pointsTsNTilts = pointsNTilts;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading import data set TS data for job {JobId}", _job?.Id);
        }
    }

    #endregion
    
    #region Tilt Series Selection

    private async Task SelectedTsThumbnailChanged(ThumbnailData data)
    {
        if (!_allTsThumbnails.Contains(data))
            return;

        int index = _allTsThumbnails.IndexOf(data);
        await SelectTsItem(index, false);
    }

    private async Task HandleTsPointClicked(ScatterPoint point)
    {
        var metadata = point.Metadata as WarpTools.MiniJsonTsItem;
        
        if (!_processedTsItems.Contains(metadata))
            return;
        
        int index = _processedTsItems.IndexOf(metadata);
        await SelectTsItem(index, true);
    }

    private async Task SelectTsItem(int index, bool scrollToItem)
    {
        _selectedTsThumbnail = _allTsThumbnails[index];
        _selectedTsItem = _processedTsItems[index];
        
        _selectedTomostar = new(Warp.Tools.Helper.PathCombine(_job.DirectoryPath, _selectedTsItem.Path));

        _seriesFsItems = _selectedTsItem.TiltMoviePaths.Select(p => _processedFsItemsDict[Warp.Tools.Helper.PathToName(p)]).ToList();

        PopulateSeriesFsThumbnails();
        
        await PopulateFsScatterPlots();
        
        _selectedFsItem = null;
        _selectedFsThumbnail = null;
        
        await InvokeAsync(StateHasChanged);
        
        await HighlightService.SetHighlight(this, -1, null);
        await _tabs.GoToTabAsync(TsTabId);
        if (scrollToItem)
            await _tsThumbnailPanel.SetSelectedThumbnailAsync(_selectedTsThumbnail);
    }

    private void PopulateSeriesFsThumbnails()
    {
        if (_seriesFsItems != null && _seriesFsItems.Any() && _job != null)
            _allFsThumbnails = _seriesFsItems.Select((item, i) => new ThumbnailData
            {
                Index = i,
                Label2 = _selectedTomostar.GetRowValueFloat(i, "wrpAngleTilt").ToString("F1") + "\u2009°",
                ImagePath = _micrographs.ToThumbnailPath(item.Path),
                Status = ProcessingStatus.Processed,
                Check = !_job.DeselectedTilts.Contains(Warp.Tools.Helper.PathToName(item.Path))
            }).ToList();
    }

    private async Task TsThumbnailCheckChanged(ThumbnailData data)
    {
        if (data.Check == null || _job == null)
            return;
        
        var name = Warp.Tools.Helper.PathToName(_processedTsItems[data.Index].Path);
        
        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            ImportDataSetTs job = (ImportDataSetTs)originalJob;
            if (data.Check.Value)
                job.DeselectedTiltSeries.Remove(name);
            else
                job.DeselectedTiltSeries.Add(name);
        });
    }
    
    #endregion
    
    #region Frame Series Selection

    private async Task SelectedFsThumbnailChanged(ThumbnailData data)
    {
        if (!_allFsThumbnails.Contains(data))
            return;

        int index = _allFsThumbnails.IndexOf(data);
        await SelectFsItem(index, false);
    }

    private async Task HandleFsPointClicked(ScatterPoint point)
    {
        var metadata = point.Metadata as WarpTools.MiniJsonFsItem;
        
        if (!_processedFsItems.Contains(metadata))
            return;
        
        int index = _processedFsItems.IndexOf(metadata);
        await SelectFsItem(index, true);
    }

    private async Task SelectFsItem(int index, bool scrollToItem)
    {
        _selectedFsThumbnail = _allFsThumbnails[index];
        _selectedFsItem = _seriesFsItems[index];
        
        await InvokeAsync(StateHasChanged);
        
        await HighlightService.SetHighlight(this, -1, null);
        await _tabs.GoToTabAsync(FsTabId);
        if (scrollToItem)
            await _tsThumbnailPanel.SetSelectedThumbnailAsync(_selectedTsThumbnail);
    }

    private async Task FsThumbnailCheckChanged(ThumbnailData data)
    {
        if (data.Check == null || _job == null)
            return;
        
        var name = Warp.Tools.Helper.PathToName(_seriesFsItems[data.Index].Path);
        
        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            ImportDataSetTs job = (ImportDataSetTs)originalJob;
            if (data.Check.Value)
                job.DeselectedTilts.Remove(name);
            else
                job.DeselectedTilts.Add(name);
        });
    }
    
    #endregion

    #region Scatter Plot Helpers
    
    private async Task PopulateFsScatterPlots()
    {
        float maxIntensity = _selectedTomostar.GetFloat("wrpAverageIntensity").Max();
        
        _pointsFsTiltAngle = _seriesFsItems.Select((item, i) => new ScatterPoint
        {
            Metadata = item, 
            Value = _selectedTomostar.GetRowValueFloat(i, "wrpAngleTilt"), 
            Color = Color.YellowGreen
        }).ToList();
        
        _pointsFsExposure = _seriesFsItems.Select((item, i) => new ScatterPoint
        {
            Metadata = item, 
            Value = _selectedTomostar.GetRowValueFloat(i, "wrpDose"),
            Color = Color.YellowGreen
        }).ToList();
        
        _pointsFsIntensity = _seriesFsItems.Select((item, i) => new ScatterPoint
        {
            Metadata = item, 
            Value = _selectedTomostar.GetRowValueFloat(i, "wrpAverageIntensity") / maxIntensity * 100, 
            Color = Color.YellowGreen
        }).ToList();

        _pointsFsDefocus = _seriesFsItems.Select(item => new ScatterPoint
        {
            Metadata = item, 
            Value = item.Defocus, 
            Color = Color.YellowGreen
        }).ToList();
        
        _pointsFsCtfRes = _seriesFsItems.Select(item => new ScatterPoint
        {
            Metadata = item, 
            Value = item.Resolution, 
            Color = Color.YellowGreen
        }).ToList();
        
        _pointsFsMotion = _seriesFsItems.Select(item => new ScatterPoint
        {
            Metadata = item, 
            Value = item.Motion, 
            Color = Color.YellowGreen
        }).ToList();
    }
    
    #endregion
    
    #region Selection export

    private async Task ClearAllDeselectedTiltSeries()
    {
        await DataManager.UpdateJob(Session.User, _job, 
                                    originalJob => ((ImportDataSetTs)originalJob).DeselectedTiltSeries.Clear());
    }
    
    private async Task ClearAllDeselectedTilts()
    {
        await DataManager.UpdateJob(Session.User, _job, 
                                    originalJob => ((ImportDataSetTs)originalJob).DeselectedTilts.Clear());
    }

    private async Task ClearDeselectedTilts()
    {
        await DataManager.UpdateJob(Session.User, _job,
                                    originalJob =>
                                    {
                                        var names = _selectedTsItem.TiltMoviePaths.Select(Warp.Tools.Helper.PathToName);
                                        ((ImportDataSetTs)originalJob).DeselectedTilts.RemoveWhere(name => names.Contains(name));
                                    });
    }

    private async Task ExportSelection()
    {
        var view = Session.View ?? _job.Space.Views.FirstOrDefault(v => v.Jobs.Contains(_job));
        if (view == null)
            throw new Exception("No suitable view found for new job");

        var template = new DeselectTilts();
        template.DeselectedTiltSeries = new(_job.DeselectedTiltSeries);
        template.DeselectedTilts = new(_job.DeselectedTilts);
        
        var createdJob = await DataManager.CreateJob(Session.User, view, template.TypeGuid, template);
        if (createdJob == null)
            throw new Exception("Failed to export selection");

        await DataManager.CreateEdge(_job.Space, 
                                     _job.PortsOut[ImportDataSetTs.PortOutDataSetTs], 
                                     createdJob.PortsIn[DeselectTilts.PortInDataSetTs]);

        await DataManager.QueueLocalJob(Session.User, createdJob);

        ToastService.ShowSuccess($"Exported tilt selection from {_job.QualifiedName}");
    }
    
    #endregion
}