using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.ThumbnailPanel;
using Refund.Components.SingleAxisScatter;
using Refund.Components.TomogramViewer;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Warp;
using Warp.Headers;
using Warp.Tools;
using ProcessingStatus = Refund.Components.ThumbnailPanel.ProcessingStatus;
using Color = System.Drawing.Color;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Extraction.ExtractParticles;

public partial class ExtractParticlesExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject]
    private RelaySession Session { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private ExpandedJobViewService ExpandedViewService { get; set; }

    [Inject]
    private IToastService ToastService { get; set; }

    [Inject]
    private ILogger<ExtractParticlesExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tomogramThumbnailPanel;
    private FluentTabs _tabs;
    private const string OverviewTabId = "tab-overview";
    private const string DetailsTabId = "tab-details";

    #endregion

    #region Tomogram data

    private ReadOnlyExtractParticles _job;
    private TiltSeriesSet _tiltSeriesSet;
    private TomogramSet _tomogramSet;
    private ParticleSet _particleSet;
    private List<WarpTools.MiniJsonTsItem> _processedTomograms = [];
    private List<ThumbnailData> _allTomogramThumbnails = [];
    private ThumbnailData _selectedTomogramThumbnail;
    private string _selectedTomogramPath;
    private string _selectedParticleStarPath;
    private List<Particle> _particles = [];

    private Dictionary<string, List<Particle>> _preloadedParticles = new();
    private int3 _tomoDims;

    private List<ScatterPoint> _pointsParticleCount = [];

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
        if (job is ReadOnlyExtractParticles extractParticles)
        {
            _job = extractParticles;
            _processedTomograms = [];
            _allTomogramThumbnails = [];
            _selectedTomogramThumbnail = null;
            _selectedTomogramPath = null;
            _selectedParticleStarPath = null;

            // Get the tomogram resource from the job's input port
            _tiltSeriesSet = _job.PortsIn[ExtractParticles.PortInTiltSeries].GetSingleResource<TiltSeriesSet>();

            // Get the position set resource from the output port
            _particleSet = _job.PortsIn[ExtractParticles.PortInParticleSet].GetSingleResource<ParticleSet>();

            // Get the tomogram set resource from the output port
            _tomogramSet = _particleSet.PickedInTomograms;

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

    #region Data loading

    private async Task LoadData()
    {
        if (_job == null)
            return;

        var thumbnails = new List<ThumbnailData>();
        var pointsParticleCount = new List<ScatterPoint>();

        try
        {
            _processedTomograms = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(await File.ReadAllTextAsync(_job.ResProcessedItemsJson));

            thumbnails = _processedTomograms.Select((data, i) => new ThumbnailData
            {
                Index = i,
                ImagePath = _tomogramSet.ToTomogramThumbnailPath(data.Path),
                Status = ProcessingStatus.Processed
            }).ToList();

            string firstExistingTomo = _processedTomograms.Select(t => _tomogramSet.ToTomogramPath(t.Path))
                                                          .FirstOrDefault(File.Exists);

            _tomoDims = firstExistingTomo == null ? new int3(1) : MapHeader.ReadFromFile(firstExistingTomo).Dimensions;

            pointsParticleCount = _processedTomograms.Select(data => new ScatterPoint()
            {
                Value = data.ParticleCount,
                Color = Color.YellowGreen,
                Metadata = data
            }).ToList();

            if (_particleSet.IsSingleStar)
            {
                string tableName = Star.IsMultiTable(_particleSet.ParticlesSingleStarPath) ? "particles" : null;
                var tablesIn = Star.LoadSplitByValue(_particleSet.ParticlesSingleStarPath, "rlnTomoName", tableName);

                foreach (var kvp in tablesIn)
                    _preloadedParticles[kvp.Key] = await LoadParticlesFromStarFileAsync(kvp.Value);
            }
            else
            {
                foreach (var item in _processedTomograms)
                {
                    var tableIn = new Star(_particleSet.ToMultiStarPath(item.Path));
                    _preloadedParticles[item.Path] = await LoadParticlesFromStarFileAsync(tableIn);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading tomogram data");
            ToastService.ShowError($"Error loading tomogram data: {ex.Message}");
        }

        _allTomogramThumbnails = thumbnails;
        _pointsParticleCount = pointsParticleCount;

        await InvokeAsync(StateHasChanged);
    }

    private async Task<List<Particle>> LoadParticlesFromStarFileAsync(Star starFile)
    {
        try
        {
            // Check which columns are available
            bool hasCoords = starFile.HasColumn("rlnCoordinateX") &&
                             starFile.HasColumn("rlnCoordinateY") &&
                             starFile.HasColumn("rlnCoordinateZ");

            bool hasAngles = starFile.HasColumn("rlnAngleRot") &&
                             starFile.HasColumn("rlnAngleTilt") &&
                             starFile.HasColumn("rlnAnglePsi");

            bool hasScores = starFile.HasColumn("rlnAutopickFigureOfMerit");

            if (!hasCoords)
            {
                Logger.LogWarning("STAR file does not contain coordinates");
                return [];
            }

            // Get the data from the STAR file
            float3[] coordinates = starFile.GetRelionCoordinates();
            float3[] angles = hasAngles ? starFile.GetRelionAngles() : null;
            float[] scores = hasScores ? starFile.GetFloatRobust("rlnAutopickFigureOfMerit") : null;

            // Check if coordinates are normalized (0-1 range)
            bool areNormalized = _particleSet.HasNormalizedCoords;

            // Get tomogram dimensions for scaling normalized coordinates
            var fVolDims = new float3(_tomoDims);

            // Convert to our Particle format
            var particles = new List<Particle>();

            for (int i = 0; i < coordinates.Length; i++)
            {
                var position = coordinates[i];

                // Scale normalized coordinates if needed
                if (areNormalized)
                    position *= fVolDims;
                else
                    position = position * (float)_particleSet.CoordPixelSize / (float)_tomogramSet.PixelSize;

                var angle = hasAngles && i < angles.Length ? angles[i] : new float3(0, 0, 0);
                var score = hasScores && i < scores.Length ? scores[i] : 0f;

                particles.Add(new(position, angle, score));
            }

            return particles;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading particles from STAR file");
            return [];
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

    private async Task OnScatterPointClicked(ScatterPoint point)
    {
        if (point.Metadata is WarpTools.MiniJsonTsItem metadata)
        {
            if (!_processedTomograms.Contains(metadata))
                return;
            
            int index = _processedTomograms.IndexOf(metadata);
            await SelectTomogram(index, true);
        }
    }

    private async Task SelectTomogram(int index, bool scrollToItem)
    {
        if (index < 0 || index >= _processedTomograms.Count)
            return;

        _selectedTomogramThumbnail = _allTomogramThumbnails[index];
        var tomogramName = _processedTomograms[index].Path;

        // Set the full path to the tomogram file
        _selectedTomogramPath = _tomogramSet.ToTomogramPath(tomogramName);

        // If a deconvolved version exists and is preferred, use that instead
        if (_tomogramSet.HasDeconvolution && _tomogramSet.HasDeconvTomograms)
        {
            var deconvPath = _tomogramSet.ToTomogramDeconvPath(tomogramName);

            if (File.Exists(deconvPath))
            {
                _selectedTomogramPath = deconvPath;
            }
        }

        // Get particles for the selected tomogram from our already loaded data
        _particles = _preloadedParticles.ContainsKey(tomogramName) ? _preloadedParticles[tomogramName] : [];

        if (scrollToItem && _tomogramThumbnailPanel != null)
            await _tomogramThumbnailPanel.SetSelectedThumbnailAsync(_selectedTomogramThumbnail);

        // Navigate to details tab
        if (_tabs != null)
            await _tabs.GoToTabAsync(DetailsTabId);

        await InvokeAsync(StateHasChanged);
    }

    #endregion
}