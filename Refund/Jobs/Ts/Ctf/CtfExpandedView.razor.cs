using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.FourierSpace;
using Refund.Components.SingleAxisScatter;
using Refund.Components.ThumbnailPanel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Warp;
using Warp.Tools;
using Color = System.Drawing.Color;
using ProcessingStatus = Refund.Components.ThumbnailPanel.ProcessingStatus;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Ctf;

public partial class CtfExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private ScatterHighlightService HighlightService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<CtfExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tsThumbnailPanel;
    private ThumbnailPanel _fsThumbnailPanel;
    private FluentTabs _tabs;
    private AmplitudeSpectrumViewer _amplitudeSpectrumViewer;

    #endregion

    #region Constants

    private const string OverviewTabId = "tab-overview";
    private const string TsTabId = "tab-ts-details";
    private const string FsTabId = "tab-ctf-details";

    #endregion

    #region Tilt Series Data
    
    private ReadOnlyCtf _job;
    private List<WarpTools.MiniJsonTsItem> _processedTsItems;
    private List<ThumbnailData> _allTsThumbnails = [];
    private ThumbnailData _selectedTsThumbnail;
    private WarpTools.MiniJsonTsItem _selectedTsItem;
    
    private List<ScatterPoint> _pointsTsNTilts = [];
    
    private List<ScatterPoint> _pointsTsMinTilt = [];
    private List<ScatterPoint> _pointsTsMaxTilt = [];
    private List<List<ScatterPoint>> _pointsTsCollectionTilt = [];
    
    private List<ScatterPoint> _pointsTsMinDefocus = [];
    private List<ScatterPoint> _pointsTsMeanDefocus = [];
    private List<ScatterPoint> _pointsTsMaxDefocus = [];
    private List<List<ScatterPoint>> _pointsTsCollectionDefocus = [];
    
    private List<ScatterPoint> _pointsTsAstigmatism = [];
    
    private List<ScatterPoint> _pointsTsMinPhase = [];
    private List<ScatterPoint> _pointsTsMeanPhase = [];
    private List<ScatterPoint> _pointsTsMaxPhase = [];
    private List<List<ScatterPoint>> _pointsTsCollectionPhase = [];
    
    private List<ScatterPoint> _pointsTsCtfRes = [];
    private List<ScatterPoint> _pointsTsCtfInclination = [];
    
    private string[] _tiltStackPaths = Array.Empty<string>();

    #endregion
    
    #region Tilt image data
    
    private MicrographSet _micrographs;

    private List<ThumbnailData> _allFsThumbnails = [];
    private ThumbnailData _selectedFsThumbnail;
    private WarpTools.MiniJsonFsItem _selectedFsItem;

    private float[] _tiltAngles;
    private float[] _axisAngles;
    private float[] _axisShiftsX;
    private float[] _axisShiftsY;
    
    private List<ScatterPoint> _pointsFsTiltAngle = [];
    private List<ScatterPoint> _pointsFsDefocus = [];
    private List<ScatterPoint> _pointsFsCtfRes = [];
    private List<ScatterPoint> _pointsFsAstigmatism = [];
    private List<ScatterPoint> _pointsFsPhaseShift = [];

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
        if (job is ReadOnlyCtf ctfJob)
        {
            _job = ctfJob;
            _processedTsItems = null;
            _allTsThumbnails = [];
            _selectedTsThumbnail = null;
            _selectedTsItem = null;
            _allFsThumbnails = [];
            _selectedFsThumbnail = null;
            _selectedFsItem = null;

            var tiltSeriesSet = _job.PortsIn[Ctf.PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();
            if (tiltSeriesSet?.DataSet?.Micrographs != null)
                _micrographs = tiltSeriesSet.DataSet.Micrographs;
            
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
            if (File.Exists(_job.ResProcessedItemsJson))
            {
                _processedTsItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(await File.ReadAllTextAsync(_job.ResProcessedItemsJson));

                var thumbnails = new List<ThumbnailData>();
                var pointsNTilts = new List<ScatterPoint>();
                var pointsMinTilt = new List<ScatterPoint>();
                var pointsMaxTilt = new List<ScatterPoint>();
                var pointsMinDefocus = new List<ScatterPoint>();
                var pointsMeanDefocus = new List<ScatterPoint>();
                var pointsMaxDefocus = new List<ScatterPoint>();
                var pointsMinPhase = new List<ScatterPoint>();
                var pointsMeanPhase = new List<ScatterPoint>();
                var pointsMaxPhase = new List<ScatterPoint>();
                var pointsAstigmatism = new List<ScatterPoint>();
                var pointsCtfRes = new List<ScatterPoint>();
                var pointsCtfInclination = new List<ScatterPoint>();

                for (var i = 0; i < _processedTsItems.Count; i++)
                {
                    var item = _processedTsItems[i];

                    List<int> animationIndices = [];
                    animationIndices.AddRange(WarpHelper.ArrayOfSequence(item.TiltMoviePaths.Length / 2, item.TiltMoviePaths.Length, 1));
                    animationIndices.AddRange(WarpHelper.ArrayOfSequence(item.TiltMoviePaths.Length - 2, -1, -1));
                    animationIndices.AddRange(WarpHelper.ArrayOfSequence(1, item.TiltMoviePaths.Length / 2, 1));

                    thumbnails.Add(new ThumbnailData
                    {
                        Index = i,
                        ImagePath = _micrographs?.ToThumbnailPath(item.TiltMoviePaths[item.TiltMoviePaths.Length / 2]) ?? "",
                        AnimationPaths = _micrographs != null ? 
                            animationIndices.Select(j => _micrographs.ToThumbnailPath(item.TiltMoviePaths[j])).ToArray() : 
                            Array.Empty<string>(),
                        Status = ProcessingStatus.Processed
                    });
                    
                    #region Populate TS scatter plots
                    
                    // Using CTF-specific values
                    pointsMinDefocus.Add(new ScatterPoint
                    {
                        Value = item.MinDefocus,
                        Color = Color.IndianRed,
                        Metadata = item
                    });
                    
                    pointsMeanDefocus.Add(new ScatterPoint
                    {
                        Value = item.MeanDefocus,
                        Color = Color.YellowGreen,
                        Metadata = item
                    });
                    
                    pointsMaxDefocus.Add(new ScatterPoint
                    {
                        Value = item.MaxDefocus,
                        Color = Color.RoyalBlue,
                        Metadata = item
                    });
                    
                    pointsAstigmatism.Add(new ScatterPoint
                    {
                        Value = item.Astigmatism,
                        Color = Color.YellowGreen,
                        Metadata = item
                    });
                    
                    if (_job.PhaseEnable)
                    {
                        pointsMinPhase.Add(new ScatterPoint
                        {
                            Value = item.MinPhase,
                            Color = Color.IndianRed,
                            Metadata = item
                        });

                        pointsMeanPhase.Add(new ScatterPoint
                        {
                            Value = item.MeanPhase,
                            Color = Color.YellowGreen,
                            Metadata = item
                        });

                        pointsMaxPhase.Add(new ScatterPoint
                        {
                            Value = item.MaxPhase,
                            Color = Color.RoyalBlue,
                            Metadata = item
                        });
                    }
                    
                    pointsCtfRes.Add(new ScatterPoint
                    {
                        Value = item.CtfResolution,
                        Color = Color.YellowGreen,
                        Metadata = item
                    });
                    
                    pointsCtfInclination.Add(new ScatterPoint
                    {
                        Value = item.CtfInclination,
                        Color = Color.YellowGreen,
                        Metadata = item
                    });

                    #endregion
                }

                _allTsThumbnails = thumbnails;
                
                _pointsTsMinDefocus = pointsMinDefocus;
                _pointsTsMeanDefocus = pointsMeanDefocus;
                _pointsTsMaxDefocus = pointsMaxDefocus;
                _pointsTsAstigmatism = pointsAstigmatism;
                _pointsTsMinPhase = pointsMinPhase;
                _pointsTsMeanPhase = pointsMeanPhase;
                _pointsTsMaxPhase = pointsMaxPhase;
                _pointsTsCtfRes = pointsCtfRes;
                _pointsTsCtfInclination = pointsCtfInclination;

                _pointsTsCollectionTilt = [pointsMinTilt, pointsMaxTilt];
                _pointsTsCollectionDefocus = [pointsMinDefocus, pointsMeanDefocus, pointsMaxDefocus];
                _pointsTsCollectionPhase = [pointsMinPhase, pointsMeanPhase, pointsMaxPhase];
                
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading CTF data for job {JobId}", _job?.Id);
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

        TiltSeries ts = new(Path.Combine(_job.DirectoryPath, _selectedTsItem.Path));
        _tiltAngles = ts.Angles;

        _tiltStackPaths = _selectedTsItem.TiltMoviePaths
                                       .Select(p => _micrographs.ToAveragePath(p))
                                       .ToArray();

        var pointsFsTiltAngle = new List<ScatterPoint>();
        var pointsFsDefocus = new List<ScatterPoint>();
        var pointsFsCtfRes = new List<ScatterPoint>();
        var pointsFsAstigmatism = new List<ScatterPoint>();
        var pointsFsPhaseShift = new List<ScatterPoint>();

        // Load per-tilt data
        for (int t = 0; t < ts.NTilts; t++)
        {
            var path = _selectedTsItem.TiltMoviePaths[t];
            
            // Add to scatter plots
            pointsFsTiltAngle.Add(new ScatterPoint
            {
                Value = ts.Angles[t],
                Color = Color.YellowGreen
            });
            
            pointsFsDefocus.Add(new ScatterPoint
            {
                Value = ts.GridCTFDefocus.Values[Math.Min(ts.GridCTFDefocus.Values.Length - 1, t)],
                Color = Color.YellowGreen
            });
            
            pointsFsAstigmatism.Add(new ScatterPoint
            {
                Value = (double)Math.Abs(ts.CTF.DefocusDelta),
                Color = Color.YellowGreen
            });
            
            pointsFsPhaseShift.Add(new ScatterPoint
            {
                Value = (double)ts.CTF.PhaseShift,
                Color = Color.YellowGreen
            });
            
            pointsFsCtfRes.Add(new ScatterPoint
            {
                Value = (double)ts.CTFResolutionEstimate,
                Color = Color.YellowGreen
            });
        }

        _pointsFsTiltAngle = pointsFsTiltAngle;
        _pointsFsDefocus = pointsFsDefocus;
        _pointsFsAstigmatism = pointsFsAstigmatism;
        _pointsFsPhaseShift = pointsFsPhaseShift;
        _pointsFsCtfRes = pointsFsCtfRes;

        _zeroTiltIndex = Array.IndexOf(_tiltAngles, _tiltAngles.OrderBy(a => Math.Abs(a)).First());
        
        await InvokeAsync(StateHasChanged);
        
        await HighlightService.SetHighlight(this, -1, null);
        await _tabs.GoToTabAsync(TsTabId);
        if (scrollToItem)
            await _tsThumbnailPanel.SetSelectedThumbnailAsync(_selectedTsThumbnail);
    }
    
    #endregion
    
    #region Tilt selection

    private async Task HandleFsPointClicked(ScatterPoint point)
    {
        if (_amplitudeSpectrumViewer == null || _pointsFsTiltAngle == null || !_pointsFsTiltAngle.Any())
            return;
            
        int pointIndex = _pointsFsTiltAngle.IndexOf(point);
        if (pointIndex >= 0 && pointIndex < _tiltAngles.Length)
        {
            await _amplitudeSpectrumViewer.SetTiltIndex(pointIndex);
        }
    }
    
    #endregion
}