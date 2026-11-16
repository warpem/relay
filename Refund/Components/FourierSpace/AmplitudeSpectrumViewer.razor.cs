using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Collections.ObjectModel;
using System.Globalization;
using Refund.Services;
using Warp;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Components.FourierSpace;

/// <summary>
/// Component that displays the amplitude spectrum (power spectrum) of a cryo-EM movie or tilt series, 
/// allowing visualization of CTF (Contrast Transfer Function) fitting.
/// This component presents both the 2D power spectrum image and the 1D plot showing 
/// experimental data, fitted model, and fit quality across spatial frequencies.
/// </summary>
public partial class AmplitudeSpectrumViewer : IAsyncDisposable
{
    [Inject] private ILogger<AmplitudeSpectrumViewer> Logger { get; set; }
    [Inject] private FileService FileService { get; set; }
    /// <summary>
    /// Minimum range for CTF fitting in Fourier space, expressed as a fraction 
    /// of Nyquist frequency. This defines the starting point for the fitted region.
    /// </summary>
    [Parameter, EditorRequired] public decimal FittingRangeMin { get; set; }
    
    /// <summary>
    /// Maximum range for CTF fitting in Fourier space, expressed as a fraction
    /// of Nyquist frequency. This defines the ending point for the fitted region.
    /// </summary>
    [Parameter, EditorRequired] public decimal FittingRangeMax { get; set; }
    
    /// <summary>
    /// Path to the movie file for which to display the amplitude spectrum.
    /// The component will extract and visualize the power spectrum from this file.
    /// This parameter is used for standard movie mode.
    /// </summary>
    [Parameter] public string? MovieFilePath { get; set; }
    /// <summary>Reload a live CTF result when its metadata/spectrum files are overwritten.</summary>
    [Parameter] public long? ResultRevision { get; set; }
    private long? _previousResultRevision;
    
    /// <summary>
    /// Path to the tilt series file for which to display the amplitude spectrum.
    /// The component will extract and visualize the power spectrum from this file.
    /// This parameter is used for tilt series mode.
    /// </summary>
    [Parameter] public string? TiltSeriesPath { get; set; }

    private ElementReference _canvasReference;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<AmplitudeSpectrumViewer>? _dotNetRef;
    private string _previousMoviePath = string.Empty;
    private string _previousTiltSeriesPath = string.Empty;
    private ElementReference _spectrumCanvas;
    // The browser loads and draws the 2D spectrum from its MRC file; tilt series pick a plane per tilt.
    private SpectrumSource? _spectrum;
    private readonly int _chartWidth = 400;
    private readonly int _chartHeight = 400;
    private bool _isChartInitialized;
    private bool _isLoading = true;
    
    // Tilt series specific properties
    private int _tiltCount = 0;
    private float[] _tiltAngles = [];
    private int _zeroTiltIndex = 0;
    private int _currentTiltIndex = 0;
    private Dictionary<int, WarpSeriesData> _cachedTiltSeriesData = new();

    private decimal? _specimenThicknessAngstrom;
    
    /// <summary>
    /// Gets whether the component is in tilt series mode.
    /// </summary>
    public bool IsTiltSeriesMode => !string.IsNullOrEmpty(TiltSeriesPath);
    
