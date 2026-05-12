using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components.ThumbnailPanel;
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
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Jobs.Ts.SelectParticles;

public partial class SelectParticlesExpandedView : IAsyncDisposable
{
    #region Dependencies

    [Inject] private RelaySession Session { get; set; }
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private ExpandedJobViewService ExpandedViewService { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private ILogger<SelectParticlesExpandedView> Logger { get; set; }

    #endregion

    #region UI Components

    private ThumbnailPanel _tomogramThumbnailPanel;
    private Icon _iconUnlocked = new Icons.Filled.Size20.LockOpen();
    private Icon _iconLocked = new Icons.Regular.Size20.LockClosed();

    #endregion

    #region Tomogram Data
    
    private ReadOnlySelectParticles _job;
    private TomogramSet _tomogramSet;
    private ParticleSet _particleSet;
    private List<WarpTools.MiniJsonTsItem> _processedTomograms = [];
    private List<ThumbnailData> _allTomogramThumbnails = [];
    private ThumbnailData _selectedTomogramThumbnail;
    private string _selectedTomogramPath;
    private string _selectedParticleStarPath;
    private List<Particle> _particles = [];
    private List<Particle> _filteredParticles = [];
    private string _selectedTomogramName;
    private string _templateVolumePath;

    private List<ParticleSpecies> _viewerSpecies;

    private void RebuildViewerSpecies()
    {
        _viewerSpecies = _filteredParticles == null || _filteredParticles.Count == 0 ? null : new()
        {
            new ParticleSpecies
            {
                Name = "Particles",
                Particles = _filteredParticles,
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
    
    // Histogram data for overview tab (all particles)
    private Dictionary<string, (decimal Min, decimal Max)> _globalHistogramRanges = new();
    private Dictionary<string, decimal[]> _globalHistogramBins = new();
    private Dictionary<string, decimal[]> _globalFilteredHistogramBins = new();
    
    // Histogram data for details tab (selected tomogram only)
    private Dictionary<string, (decimal Min, decimal Max)> _tomogramHistogramRanges = new();
    private Dictionary<string, decimal[]> _tomogramHistogramBins = new();
    private Dictionary<string, decimal[]> _tomogramFilteredHistogramBins = new();
    
    // Whether the user can edit the histograms
    private bool IsEditingDisabled => (_job?.IsInteractiveFinished ?? false) ||
                                      _job?.Status != JobStatus.Running;
    
    // Histogram labels
    private Dictionary<string, string> _histogramLabels = new()
    {
        { SelectParticles.FilterKeyScore, "Correlation score" },
        { SelectParticles.FilterKeyCoordX, "Coordinate X" },
        { SelectParticles.FilterKeyCoordY, "Coordinate Y" },
        { SelectParticles.FilterKeyCoordZ, "Coordinate Z" },
        { SelectParticles.FilterKeyAngleX, "Rotation angle (\u00b0)" },
        { SelectParticles.FilterKeyAngleY, "Tilt angle (\u00b0)" }
    };
    
    // Particle counts for filter reporting
    private Dictionary<string, (int Original, int Filtered)> _globalFilterCounts = new();
    private Dictionary<string, (int Original, int Filtered)> _tomogramFilterCounts = new();
    private (int Original, int Filtered) _globalTotalCounts = (0, 0);
    private (int Original, int Filtered) _tomogramTotalCounts = (0, 0);
    
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
        
        if (job is ReadOnlySelectParticles selectParticles)
        {
            _job = selectParticles;
            
            _processedTomograms = [];
            _allTomogramThumbnails = [];
            _selectedTomogramThumbnail = null;
            _selectedTomogramPath = null;
            _selectedParticleStarPath = null;
            _selectedTomogramName = null;
            _allTomogramParticles = [];
            _isLoadingAllParticles = false;
            _templateVolumePath = null;
            
            // Reset histogram data
            foreach (var key in SelectParticles.FilterKeys)
            {
                _globalHistogramBins[key] = Array.Empty<decimal>();
                _globalFilteredHistogramBins[key] = Array.Empty<decimal>();
                _tomogramHistogramBins[key] = Array.Empty<decimal>();
                _tomogramFilteredHistogramBins[key] = Array.Empty<decimal>();
                
                _globalHistogramRanges[key] = (0, 1);
                _tomogramHistogramRanges[key] = (0, 1);
            }

            // Get the tomogram resource from the job's input port
            _tomogramSet = _job.PortsIn[SelectParticles.PortInTomogramSet].GetSingleResource<TomogramSet>();
            
            // Get the position set resource from the input port (template matching output)
            _particleSet = _job.PortsIn[SelectParticles.PortInParticleSet].GetSingleResource<ParticleSet>();

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
            
            // Auto-select the first tomogram when data is loaded
            await Task.Delay(100); // Small delay to ensure data is loaded
            if (_processedTomograms.Count > 0)
            {
                await SelectTomogram(0, true);
            }
        }
        else
        {
            _job = null;
        }
        
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleJobUpdated()
    {
        // Only recalculate filters and histograms - don't reload particles from disk
        // The particle data is static, only filter thresholds change
        if (_allTomogramParticles.Count > 0)
        {
            var allParticles = _allTomogramParticles.SelectMany(t => t.Particles).ToList();
            UpdateGlobalFilterCounts(allParticles, default(CancellationToken));
            CalculateGlobalFilteredHistograms(allParticles, default(CancellationToken));
        }
        
        // Recalculate filtered particles for the selected tomogram
        await UpdateFilteredParticles();
        
        await InvokeAsync(StateHasChanged);
    }
    
    // Helper methods to get formatted count texts
    private string GetGlobalTotalCountsText()
    {
        return $"{_globalTotalCounts.Original} → {_globalTotalCounts.Filtered} particles";
    }
    
    private string GetTomogramTotalCountsText()
    {
        return $"{_tomogramTotalCounts.Original} → {_tomogramTotalCounts.Filtered} particles";
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
        var ct = _loadingCts.Token;
            
        _isLoadingAllParticles = true;
        await InvokeAsync(StateHasChanged);
        
        try 
        {
            _allTomogramParticles.Clear();
            
            // Find first existing tomogram to get dimensions
            string firstExistingTomo = _processedTomograms.Select(t => _tomogramSet.ToTomogramPath(t.Path))
                                                          .FirstOrDefault(p => File.Exists(p));
            _tomoDims = firstExistingTomo == null ? new int3(1) : MapHeader.ReadFromFile(firstExistingTomo).Dimensions;

            var combinedParticles = _particleSet.IsSingleStar ?
                                        Star.LoadSplitByValue(_particleSet.ParticlesSingleStarPath,
                                                              "rlnTomoName") :
                                        null;
            
            // Load particles from each tomogram
            foreach (var tomogramItem in _processedTomograms)
            {
                ct.ThrowIfCancellationRequested();

                List<Particle> particles = null;

                if (_particleSet.IsSingleStar)
                {
                    if (File.Exists(_particleSet.ParticlesSingleStarPath))
                        particles = await LoadParticlesFromStarFileAsync(combinedParticles[tomogramItem.Path]);
                }
                else
                {
                    var particleStarPath = _particleSet.ToMultiStarPath(tomogramItem.Path);
                    if (particleStarPath != null && File.Exists(particleStarPath))
                        particles = await LoadParticlesFromStarFileAsync(new Star(particleStarPath));
                }

                if (particles != null && particles.Count > 0)
                    _allTomogramParticles.Add((tomogramItem.Path, particles));
            }
            
            ct.ThrowIfCancellationRequested();
            
            CalculateHistogramData(ct);
            
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
                return new List<Particle>();
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

                // Wrap angles into expected ranges: X to [-180, 180], Y to [0, 180]
                angle.X = ((angle.X % 360f) + 540f) % 360f - 180f;
                angle.Y = ((angle.Y % 180f) + 180f) % 180f;

                var score = hasScores && i < scores.Length ? scores[i] : 0f;
                
                particles.Add(new Particle(position, angle, score));
            }
            
            return particles;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading particles from STAR file");
            return new List<Particle>();
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

        Dictionary<string, List<decimal>> allValues = new()
        {
            { SelectParticles.FilterKeyScore, allParticles.Select(p => (decimal)p.Score).ToList() },
            { SelectParticles.FilterKeyCoordX, allParticles.Select(p => (decimal)p.Position.X).ToList() },
            { SelectParticles.FilterKeyCoordY, allParticles.Select(p => (decimal)p.Position.Y).ToList() },
            { SelectParticles.FilterKeyCoordZ, allParticles.Select(p => (decimal)p.Position.Z).ToList() },
            { SelectParticles.FilterKeyAngleX, allParticles.Select(p => (decimal)p.Angles.X).ToList() },
            { SelectParticles.FilterKeyAngleY, allParticles.Select(p => (decimal)p.Angles.Y).ToList() }
        };
        
        ct.ThrowIfCancellationRequested();
            
        // Calculate ranges across all particles
        _globalHistogramRanges[SelectParticles.FilterKeyScore] = CalculateRange(allValues[SelectParticles.FilterKeyScore]);
        _globalHistogramRanges[SelectParticles.FilterKeyCoordX] = (0, _tomoDims.X - 1);
        _globalHistogramRanges[SelectParticles.FilterKeyCoordY] = (0, _tomoDims.Y - 1);
        _globalHistogramRanges[SelectParticles.FilterKeyCoordZ] = (0, _tomoDims.Z - 1);
        _globalHistogramRanges[SelectParticles.FilterKeyAngleX] = CalculateRange(allValues[SelectParticles.FilterKeyAngleX]);
        _globalHistogramRanges[SelectParticles.FilterKeyAngleY] = CalculateRange(allValues[SelectParticles.FilterKeyAngleY]);
        
        ct.ThrowIfCancellationRequested();
        
        // Calculate histogram bins
        foreach (var key in SelectParticles.FilterKeys)
        {
            ct.ThrowIfCancellationRequested();
            _globalHistogramBins[key] = CalculateHistogramBins(allValues[key],
                                                               _globalHistogramRanges[key].Min,
                                                               _globalHistogramRanges[key].Max,
                                                               (int)((_globalHistogramRanges[key].Max -
                                                                      _globalHistogramRanges[key].Min) *
                                                                     (key == SelectParticles.FilterKeyScore ? 10 : 1)) +
                                                               1,
                                                               ct);
        }
        
        // Calculate global filter counts and filtered histograms
        UpdateGlobalFilterCounts(allParticles, ct);
        CalculateGlobalFilteredHistograms(allParticles, ct);
        
        // If tomogram selected, update its histograms
        if (_particles.Count > 0)
            UpdateSelectedTomogramHistograms(ct);
    }
    
    private void UpdateGlobalFilterCounts(List<Particle> allParticles, CancellationToken ct = default)
    {
        if (allParticles.Count == 0 || _job == null)
        {
            _globalTotalCounts = (0, 0);
            foreach (var key in SelectParticles.FilterKeys)
                _globalFilterCounts[key] = (0, 0);
            return;
        }
        
        ct.ThrowIfCancellationRequested();
        
        // Group particles by tomogram to apply correct filters
        var particlesByTomogram = _allTomogramParticles.ToDictionary(
            t => t.TomogramName,
            t => t.Particles
        );
        
        // For each filter key, count particles that pass
        foreach (var key in SelectParticles.FilterKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            int passCount = 0;
            
            // Count by applying appropriate filter for each tomogram
            foreach (var tomoEntry in particlesByTomogram)
            {
                string tomoName = tomoEntry.Key;
                List<Particle> tomoParticles = tomoEntry.Value;
                
                // Get the correct filter to apply (local if unlocked, global if locked or non-existent)
                var filterToUse = _job.GetUnlockedFilterSetting(tomoName, key);
                
                // Count particles from this tomogram that pass the filter
                if (key == SelectParticles.FilterKeyScore)
                    passCount += tomoParticles.Count(p => filterToUse?.Passes(p.Score) ?? true);
                else if (key == SelectParticles.FilterKeyCoordX)
                    passCount += tomoParticles.Count(p => filterToUse?.Passes(p.Position.X) ?? true);
                else if (key == SelectParticles.FilterKeyCoordY)
                    passCount += tomoParticles.Count(p => filterToUse?.Passes(p.Position.Y) ?? true);
                else if (key == SelectParticles.FilterKeyCoordZ)
                    passCount += tomoParticles.Count(p => filterToUse?.Passes(p.Position.Z) ?? true);
                else if (key == SelectParticles.FilterKeyAngleX)
                    passCount += tomoParticles.Count(p => filterToUse?.Passes(p.Angles.X) ?? true);
                else if (key == SelectParticles.FilterKeyAngleY)
                    passCount += tomoParticles.Count(p => filterToUse?.Passes(p.Angles.Y) ?? true);
            }
            
            _globalFilterCounts[key] = (allParticles.Count, passCount);
        }
        
        // Count particles that pass all applicable filters
        int filteredTotal = 0;
        
        foreach (var tomoEntry in particlesByTomogram)
        {
            ct.ThrowIfCancellationRequested();
            
            string tomoName = tomoEntry.Key;
            List<Particle> tomoParticles = tomoEntry.Value;
            
            // Get all the filters for this tomogram
            var scoreFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyScore);
            var coordXFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyCoordX);
            var coordYFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyCoordY);
            var coordZFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyCoordZ);
            var angleXFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyAngleX);
            var angleYFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyAngleY);
            
