using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.SingleAxisScatter;
using Refund.Components.ThumbnailPanel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Warp;
using Color = System.Drawing.Color;
using ProcessingStatus = Refund.Components.ThumbnailPanel.ProcessingStatus;

namespace Refund.Jobs.Ts.Alignment.AlignEtomo;

public partial class AlignEtomoExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private ScatterHighlightService HighlightService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<AlignEtomoExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tsThumbnailPanel;
    private FluentTabs _tabs;

    #endregion

    #region Constants

    private const string OverviewTabId = "tab-overview";
    private const string TsTabId = "tab-ts-details";

    #endregion

    #region Tilt Series Data
    
    private ReadOnlyAlignEtomo _job;
    private List<WarpTools.MiniJsonTsItem> _processedTsItems;
    private List<ThumbnailData> _allTsThumbnails = [];
    private ThumbnailData _selectedTsThumbnail;
    private WarpTools.MiniJsonTsItem _selectedTsItem;
    
    private List<ScatterPoint> _pointsTsNTilts = [];
    
    private List<ScatterPoint> _pointsTsMinTilt = [];
    private List<ScatterPoint> _pointsTsMaxTilt = [];
    private List<List<ScatterPoint>> _pointsTsCollectionTilt = [];
    
    private List<ScatterPoint> _pointsTsMinAxis = [];
    private List<ScatterPoint> _pointsTsMeanAxis = [];
    private List<ScatterPoint> _pointsTsMaxAxis = [];
    private List<List<ScatterPoint>> _pointsTsCollectionAxis = [];
    
    private List<ScatterPoint> _pointsTsMinShiftX = [];
    private List<ScatterPoint> _pointsTsMeanShiftX = [];
    private List<ScatterPoint> _pointsTsMaxShiftX = [];
    private List<List<ScatterPoint>> _pointsTsCollectionShiftX = [];
    
    private List<ScatterPoint> _pointsTsMinShiftY = [];
    private List<ScatterPoint> _pointsTsMeanShiftY = [];
    private List<ScatterPoint> _pointsTsMaxShiftY = [];
    private List<List<ScatterPoint>> _pointsTsCollectionShiftY = [];

    #endregion
    
    #region Tilt image data
    
    private MicrographSet _micrographs;

    private string[] _micrographThumbnailPaths;

    private float[] _tiltAngles;
    private float[] _axisAngles;
    private float[] _axisShiftsX;
    private float[] _axisShiftsY;
    
    private List<ScatterPoint> _pointsFsTiltAngle = [];
    private List<ScatterPoint> _pointsFsExposure = [];
    private List<ScatterPoint> _pointsFsAxis = [];
    private List<ScatterPoint> _pointsFsShiftX = [];
    private List<ScatterPoint> _pointsFsShiftY = [];
    private List<ScatterPoint> _pointsFsFov = [];

    private int _zeroTiltIndex;
    
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
        if (job is ReadOnlyAlignEtomo importDataSetTs)
        {
            _job = importDataSetTs;
            _processedTsItems = null;
            _allTsThumbnails = [];
            _selectedTsThumbnail = null;
            _selectedTsItem = null;

            _micrographs = _job.PortsIn[AlignEtomo.PortInDataSetTs].GetSingleResource<DataSetTs>().Micrographs;
            
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
        if (_job == null || !File.Exists(_job.ResProcessedItemsJson))
            return;

        try
        {
            _processedTsItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(await File.ReadAllTextAsync(_job.ResProcessedItemsJson));

            var thumbnails = new List<ThumbnailData>(_allTsThumbnails.Capacity);
            var pointsNTilts = new List<ScatterPoint>(_pointsTsNTilts.Capacity);
            var pointsMinTilt = new List<ScatterPoint>(_pointsTsMinTilt.Capacity);
            var pointsMaxTilt = new List<ScatterPoint>(_pointsTsMaxTilt.Capacity);
            var pointsMinAxis = new List<ScatterPoint>(_pointsTsMinAxis.Capacity);
            var pointsMeanAxis = new List<ScatterPoint>(_pointsTsMeanAxis.Capacity);
            var pointsMaxAxis = new List<ScatterPoint>(_pointsTsMaxAxis.Capacity);
            var pointsMinShiftX = new List<ScatterPoint>(_pointsTsMinShiftX.Capacity);
            var pointsMeanShiftX = new List<ScatterPoint>(_pointsTsMeanShiftX.Capacity);
            var pointsMaxShiftX = new List<ScatterPoint>(_pointsTsMaxShiftX.Capacity);
            var pointsMinShiftY = new List<ScatterPoint>(_pointsTsMinShiftY.Capacity);
            var pointsMeanShiftY = new List<ScatterPoint>(_pointsTsMeanShiftY.Capacity);
            var pointsMaxShiftY = new List<ScatterPoint>(_pointsTsMaxShiftY.Capacity);

            for (var i = 0; i < _processedTsItems.Count; i++)
            {
                var item = _processedTsItems[i];

                List<int> animationIndices = [];
                animationIndices.AddRange(Warp.Tools.Helper.ArrayOfSequence(item.TiltMoviePaths.Length / 2, item.TiltMoviePaths.Length, 1));
                animationIndices.AddRange(Warp.Tools.Helper.ArrayOfSequence(item.TiltMoviePaths.Length - 2, -1, -1));
                animationIndices.AddRange(Warp.Tools.Helper.ArrayOfSequence(1, item.TiltMoviePaths.Length / 2, 1));

                thumbnails.Add(new ThumbnailData
                {
                    Index = i,
                    ImagePath = _micrographs.ToThumbnailPath(item.TiltMoviePaths[item.TiltMoviePaths.Length / 2]),
                    AnimationPaths = animationIndices.Select(j => _micrographs.ToThumbnailPath(item.TiltMoviePaths[j])).ToArray(),
                    Status = ProcessingStatus.Processed
                });
                
                #region Populate TS scatter plots

                pointsNTilts.Add(new ScatterPoint
                {
                    Value = item.TiltMoviePaths.Length,
                    Color = Color.YellowGreen,
                    Metadata = item
                });

                pointsMinTilt.Add(new ScatterPoint
                {
                    Value = item.MinTilt,
                    Color = Color.IndianRed,
                    Metadata = item
                });
                
                pointsMaxTilt.Add(new ScatterPoint
                {
                    Value = item.MaxTilt,
                    Color = Color.RoyalBlue,
                    Metadata = item
                });
                
                pointsMinAxis.Add(new ScatterPoint
                {
                    Value = item.MinAxis,
                    Color = Color.IndianRed,
                    Metadata = item
                });
                
                pointsMeanAxis.Add(new ScatterPoint
                {
                    Value = item.MeanAxis,
                    Color = Color.YellowGreen,
                    Metadata = item
                });
                
                pointsMaxAxis.Add(new ScatterPoint
                {
                    Value = item.MaxAxis,
                    Color = Color.RoyalBlue,
                    Metadata = item
                });
                
                pointsMinShiftX.Add(new ScatterPoint
                {
                    Value = item.MinShiftX,
                    Color = Color.IndianRed,
                    Metadata = item
                });
                
                pointsMeanShiftX.Add(new ScatterPoint
                {
                    Value = item.MeanShiftX,
                    Color = Color.YellowGreen,
                    Metadata = item
                });
                
                pointsMaxShiftX.Add(new ScatterPoint
                {
                    Value = item.MaxShiftX,
                    Color = Color.RoyalBlue,
                    Metadata = item
                });
                
                pointsMinShiftY.Add(new ScatterPoint
                {
                    Value = item.MinShiftY,
                    Color = Color.IndianRed,
                    Metadata = item
                });

                pointsMeanShiftY.Add(new ScatterPoint
                {
                    Value = item.MeanShiftY,
                    Color = Color.YellowGreen,
                    Metadata = item
                });
                
                pointsMaxShiftY.Add(new ScatterPoint
                {
                    Value = item.MaxShiftY,
                    Color = Color.RoyalBlue,
                    Metadata = item
                });

                #endregion
            }

            _allTsThumbnails = thumbnails;
            
            _pointsTsNTilts = pointsNTilts;
            _pointsTsMinTilt = pointsMinTilt;
            _pointsTsMaxTilt = pointsMaxTilt;
            _pointsTsMinAxis = pointsMinAxis;
            _pointsTsMeanAxis = pointsMeanAxis;
            _pointsTsMaxAxis = pointsMaxAxis;
            _pointsTsMinShiftX = pointsMinShiftX;
            _pointsTsMeanShiftX = pointsMeanShiftX;
            _pointsTsMaxShiftX = pointsMaxShiftX;
            _pointsTsMinShiftY = pointsMinShiftY;
            _pointsTsMeanShiftY = pointsMeanShiftY;
            _pointsTsMaxShiftY = pointsMaxShiftY;

            _pointsTsCollectionTilt = [pointsMinTilt, pointsMaxTilt];
            
            if (Enumerable.Range(0, pointsMinAxis.Count).Any(i => pointsMinAxis[i].Value != pointsMaxAxis[i].Value))
                _pointsTsCollectionAxis = [pointsMinAxis, pointsMeanAxis, pointsMaxAxis];
            else
                _pointsTsCollectionAxis = [pointsMeanAxis];
            
            if (Enumerable.Range(0, pointsMinShiftX.Count).Any(i => pointsMinShiftX[i].Value != pointsMaxShiftX[i].Value))
                _pointsTsCollectionShiftX = [pointsMinShiftX, pointsMeanShiftX, pointsMaxShiftX];
            else
                _pointsTsCollectionShiftX = [pointsMeanShiftX];
            
            if (Enumerable.Range(0, pointsMinShiftY.Count).Any(i => pointsMinShiftY[i].Value != pointsMaxShiftY[i].Value))
                _pointsTsCollectionShiftY = [pointsMinShiftY, pointsMeanShiftY, pointsMaxShiftY];
            else
                _pointsTsCollectionShiftY = [pointsMeanShiftY];
            
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading data for job {JobId}", _job?.Id);
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
        
        var tsSet = (TiltSeriesSet)_job.PortsOut[AlignEtomo.PortOutDataSetTs].GetResource();

        TiltSeries ts = new(Path.Combine(_job.DirectoryPath, _selectedTsItem.Path));
        _tiltAngles = ts.Angles;
        _axisAngles = ts.TiltAxisAngles;
        _axisShiftsX = ts.TiltAxisOffsetX.Select(v => v / (float)_job.AngPix).ToArray();
        _axisShiftsY = ts.TiltAxisOffsetY.Select(v => v / (float)_job.AngPix).ToArray();

        _micrographThumbnailPaths = _selectedTsItem.TiltMoviePaths
                                                   .Select(p => tsSet.ToTiltStackThumbnailPath(_selectedTsItem.Path, p))
                                                   .ToArray();

        _pointsFsTiltAngle = ts.Angles.Select(a => new ScatterPoint
        {
            Value = a,
            Color = Color.YellowGreen,
            Metadata = null
        }).ToList();

        _pointsFsExposure = ts.Dose.Select(a => new ScatterPoint
        {
            Value = a,
            Color = Color.YellowGreen,
            Metadata = null
        }).ToList();
        
        _pointsFsAxis = ts.TiltAxisAngles.Select(a => new ScatterPoint
        {
            Value = a,
            Color = Color.YellowGreen,
            Metadata = null
        }).ToList();
        
        _pointsFsShiftX = ts.TiltAxisOffsetX.Select(a => new ScatterPoint
        {
            Value = a / (float)_job.AngPix,
            Color = Color.YellowGreen,
            Metadata = null
        }).ToList();
        
        _pointsFsShiftY = ts.TiltAxisOffsetY.Select(a => new ScatterPoint
        {
            Value = a / (float)_job.AngPix,
            Color = Color.YellowGreen,
            Metadata = null
        }).ToList();
        
        _pointsFsFov = ts.FOVFraction.Select(a => new ScatterPoint
        {
            Value = a * 100,
            Color = Color.YellowGreen,
            Metadata = null
        }).ToList();

        _zeroTiltIndex = ts.IndicesSortedDose.First();
        
        await HighlightService.SetHighlight(this, -1, null);
        await _tabs.GoToTabAsync(TsTabId);
        if (scrollToItem)
            await _tsThumbnailPanel.SetSelectedThumbnailAsync(_selectedTsThumbnail);
        
        await InvokeAsync(StateHasChanged);
    }

    private async Task TsThumbnailCheckChanged(ThumbnailData data)
    {
        if (data.Check == null || _job == null)
            return;
        
        var name = Warp.Tools.Helper.PathToName(_processedTsItems[data.Index].Path);
        
        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            
        });
    }
    
    #endregion
    
    #region Selection export

    // private async Task ClearAllDeselectedTiltSeries()
    // {
    //     await DataManager.UpdateJob(Session.User, _job, 
    //                                 originalJob => ((ImportDataSetTs)originalJob).DeselectedTiltSeries.Clear());
    // }
    //
    // private async Task ClearAllDeselectedTilts()
    // {
    //     await DataManager.UpdateJob(Session.User, _job, 
    //                                 originalJob => ((ImportDataSetTs)originalJob).DeselectedTilts.Clear());
    // }
    //
    // private async Task ClearDeselectedTilts()
    // {
    //     await DataManager.UpdateJob(Session.User, _job,
    //                                 originalJob =>
    //                                 {
    //                                     var names = _selectedTsItem.TiltMoviePaths.Select(Warp.Tools.Helper.PathToName);
    //                                     ((ImportDataSetTs)originalJob).DeselectedTilts.RemoveWhere(name => names.Contains(name));
    //                                 });
    // }
    //
    // private async Task ExportSelection()
    // {
    //     var view = Session.View ?? _job.Space.Views.FirstOrDefault(v => v.Jobs.Contains(_job));
    //     if (view == null)
    //         throw new Exception("No suitable view found for new job");
    //
    //     var template = new DeselectTilts();
    //     template.DeselectedTiltSeries = new(_job.DeselectedTiltSeries);
    //     template.DeselectedTilts = new(_job.DeselectedTilts);
    //     
    //     var createdJob = await DataManager.CreateJob(Session.User, view, template.TypeCategory, template);
    //     if (createdJob == null)
    //         throw new Exception("Failed to export selection");
    //
    //     await DataManager.CreateEdge(_job.Space, 
    //                                  _job.PortsOut[ImportDataSetTs.PortOutDataSetTs], 
    //                                  createdJob.PortsIn[DeselectTilts.PortInDataSetTs]);
    //
    //     await DataManager.QueueLocalJob(Session.User, createdJob);
    //
    //     ToastService.ShowSuccess($"Exported tilt selection from {_job.QualifiedName}");
    // }
    
    #endregion
}