    /// <summary>
    /// Gets or sets the currently selected tilt index (only relevant in tilt series mode).
    /// </summary>
    public int CurrentTiltIndex
    {
        get => _currentTiltIndex;
        private set
        {
            if (value != _currentTiltIndex && value >= 0 && value < _tiltCount)
            {
                _currentTiltIndex = value;
                UpdateTiltVisualizationAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Handles tilt index changes from the slider UI.
    /// </summary>
    private async Task HandleTiltIndexChanged(int newIndex)
    {
        await SetTiltIndex(newIndex);
    }
    
    /// <summary>
    /// Public method to set the selected tilt index from a parent component.
    /// Only works in tilt series mode and if the specified index is valid.
    /// </summary>
    /// <param name="tiltIndex">The tilt index to select</param>
    /// <returns>Task that completes when the visualization has been updated</returns>
    public async Task<bool> SetTiltIndex(int tiltIndex)
    {
        if (!IsTiltSeriesMode || tiltIndex < 0 || tiltIndex >= _tiltCount)
            return false;
        
        if (tiltIndex != _currentTiltIndex)
        {
            _currentTiltIndex = tiltIndex;
            await UpdateTiltVisualizationAsync();
            await InvokeAsync(StateHasChanged);
        }
        
        return true;
    }

    /// <summary>
    /// Initializes the component after rendering. For the first render, imports the JavaScript module,
    /// creates a .NET reference for JS interop, and initializes the visualization.
    /// </summary>
    /// <param name="firstRender">True if this is the first time the component has been rendered.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/FourierSpace/AmplitudeSpectrumViewer.razor.js");
            _dotNetRef = DotNetObjectReference.Create(this);
            await _jsModule.InvokeVoidAsync("initialize", _dotNetRef);

            await UpdateVisualization();
        }
    }

    /// <summary>
    /// Handles parameter changes. If the movie file path or tilt series path changes, updates 
    /// the visualization by destroying any existing chart and creating a new one.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        bool needsUpdate = false;
        
        // Check if movie path has changed
        if (MovieFilePath != null && (MovieFilePath != _previousMoviePath || ResultRevision != _previousResultRevision))
        {
            _previousMoviePath = MovieFilePath;
            _previousResultRevision = ResultRevision;
            needsUpdate = true;
        }
        
        // Check if tilt series path has changed
        if (TiltSeriesPath != null && TiltSeriesPath != _previousTiltSeriesPath)
        {
            _previousTiltSeriesPath = TiltSeriesPath;
            _cachedTiltSeriesData.Clear();
            needsUpdate = true;
        }
        
        if (!needsUpdate)
            return;
            
        _isLoading = true;
        _specimenThicknessAngstrom = null;
        await InvokeAsync(StateHasChanged);

        if (_isChartInitialized)
            await DestroyChart();

        await UpdateVisualization();
    }