            // Count particles from this tomogram that pass all filters
            filteredTotal += tomoParticles.Count(p => 
                (scoreFilter?.Passes(p.Score) ?? true) &&
                (coordXFilter?.Passes(p.Position.X) ?? true) &&
                (coordYFilter?.Passes(p.Position.Y) ?? true) &&
                (coordZFilter?.Passes(p.Position.Z) ?? true) &&
                (angleXFilter?.Passes(p.Angles.X) ?? true) &&
                (angleYFilter?.Passes(p.Angles.Y) ?? true)
            );
        }
        
        _globalTotalCounts = (allParticles.Count, filteredTotal);
    }
    
    private void UpdateSelectedTomogramHistograms(CancellationToken ct = default)
    {
        if (_particles.Count == 0)
        {
            // Clear histograms if no particles
            foreach (var key in SelectParticles.FilterKeys)
            {
                _tomogramHistogramBins[key] = new decimal[50];
                _tomogramFilteredHistogramBins[key] = new decimal[50];
            }
            
            return;
        }
        
        Dictionary<string, List<decimal>> allValues = new()
        {
            { SelectParticles.FilterKeyScore, _particles.Select(p => (decimal)p.Score).ToList() },
            { SelectParticles.FilterKeyCoordX, _particles.Select(p => (decimal)p.Position.X).ToList() },
            { SelectParticles.FilterKeyCoordY, _particles.Select(p => (decimal)p.Position.Y).ToList() },
            { SelectParticles.FilterKeyCoordZ, _particles.Select(p => (decimal)p.Position.Z).ToList() },
            { SelectParticles.FilterKeyAngleX, _particles.Select(p => (decimal)p.Angles.X).ToList() },
            { SelectParticles.FilterKeyAngleY, _particles.Select(p => (decimal)p.Angles.Y).ToList() }
        };
        
        // Calculate histogram bins using same bin counts as global histograms
        foreach (var key in SelectParticles.FilterKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            _tomogramHistogramBins[key] = CalculateHistogramBins(allValues[key],
                                                                 _globalHistogramRanges[key].Min,
                                                                 _globalHistogramRanges[key].Max,
                                                                 (int)((_globalHistogramRanges[key].Max -
                                                                        _globalHistogramRanges[key].Min) *
                                                                       (key == SelectParticles.FilterKeyScore ? 10 : 1)) +
                                                                 1);
        }
                                                                 
        // Calculate filtered histogram bins for the tomogram
        CalculateTomogramFilteredHistograms(ct);
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
    
    #region Filter Management
    
    // Helper methods for filter UI
    private bool IsFilterLocked(string tomoName, string key)
    {
        if (_job == null || string.IsNullOrEmpty(tomoName))
            return true;
            
        if (!_job.TomogramFilterSettings.ContainsKey(tomoName) || 
            !_job.TomogramFilterSettings[tomoName].ContainsKey(key))
            return true;
            
        return _job.TomogramFilterSettings[tomoName].Filters[key].Locked;
    }
    
    // Determine if global filter should be disabled (when local filter is unlocked)
    private bool IsGlobalFilterDisabled(string key)
    {
        if (_job == null || string.IsNullOrEmpty(_selectedTomogramName))
            return true;
            
        return !IsFilterLocked(_selectedTomogramName, key) || IsEditingDisabled;
    }
    
    private decimal GetFilterStart(string tomoName, string key)
    {
        var filter = _job?.GetFilterSetting(tomoName, key);
        return filter?.Min ?? 0;
    }
    
    private decimal GetFilterEnd(string tomoName, string key)
    {
        var filter = _job?.GetFilterSetting(tomoName, key);
        return filter?.Max ?? 1;
    }
    
    // Event handlers for filter range changes
    private async Task OnGlobalRangeChanged(string key, (decimal Start, decimal End) range)
    {
        if (_job == null || string.IsNullOrEmpty(key))
            return;
        
        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            var job = (SelectParticles)originalJob;
            job.GlobalFilterSettings.Filters[key].Min = range.Start;
            job.GlobalFilterSettings.Filters[key].Max = range.End;
        });
        
        // Job update will trigger HandleJobUpdated, which will update particle counts
    }
    
    private async Task OnTomogramRangeChanged(string tomoName, string key, (decimal Start, decimal End) range)
    {
        if (_job == null || string.IsNullOrEmpty(tomoName) || string.IsNullOrEmpty(key))
            return;
        
        await DataManager.UpdateJob(Session.User, _job, originalJob =>
        {
            var job = (SelectParticles)originalJob;
            if (!job.TomogramFilterSettings.ContainsKey(tomoName))
                job.TomogramFilterSettings[tomoName] = new FilterCollection();
            if (!job.TomogramFilterSettings[tomoName].Filters.ContainsKey(key))
                job.TomogramFilterSettings[tomoName].Filters[key] = new FilterSetting();
            
            job.TomogramFilterSettings[tomoName].Filters[key].Min = range.Start;
            job.TomogramFilterSettings[tomoName].Filters[key].Max = range.End;
        });
        
        // Job update will trigger HandleJobUpdated, which will update particle counts
    }
    
    // Toggle lock state for a filter type
    private async Task ToggleTomogramLockState(string tomoName, string key)
    {
        if (_job == null || string.IsNullOrEmpty(tomoName))
            return;
        
        // Update the filter settings dictionary with the new lock state
        await DataManager.UpdateJob(Session.User, _job, originalJob => 
        {
            var job = (SelectParticles)originalJob;
            
            if (!job.TomogramFilterSettings.ContainsKey(tomoName))
                job.TomogramFilterSettings[tomoName] = new FilterCollection();
            
            if (!job.TomogramFilterSettings[tomoName].Filters.ContainsKey(key))
                job.TomogramFilterSettings[tomoName].Filters[key] = new FilterSetting()
                {
                    Min = job.GlobalFilterSettings.Filters[key].Min,
                    Max = job.GlobalFilterSettings.Filters[key].Max,
                    Locked = false
                };
            else
                job.TomogramFilterSettings[tomoName].Filters[key].Locked = !job.TomogramFilterSettings[tomoName].Filters[key].Locked;
        });
        
        //await UpdateFilteredParticles();
    }
    
    // Update the filtered particles based on current filter settings
    private async Task UpdateFilteredParticles()
    {
        if (_particles.Count == 0)
        {
            _filteredParticles = [];
            RebuildViewerSpecies();
            _tomogramTotalCounts = (0, 0);
            foreach (var key in SelectParticles.FilterKeys)
                _tomogramFilterCounts[key] = (0, 0);
            return;
        }

        // Get the appropriate filter for each parameter
        // If a local filter is unlocked, use it; otherwise use the global filter
        var filterScore = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyScore);
        var filterCoordX = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyCoordX);
        var filterCoordY = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyCoordY);
        var filterCoordZ = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyCoordZ);
        var filterAngleX = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyAngleX);
        var filterAngleY = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyAngleY);
        
        // Reset counts
        _tomogramTotalCounts = (_particles.Count, 0);
        
        // Calculate counts for each individual filter
        _tomogramFilterCounts[SelectParticles.FilterKeyScore] = (
            _particles.Count,
            _particles.Count(p => filterScore?.Passes(p.Score) ?? true)
        );
        
        _tomogramFilterCounts[SelectParticles.FilterKeyCoordX] = (
            _particles.Count,
            _particles.Count(p => filterCoordX?.Passes(p.Position.X) ?? true)
        );
        
        _tomogramFilterCounts[SelectParticles.FilterKeyCoordY] = (
            _particles.Count,
            _particles.Count(p => filterCoordY?.Passes(p.Position.Y) ?? true)
        );
        
        _tomogramFilterCounts[SelectParticles.FilterKeyCoordZ] = (
            _particles.Count,
            _particles.Count(p => filterCoordZ?.Passes(p.Position.Z) ?? true)
        );
        
        _tomogramFilterCounts[SelectParticles.FilterKeyAngleX] = (
            _particles.Count,
            _particles.Count(p => filterAngleX?.Passes(p.Angles.X) ?? true)
        );
        
        _tomogramFilterCounts[SelectParticles.FilterKeyAngleY] = (
            _particles.Count,
            _particles.Count(p => filterAngleY?.Passes(p.Angles.Y) ?? true)
        );
        
        // Apply all filters
        _filteredParticles = _particles.Where(p => 
        {
            return (filterScore?.Passes(p.Score) ?? true) &&
                   (filterCoordX?.Passes(p.Position.X) ?? true) &&
                   (filterCoordY?.Passes(p.Position.Y) ?? true) &&
                   (filterCoordZ?.Passes(p.Position.Z) ?? true) &&
                   (filterAngleX?.Passes(p.Angles.X) ?? true) &&
                   (filterAngleY?.Passes(p.Angles.Y) ?? true);
        }).ToList();
        
        RebuildViewerSpecies();

        // Update total counts
        _tomogramTotalCounts = (_particles.Count, _filteredParticles.Count);

        // Update filtered histograms
        CalculateTomogramFilteredHistograms(default(CancellationToken));
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
        _selectedTomogramName = tomogramName;
        
        // Set the full path to the tomogram file
        _selectedTomogramPath = _tomogramSet.ToTomogramPath(tomogramName);
        
        // Get particles for the selected tomogram from our already loaded data
        _particles = [];
        var tomogramData = _allTomogramParticles.FirstOrDefault(t => t.TomogramName == tomogramName);
        if (tomogramData != default)
        {
            _particles = tomogramData.Particles;
            UpdateSelectedTomogramHistograms(default(CancellationToken));
        }
        
        // Apply filters to the particles and update counts
        await UpdateFilteredParticles();

        if (scrollToItem && _tomogramThumbnailPanel != null)
            await _tomogramThumbnailPanel.SetSelectedThumbnailAsync(_selectedTomogramThumbnail);
        
        await InvokeAsync(StateHasChanged);
    }

    #endregion
    
    #region Filtered Histogram Methods
    
    private void CalculateGlobalFilteredHistograms(List<Particle> allParticles, CancellationToken ct = default)
    {
        if (allParticles.Count == 0 || _job == null)
        {
            foreach (var key in SelectParticles.FilterKeys)
                _globalFilteredHistogramBins[key] = Array.Empty<decimal>();
            return;
        }
        
        // Group particles by tomogram to apply correct filters
        var particlesByTomogram = _allTomogramParticles.ToDictionary(
            t => t.TomogramName,
            t => t.Particles
        );
        
        // First, get all fully filtered particles (that pass ALL filters)
        List<Particle> allFilteredParticles = new();
        
        foreach (var tomoEntry in particlesByTomogram)
        {
            ct.ThrowIfCancellationRequested();
            
            string tomoName = tomoEntry.Key;
            List<Particle> tomoParticles = tomoEntry.Value;
            
            // Get all the filters for this tomogram
            var scoreFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyScore);
            var coordXFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyCoordX);
            var coordYFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyCoordY);
            var coordZFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyCoordZ);
            var angleXFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyAngleX);
            var angleYFilter = _job.GetUnlockedFilterSetting(tomoName, SelectParticles.FilterKeyAngleY);
            
            // Get particles that pass ALL filters
            var filteredTomogramParticles = tomoParticles.Where(p => 
                (scoreFilter?.Passes(p.Score) ?? true) &&
                (coordXFilter?.Passes(p.Position.X) ?? true) &&
                (coordYFilter?.Passes(p.Position.Y) ?? true) &&
                (coordZFilter?.Passes(p.Position.Z) ?? true) &&
                (angleXFilter?.Passes(p.Angles.X) ?? true) &&
                (angleYFilter?.Passes(p.Angles.Y) ?? true)
            ).ToList();
            
            // Add these filtered particles to our global list
            allFilteredParticles.AddRange(filteredTomogramParticles);
        }
        
        ct.ThrowIfCancellationRequested();
        
        // Now, for each key, extract the relevant values from the fully filtered particles
        foreach (var key in SelectParticles.FilterKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            // Extract the specific values for this filter type
            List<decimal> filteredValues = new();
            
            switch (key)
            {
                case SelectParticles.FilterKeyScore:
                    filteredValues.AddRange(allFilteredParticles.Select(p => (decimal)p.Score));
                    break;
                case SelectParticles.FilterKeyCoordX:
                    filteredValues.AddRange(allFilteredParticles.Select(p => (decimal)p.Position.X));
                    break;
                case SelectParticles.FilterKeyCoordY:
                    filteredValues.AddRange(allFilteredParticles.Select(p => (decimal)p.Position.Y));
                    break;
                case SelectParticles.FilterKeyCoordZ:
                    filteredValues.AddRange(allFilteredParticles.Select(p => (decimal)p.Position.Z));
                    break;
                case SelectParticles.FilterKeyAngleX:
                    filteredValues.AddRange(allFilteredParticles.Select(p => (decimal)p.Angles.X));
                    break;
                case SelectParticles.FilterKeyAngleY:
                    filteredValues.AddRange(allFilteredParticles.Select(p => (decimal)p.Angles.Y));
                    break;
            }
            
            // Calculate histogram bins for the filtered values
            _globalFilteredHistogramBins[key] = CalculateHistogramBins(
                filteredValues,
                _globalHistogramRanges[key].Min,
                _globalHistogramRanges[key].Max,
                (int)((_globalHistogramRanges[key].Max - _globalHistogramRanges[key].Min) * 
                      (key == SelectParticles.FilterKeyScore ? 10 : 1)) + 1
            );
        }
    }
    
    private void CalculateTomogramFilteredHistograms(CancellationToken ct = default)
    {
        if (_particles.Count == 0 || _job == null || string.IsNullOrEmpty(_selectedTomogramName))
        {
            foreach (var key in SelectParticles.FilterKeys)
                _tomogramFilteredHistogramBins[key] = Array.Empty<decimal>();
            return;
        }
        
        // Get all the filters for this tomogram
        var scoreFilter = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyScore);
        var coordXFilter = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyCoordX);
        var coordYFilter = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyCoordY);
        var coordZFilter = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyCoordZ);
        var angleXFilter = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyAngleX);
        var angleYFilter = _job.GetUnlockedFilterSetting(_selectedTomogramName, SelectParticles.FilterKeyAngleY);
        
        // Get particles that pass ALL filters
        var filteredParticles = _particles.Where(p => 
            (scoreFilter?.Passes(p.Score) ?? true) &&
            (coordXFilter?.Passes(p.Position.X) ?? true) &&
            (coordYFilter?.Passes(p.Position.Y) ?? true) &&
            (coordZFilter?.Passes(p.Position.Z) ?? true) &&
            (angleXFilter?.Passes(p.Angles.X) ?? true) &&
            (angleYFilter?.Passes(p.Angles.Y) ?? true)
        ).ToList();
        
        // Now, for each key, calculate a filtered histogram from the fully filtered particles
        foreach (var key in SelectParticles.FilterKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            // Create a list of the relevant values from the filtered particles
            List<decimal> filteredValues = new();
            switch (key)
            {
                case SelectParticles.FilterKeyScore:
                    filteredValues.AddRange(filteredParticles.Select(p => (decimal)p.Score));
                    break;
                case SelectParticles.FilterKeyCoordX:
                    filteredValues.AddRange(filteredParticles.Select(p => (decimal)p.Position.X));
                    break;
                case SelectParticles.FilterKeyCoordY:
                    filteredValues.AddRange(filteredParticles.Select(p => (decimal)p.Position.Y));
                    break;
                case SelectParticles.FilterKeyCoordZ:
                    filteredValues.AddRange(filteredParticles.Select(p => (decimal)p.Position.Z));
                    break;
                case SelectParticles.FilterKeyAngleX:
                    filteredValues.AddRange(filteredParticles.Select(p => (decimal)p.Angles.X));
                    break;
                case SelectParticles.FilterKeyAngleY:
                    filteredValues.AddRange(filteredParticles.Select(p => (decimal)p.Angles.Y));
                    break;
            }
            
            // Calculate histogram bins for the filtered values
            _tomogramFilteredHistogramBins[key] = CalculateHistogramBins(
                filteredValues,
                _globalHistogramRanges[key].Min,
                _globalHistogramRanges[key].Max,
                (int)((_globalHistogramRanges[key].Max - _globalHistogramRanges[key].Min) * 
                      (key == SelectParticles.FilterKeyScore ? 10 : 1)) + 1
            );
        }
    }
    
    #endregion
}