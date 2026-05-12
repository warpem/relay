using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.ThumbnailPanel;
using Refund.Components.TomogramViewer;
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

namespace Refund.Jobs.Ts.TemplateMatch;

public partial class TemplateMatchExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<TemplateMatchExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tomogramThumbnailPanel;
    private FluentTabs _tabs;
    private const string OverviewTabId = "tab-overview";
    private const string DetailsTabId = "tab-details";
    private string _currentTabId = OverviewTabId;

    #endregion

    #region Tomogram Data
    
    private ReadOnlyTemplateMatch _job;
    private TomogramSet _tomogramSet;
    private ParticleSet _particleSet;
    private List<WarpTools.MiniJsonTsItem> _processedTomograms = [];
    private List<ThumbnailData> _allTomogramThumbnails = [];
    private ThumbnailData _selectedTomogramThumbnail;
    private string _selectedTomogramPath;
    private string _selectedParticleStarPath;
    private List<Particle> _particles = [];
    private string _templateVolumePath;

    private List<ParticleSpecies> _viewerSpecies;

    private void RebuildViewerSpecies()
    {
        _viewerSpecies = _particles == null || _particles.Count == 0 ? null : new()
        {
            new ParticleSpecies
            {
                Name = "Particles",
                Particles = _particles,
                ModelVolumePath = _templateVolumePath
            }
        };
    }

    // Store particles from all tomograms
    private List<(string TomogramName, List<Particle> Particles)> _allTomogramParticles = [];
    private bool _isLoadingAllParticles = false;
    
    // Cancellation support
    private CancellationTokenSource _loadingCts;
    
    // Tomogram dimensions
    private int3 _tomoDims;
    
    // Histogram data ranges
    private (decimal Min, decimal Max) _scoreRange = (0, 1);
    private (decimal Min, decimal Max) _coordXRange = (0, 1);
    private (decimal Min, decimal Max) _coordYRange = (0, 1);
    private (decimal Min, decimal Max) _coordZRange = (0, 1);
    private (decimal Min, decimal Max) _angleXRange = (-180, 180);
    private (decimal Min, decimal Max) _angleYRange = (0, 180);
    
    // Histogram data for overview tab (all particles)
    private decimal[] _histogramBinsScore = Array.Empty<decimal>();
    private decimal[] _histogramBinsCoordX = Array.Empty<decimal>();
    private decimal[] _histogramBinsCoordY = Array.Empty<decimal>();
    private decimal[] _histogramBinsCoordZ = Array.Empty<decimal>();
    private decimal[] _histogramBinsAngleX = Array.Empty<decimal>();
    private decimal[] _histogramBinsAngleY = Array.Empty<decimal>();
    
    // Histogram data for details tab (selected tomogram only)
    private decimal[] _selectedHistogramBinsScore = Array.Empty<decimal>();
    private decimal[] _selectedHistogramBinsCoordX = Array.Empty<decimal>();
    private decimal[] _selectedHistogramBinsCoordY = Array.Empty<decimal>();
    private decimal[] _selectedHistogramBinsCoordZ = Array.Empty<decimal>();
    private decimal[] _selectedHistogramBinsAngleX = Array.Empty<decimal>();
    private decimal[] _selectedHistogramBinsAngleY = Array.Empty<decimal>();
    
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
        
        // Cancel any ongoing loading operation
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = null;
    }

    #endregion

    #region Job Event Handlers

    private async Task HandleJobChanged(ReadOnlyJob job)
    {
        // Cancel any existing loading operation
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = null;
        
        if (job is ReadOnlyTemplateMatch templateMatch)
        {
            _job = templateMatch;
            _processedTomograms = [];
            _allTomogramThumbnails = [];
            _selectedTomogramThumbnail = null;
            _selectedTomogramPath = null;
            _selectedParticleStarPath = null;
            _allTomogramParticles = [];
            _isLoadingAllParticles = false;
            _templateVolumePath = null;

            // Reset histogram data
            _histogramBinsScore = Array.Empty<decimal>();
            _histogramBinsCoordX = Array.Empty<decimal>();
            _histogramBinsCoordY = Array.Empty<decimal>();
            _histogramBinsCoordZ = Array.Empty<decimal>();
            _histogramBinsAngleX = Array.Empty<decimal>();
            _histogramBinsAngleY = Array.Empty<decimal>();
            
            _selectedHistogramBinsScore = Array.Empty<decimal>();
            _selectedHistogramBinsCoordX = Array.Empty<decimal>();
            _selectedHistogramBinsCoordY = Array.Empty<decimal>();
            _selectedHistogramBinsCoordZ = Array.Empty<decimal>();
            _selectedHistogramBinsAngleX = Array.Empty<decimal>();
            _selectedHistogramBinsAngleY = Array.Empty<decimal>();

            // Get the tomogram resource from the job's input port
            _tomogramSet = _job.PortsIn[TemplateMatch.PortInTomogramSet].GetSingleResource<TomogramSet>();
            
            // Get the position set resource from the output port
            _particleSet = _job.PortsOut[TemplateMatch.PortOutParticleSet].GetResource() as ParticleSet;
            
            // Get template path for visualization
            if (_particleSet?.CorrespondingMaps?.Maps.Any() == true)
            {
                var path = _particleSet.CorrespondingMaps
                                                  .Maps
                                                  .First()
                                                  .GetAverageOrSimilar();
                if (File.Exists(path))
                    _templateVolumePath = path;
            }

            RebuildViewerSpecies();

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

        await InvokeAsync(async () =>
        {
            StateHasChanged();

            // Restore the current tab after the component re-renders
            if (_tabs != null && !string.IsNullOrEmpty(_currentTabId))
                await _tabs.GoToTabAsync(_currentTabId);
        });
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

            // Start loading all particles for histograms
            await LoadAllParticles();

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading tomogram data");
            ToastService.ShowError($"Error loading tomogram data: {ex.Message}");
        }
    }
    
    private async Task LoadAllParticles()
    {
        if (_job == null || _tomogramSet == null || _particleSet == null || _processedTomograms.Count == 0)
            return;

        // Prevent concurrent loading operations
        if (_isLoadingAllParticles)
            return;

        // Create new cancellation token for this operation
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;
            
        _isLoadingAllParticles = true;
        await InvokeAsync(StateHasChanged);
        
        try 
        {
            _allTomogramParticles.Clear();
            
            // Find first existing tomogram to get dimensions
            string firstExistingTomo = _processedTomograms.Select(t => _tomogramSet.ToTomogramPath(t.Path))
                                                          .FirstOrDefault(p => File.Exists(p));
            _tomoDims = firstExistingTomo == null ? new int3(1) : MapHeader.ReadFromFile(firstExistingTomo).Dimensions;

            // Load particles from each tomogram
            foreach (var tomogramItem in _processedTomograms)
            {
                token.ThrowIfCancellationRequested();
                
                var particleStarPath = _particleSet.ToMultiStarPath(tomogramItem.Path);
                if (particleStarPath != null && File.Exists(particleStarPath))
                {
                    var particles = await LoadParticlesFromStarFileAsync(particleStarPath);
                    if (particles.Count > 0)
                        _allTomogramParticles.Add((tomogramItem.Path, particles));
                }
            }
            
            token.ThrowIfCancellationRequested();
            
            CalculateHistogramData(token);
            
            _isLoadingAllParticles = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // Expected when operation is canceled
            _isLoadingAllParticles = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading all particles");
            _isLoadingAllParticles = false;
            await InvokeAsync(StateHasChanged);
        }
    }
    
    private async Task<List<Particle>> LoadParticlesFromStarFileAsync(string starFilePath)
    {
        if (string.IsNullOrEmpty(starFilePath) || !File.Exists(starFilePath))
            return [];
            
        try
        {
            // Load the STAR file once and extract all the data we need
            var starFile = new Star(starFilePath);
            
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
                Logger.LogWarning("STAR file {StarFilePath} does not contain coordinates", starFilePath);
                return [];
            }
            
            // Get the data from the STAR file
            float3[] coordinates = starFile.GetRelionCoordinates();
            float3[] angles = hasAngles ? starFile.GetRelionAngles() : null;
            float[] scores = hasScores ? starFile.GetFloatRobust("rlnAutopickFigureOfMerit") : null;
            
            // Check if coordinates are normalized (0-1 range)
            bool areNormalized = coordinates.Any() && 
                                coordinates.Select(v => Math.Max(v.X, Math.Max(v.Y, v.Z))).Max() < 1.1f;
            
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
                
                var angle = hasAngles && i < angles.Length ? angles[i] : new float3(0, 0, 0);
                var score = hasScores && i < scores.Length ? scores[i] : 0f;
                
                particles.Add(new(position, angle, score));
            }
            
            return particles;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading particles from STAR file {StarFilePath}", starFilePath);
            return [];
        }
    }

    #endregion
    
    #region Histogram Calculation
    
    private void CalculateHistogramData(CancellationToken ct = default)
    {
        if (_allTomogramParticles.Count == 0)
            return;
        
        ct.ThrowIfCancellationRequested();
        
        // Get all particles into a flat list
        var allParticles = _allTomogramParticles.SelectMany(t => t.Particles).ToList();
        if (allParticles.Count == 0)
            return;
        
        ct.ThrowIfCancellationRequested();
        
        var valsScore = allParticles.Select(p => (decimal)p.Score).ToList();
        var valsCoordX = allParticles.Select(p => (decimal)p.Position.X).ToList();
        var valsCoordY = allParticles.Select(p => (decimal)p.Position.Y).ToList();
        var valsCoordZ = allParticles.Select(p => (decimal)p.Position.Z).ToList();
        var valsAngleX = allParticles.Select(p => (decimal)p.Angles.X).ToList();
        var valsAngleY = allParticles.Select(p => (decimal)p.Angles.Y).ToList();
        
        ct.ThrowIfCancellationRequested();
        
        // Calculate ranges across all particles
        _scoreRange = CalculateRange(valsScore);
        _coordXRange = (0, _tomoDims.X - 1);
        _coordYRange = (0, _tomoDims.Y - 1);
        _coordZRange = (0, _tomoDims.Z - 1);
        _angleXRange = CalculateRange(valsAngleX);
        _angleYRange = CalculateRange(valsAngleY);
        
        ct.ThrowIfCancellationRequested();
        
        // Calculate histogram bins
        _histogramBinsScore = CalculateHistogramBins(valsScore, 
                                                     _scoreRange.Min, _scoreRange.Max, 
                                                     binCount: Math.Max(1, (int)_scoreRange.Max - (int)_scoreRange.Min) * 10,
                                                     ct);
        _histogramBinsCoordX = CalculateHistogramBins(valsCoordX, 0, _tomoDims.X - 1, _tomoDims.X, ct);
        _histogramBinsCoordY = CalculateHistogramBins(valsCoordY, 0, _tomoDims.Y - 1, _tomoDims.Y, ct);
        _histogramBinsCoordZ = CalculateHistogramBins(valsCoordZ, 0, _tomoDims.Z - 1, _tomoDims.Z, ct);
        _histogramBinsAngleX = CalculateHistogramBins(valsAngleX, _angleXRange.Min, _angleXRange.Max, 
                                                      binCount: Math.Max(1, (int)_angleXRange.Max - (int)_angleXRange.Min), ct);
        _histogramBinsAngleY = CalculateHistogramBins(valsAngleY, _angleYRange.Min, _angleYRange.Max, 
                                                      binCount: Math.Max(1, (int)_angleYRange.Max - (int)_angleYRange.Min), ct);
        
        ct.ThrowIfCancellationRequested();
        
        // If tomogram selected, update its histograms
        if (_particles.Count > 0)
            UpdateSelectedTomogramHistograms();
    }
    
    private void UpdateSelectedTomogramHistograms()
    {
        if (_particles.Count == 0)
        {
            // Clear histograms if no particles
            _selectedHistogramBinsScore = new decimal[50];
            _selectedHistogramBinsCoordX = new decimal[50];
            _selectedHistogramBinsCoordY = new decimal[50];
            _selectedHistogramBinsCoordZ = new decimal[50];
            _selectedHistogramBinsAngleX = new decimal[50];
            _selectedHistogramBinsAngleY = new decimal[50];
            return;
        }
        
        // Calculate histogram bins using same bin counts as global histograms
        _selectedHistogramBinsScore = CalculateHistogramBins(_particles.Select(p => (decimal)p.Score), 
                                                             _scoreRange.Min, 
                                                             _scoreRange.Max, 
                                                             binCount: Math.Max(1, (int)_scoreRange.Max - (int)_scoreRange.Min) * 10);
        
        _selectedHistogramBinsCoordX = CalculateHistogramBins(_particles.Select(p => (decimal)p.Position.X), 
                                                              0, 
                                                              _tomoDims.X - 1, 
                                                              _tomoDims.X);
        
        _selectedHistogramBinsCoordY = CalculateHistogramBins(_particles.Select(p => (decimal)p.Position.Y), 
                                                              0, 
                                                              _tomoDims.Y - 1, 
                                                              _tomoDims.Y);
        
        _selectedHistogramBinsCoordZ = CalculateHistogramBins(_particles.Select(p => (decimal)p.Position.Z), 
                                                              0, 
                                                              _tomoDims.Z - 1, 
                                                              _tomoDims.Z);
        
        _selectedHistogramBinsAngleX = CalculateHistogramBins(_particles.Select(p => (decimal)p.Angles.X), 
                                                              _angleXRange.Min, _angleXRange.Max, 
                                                              binCount: Math.Max(1, (int)_angleXRange.Max - (int)_angleXRange.Min));
        
        _selectedHistogramBinsAngleY = CalculateHistogramBins(_particles.Select(p => (decimal)p.Angles.Y), 
                                                              _angleYRange.Min, _angleYRange.Max, 
                                                              binCount: Math.Max(1, (int)_angleYRange.Max - (int)_angleYRange.Min));
    }
    
    private (decimal Min, decimal Max) CalculateRange(IEnumerable<decimal> values)
    {
        if (!values.Any())
            return (0, 1);
            
        return (Math.Floor(values.Min()), Math.Ceiling(values.Max()));
    }
    
    private decimal[] CalculateHistogramBins(IEnumerable<decimal> values, decimal min, decimal max, int binCount, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Handle empty input
        if (!values.Any())
            return new decimal[binCount];
            
        // Handle single value or min == max
        if (min == max)
        {
            var result = new decimal[binCount];
            result[0] = values.Count();
            return result;
        }
        
        ct.ThrowIfCancellationRequested();
        
        // Initialize bins
        var bins = new decimal[binCount];
        decimal binWidth = (max - min) / binCount;
        
        // Count values in each bin
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            
            int binIndex = (int)((value - min) / binWidth);
            if (binIndex == binCount) // Handle maximum value
                binIndex = binCount - 1;
                
            if (binIndex >= 0 && binIndex < binCount)
                bins[binIndex]++;
        }
        
        return bins;
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
        _particles = [];
        var tomogramData = _allTomogramParticles.FirstOrDefault(t => t.TomogramName == tomogramName);
        if (tomogramData != default)
        {
            _particles = tomogramData.Particles;
            UpdateSelectedTomogramHistograms();
        }
        RebuildViewerSpecies();

        if (scrollToItem && _tomogramThumbnailPanel != null)
            await _tomogramThumbnailPanel.SetSelectedThumbnailAsync(_selectedTomogramThumbnail);
        
        // Navigate to details tab and update current tab state
        _currentTabId = DetailsTabId;
        if (_tabs != null)
            await _tabs.GoToTabAsync(DetailsTabId);
            
        await InvokeAsync(StateHasChanged);
    }

    #endregion
    
    #region Tab Management
    
    private void OnTabIdChanged(string id)
    {
        _currentTabId = id;
    }
    
    #endregion
}