    /// <summary>
    /// Updates the visualization based on whether we're in movie mode or tilt series mode.
    /// </summary>
    private async Task UpdateVisualization()
    {
        if (_jsModule == null)
            return;

        try 
        {
            if (IsTiltSeriesMode && !string.IsNullOrEmpty(TiltSeriesPath))
            {
                await LoadTiltSeriesData(TiltSeriesPath);
                await UpdateTiltVisualizationAsync();
            }
            else if (!string.IsNullOrEmpty(MovieFilePath))
            {
                await UpdateMovieVisualization(MovieFilePath);
            }
            else
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating visualization");
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }
    
    /// <summary>
    /// Loads data from a tilt series file and caches power spectrum images and chart data for all tilts.
    /// </summary>
    private async Task LoadTiltSeriesData(string tiltSeriesPath)
    {
        var tiltSeries = new TiltSeries(tiltSeriesPath);
        _tiltCount = tiltSeries.NTilts;
        _tiltAngles = tiltSeries.Angles;
        _zeroTiltIndex = 0;
        for (int tiltIndex = 1; tiltIndex < _tiltAngles.Length; tiltIndex++)
        {
            if (Math.Abs(_tiltAngles[tiltIndex]) < Math.Abs(_tiltAngles[_zeroTiltIndex]))
                _zeroTiltIndex = tiltIndex;
        }
        _currentTiltIndex = _zeroTiltIndex;
        
        _spectrum = SpectrumFor(tiltSeries.PowerSpectrumPath, tiltSeries.OptionsCTF.RangeMin, tiltSeries.OptionsCTF.RangeMax, 0);
        for (int tiltIndex = 0; tiltIndex < _tiltCount; tiltIndex++)
        {
            var warpSeriesData = GetTiltWarpSeries(tiltSeries, tiltIndex, FittingRangeMin, FittingRangeMax);
            _cachedTiltSeriesData[tiltIndex] = warpSeriesData;
        }
    }
    
    /// <summary>
    /// Updates the visualization for the currently selected tilt.
    /// </summary>
    private async Task UpdateTiltVisualizationAsync()
    {
        if (_jsModule == null || !IsTiltSeriesMode)
            return;
            
        try
        {
            if (_cachedTiltSeriesData.TryGetValue(_currentTiltIndex, out var warpSeries))
            {
                _specimenThicknessAngstrom = warpSeries.SpecimenThicknessAngstrom;
                // First update UI state to ensure the canvas is rendered
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
                
                // Then initialize the chart after the canvas is available in the DOM
                await Task.Delay(10); // Small delay to ensure the canvas is rendered
                
                await _jsModule.InvokeVoidAsync("setupChart", 
                    _canvasReference,
                    new ChartConfig 
                    {
                        BinnedPixelSize = warpSeries.BinnedPixelSize,
                        MinRange = warpSeries.MinRange,
                        MaxRange = warpSeries.MaxRange,
                        MinNormalized = warpSeries.MinNormalized,
                        MaxNormalized = warpSeries.MaxNormalized,
                        ExperimentalValues = warpSeries.ExperimentalValues.Select(v => float.IsNaN(v) ? 0f : MathF.Round(v, 2)).ToArray(),
                        SimulatedValues = warpSeries.SimulatedValues.Select(v => float.IsNaN(v) ? 0f : MathF.Round(v, 2)).ToArray(),
                        QualityValues = warpSeries.QualityValues
                    });
                if (_spectrum != null)
                    await RenderSpectrumAsync(_spectrum with { Layer = _currentTiltIndex });
            }
            else
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
            
            _isChartInitialized = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating tilt visualization");
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }
    
    /// <summary>
    /// Updates the visualization for a movie file.
    /// </summary>
    private async Task UpdateMovieVisualization(string movieFilePath)
    {
        if (_jsModule == null)
            return;
            
        try
        {
            var movie = new Movie(movieFilePath);
            var spectrum = _spectrum = SpectrumFor(movie.PowerSpectrumPath, movie.OptionsCTF.RangeMin, movie.OptionsCTF.RangeMax, 0);

            var warpSeries = GetWarpSeries(movieFilePath, FittingRangeMin, FittingRangeMax);
            _specimenThicknessAngstrom = warpSeries.SpecimenThicknessAngstrom;

            // First update UI state to ensure the canvas is rendered
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
            
            // Then initialize the chart after the canvas is available in the DOM
            await Task.Delay(10); // Small delay to ensure the canvas is rendered

            await _jsModule.InvokeVoidAsync("setupChart", 
                _canvasReference,
                new ChartConfig 
                {
                    BinnedPixelSize = warpSeries.BinnedPixelSize,
                    MinRange = warpSeries.MinRange,
                    MaxRange = warpSeries.MaxRange,
                    MinNormalized = warpSeries.MinNormalized,
                    MaxNormalized = warpSeries.MaxNormalized,
                    ExperimentalValues = warpSeries.ExperimentalValues.Select(v => float.IsNaN(v) ? 0f : MathF.Round(v, 2)).ToArray(),
                    SimulatedValues = warpSeries.SimulatedValues.Select(v => float.IsNaN(v) ? 0f : MathF.Round(v, 2)).ToArray(),
                    QualityValues = warpSeries.QualityValues
                });
            await RenderSpectrumAsync(spectrum);

            _isChartInitialized = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating movie visualization");
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private sealed record SpectrumSource(string Url, int Layer, decimal RangeMin, decimal RangeMax);

    /// <summary>
    /// Points the browser at a spectrum file. The write time busts the browser cache when a rerun
    /// overwrites the file under the same URL.
    /// </summary>
    private SpectrumSource SpectrumFor(string path, decimal rangeMin, decimal rangeMax, int layer) =>
        new(FileService.GetUrl(path) + "?v=" + File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture),
            layer, rangeMin, rangeMax);

    private async Task RenderSpectrumAsync(SpectrumSource spectrum)
    {
        try
        {
            await _jsModule!.InvokeAsync<bool>("renderSpectrum", _spectrumCanvas, spectrum);
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Couldn't load power spectrum {Url}", spectrum.Url);
        }
    }

    /// <summary>
    /// Extracts and prepares the CTF fitting data from a movie file for visualization.
    /// </summary>
    /// <param name="basePath">Path to the movie file</param>
    /// <param name="rangeMin">Minimum range for CTF fitting (fraction of Nyquist)</param>
    /// <param name="rangeMax">Maximum range for CTF fitting (fraction of Nyquist)</param>
    /// <returns>A WarpSeriesData object containing all data for the chart visualization</returns>
    internal static WarpSeriesData GetWarpSeries(string basePath, decimal rangeMin, decimal rangeMax)
    {
        var movie = new Movie(basePath);

        var fittingRangeMin = movie.CTF.PixelSize * 2 / rangeMin;
        var fittingRangeMax = movie.CTF.PixelSize * 2 / rangeMax;

        var experimentalData = movie.PS1D;
        var simulatedData = movie.Simulated1D;

        var experimentalDataLengthMultiplied = experimentalData.Length * 2;

        // Transform to y-values only after applying x-transformation
        var experimentalValues = experimentalData
            .Select(s => s.Y)
            .ToArray();

        var simulatedValues = simulatedData
            .Select(s => s.Y)
            .ToArray();

        // Calculate ranges from transformed values
        var start = (int)(experimentalValues.Length * fittingRangeMin);
        var end = (int)(experimentalValues.Length * (fittingRangeMax - fittingRangeMin));

        var relevantExperimental = experimentalValues.Skip(start).Take(end).ToArray();
        var relevantSimulated = simulatedValues.Skip(start).Take(end).ToArray();

        var minExperimental = MathHelper.Min(relevantExperimental);
        var maxExperimental = MathHelper.Max(relevantExperimental);
        var minSimulated = MathHelper.Min(relevantSimulated);
        var maxSimulated = MathHelper.Max(relevantSimulated);

        return new WarpSeriesData((float)movie.OptionsCTF.BinnedPixelSizeMean,
                                  (int)(experimentalDataLengthMultiplied * (double)fittingRangeMin / 2),
                                  (int)(experimentalDataLengthMultiplied * (double)fittingRangeMax / 2),
                                  Math.Min(minExperimental, minSimulated),
                                  Math.Max(maxExperimental, maxSimulated) * 1.25f,
                                  experimentalValues,
                                  simulatedValues,
                                  GetQualityValues(movie.CTFQuality),
                                  movie.CTFSpecimenThicknessAngstrom > 0 ? movie.CTFSpecimenThicknessAngstrom : null
        );
    }
    
    /// <summary>
    /// Extracts and prepares the CTF fitting data from a tilt series file for a specific tilt.
    /// </summary>
    /// <param name="tiltSeries">The tilt series object</param>
    /// <param name="tiltIndex">The index of the tilt to process</param>
    /// <param name="rangeMin">Minimum range for CTF fitting (fraction of Nyquist)</param>
    /// <param name="rangeMax">Maximum range for CTF fitting (fraction of Nyquist)</param>
    /// <returns>A WarpSeriesData object containing all data for the chart visualization</returns>
    internal static WarpSeriesData GetTiltWarpSeries(TiltSeries tiltSeries, int tiltIndex, decimal rangeMin, decimal rangeMax)
    {
        var fittingRangeMin = tiltSeries.CTF.PixelSize * 2 / rangeMin;
        var fittingRangeMax = tiltSeries.CTF.PixelSize * 2 / rangeMax;

        // Get the experimental and simulated data for the specific tilt
        var experimentalData = tiltSeries.TiltPS1D[tiltIndex];
        var simulatedData = tiltSeries.GetTiltSimulated1D(tiltIndex);

        var experimentalDataLengthMultiplied = experimentalData.Length * 2;

        // Transform to y-values only after applying x-transformation
        var experimentalValues = experimentalData
            .Select(s => s.Y)
            .ToArray();

        var simulatedValues = simulatedData
            .Select(s => s.Y)
            .ToArray();

        // Calculate ranges from transformed values
        var start = (int)(experimentalValues.Length * fittingRangeMin);
        var end = (int)(experimentalValues.Length * (fittingRangeMax - fittingRangeMin));

        var relevantExperimental = experimentalValues.Skip(start).Take(end).ToArray();
        var relevantSimulated = simulatedValues.Skip(start).Take(end).ToArray();

        var minExperimental = MathHelper.Min(relevantExperimental);
        var maxExperimental = MathHelper.Max(relevantExperimental);
        var minSimulated = MathHelper.Min(relevantSimulated);
        var maxSimulated = MathHelper.Max(relevantSimulated);

        return new WarpSeriesData((float)tiltSeries.OptionsCTF.BinnedPixelSizeMean,
                                  (int)(experimentalDataLengthMultiplied * (double)fittingRangeMin / 2),
                                  (int)(experimentalDataLengthMultiplied * (double)fittingRangeMax / 2),
                                  Math.Min(minExperimental, minSimulated),
                                  Math.Max(maxExperimental, maxSimulated) * 1.25f,
                                  experimentalValues,
                                  simulatedValues,
                                  GetQualityValues(tiltSeries.TiltCTFQuality.ElementAtOrDefault(tiltIndex)),
                                  tiltSeries.CTFSpecimenThicknessAngstrom > 0 ? tiltSeries.CTFSpecimenThicknessAngstrom : null
        );
    }

    // JSON nulls keep unsupported frequencies as gaps in Chart.js instead of zero correlations.
    private static float?[] GetQualityValues(float2[] curve) =>
        curve?.Select(p => float.IsFinite(p.Y) ? (float?)p.Y : null).ToArray() ?? [];

    /// <summary>
    /// Destroys the current chart by calling the JavaScript destroy function.
    /// This should be called before creating a new chart or when the component is disposed.
    /// </summary>
    private async Task DestroyChart()
    {
        if (_jsModule == null) 
            return;
            
        try
        {
            await _jsModule.InvokeVoidAsync("destroyChart");
            _isChartInitialized = false;
        }
        catch (JSDisconnectedException)
        {
            // The browser is gone, and the chart with it.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error destroying chart");
        }
    }

    /// <summary>
    /// Implements IAsyncDisposable to clean up resources when the component is removed from the UI.
    /// Destroys the chart, disposes of JavaScript module references, and clears memory buffers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_isChartInitialized)
                await DestroyChart();

            if (_jsModule != null)
                await _jsModule.DisposeAsync();

            _dotNetRef?.Dispose();
            _cachedTiltSeriesData.Clear();
        }
        catch (JSDisconnectedException)
        {
            // Disposal after the circuit closed has nothing left to release in the browser.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error disposing Fourier visualization");
        }
    }
}

/// <summary>
/// Record class containing all data needed for visualizing a CTF series.
/// This includes experimental, simulated, and fit-quality curves and display range parameters.
/// </summary>
/// <param name="BinnedPixelSize">The pixel size after binning, used for Angstrom calculations</param>
/// <param name="MinRange">The minimum index for the fitting range</param>
/// <param name="MaxRange">The maximum index for the fitting range</param>
/// <param name="MinNormalized">The minimum normalized value for chart Y-axis scaling</param>
/// <param name="MaxNormalized">The maximum normalized value for chart Y-axis scaling</param>
/// <param name="ExperimentalValues">Array of experimental CTF values from the power spectrum</param>
/// <param name="SimulatedValues">Array of simulated CTF values from the fitted model</param>
/// <param name="QualityValues">Saved fit correlations; null entries mark unsupported frequencies</param>
/// <param name="SpecimenThicknessAngstrom">Estimated sample thickness, when available</param>
public record WarpSeriesData(
    float BinnedPixelSize,
    int MinRange,
    int MaxRange,
    float MinNormalized,
    float MaxNormalized,
    float[] ExperimentalValues,
    float[] SimulatedValues,
    float?[] QualityValues,
    decimal? SpecimenThicknessAngstrom);

/// <summary>
/// Configuration class for passing data to the JavaScript chart.
/// Contains all the parameters needed to create and configure the Chart.js visualization.
/// Properties match the structure expected by the JavaScript setupChart function.
/// </summary>
public class ChartConfig
{
    /// <summary>
    /// The pixel size after binning, used for converting between pixel and Angstrom space
    /// </summary>
    public float BinnedPixelSize { get; init; }
    
    /// <summary>
    /// The minimum index for the fitting range annotation in the chart
    /// </summary>
    public int MinRange { get; init; }
    
    /// <summary>
    /// The maximum index for the fitting range annotation in the chart
    /// </summary>
    public int MaxRange { get; init; }
    
    /// <summary>
    /// The minimum normalized value for chart Y-axis scaling
    /// </summary>
    public float MinNormalized { get; init; }
    
    /// <summary>
    /// The maximum normalized value for chart Y-axis scaling
    /// </summary>
    public float MaxNormalized { get; init; }
    
    /// <summary>
    /// Array of experimental CTF values from the power spectrum
    /// </summary>
    public float[] ExperimentalValues { get; init; } = Array.Empty<float>();
    
    /// <summary>
    /// Array of simulated CTF values from the fitted model
    /// </summary>
    public float[] SimulatedValues { get; init; } = Array.Empty<float>();

    /// <summary>Saved fit correlations; null entries mark unsupported frequencies.</summary>
    public float?[] QualityValues { get; init; } = Array.Empty<float?>();
}
