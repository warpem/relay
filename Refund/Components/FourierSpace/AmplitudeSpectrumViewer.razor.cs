using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkiaSharp;
using System.Collections.ObjectModel;
using Warp;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Components.FourierSpace;

/// <summary>
/// Component that displays the amplitude spectrum (power spectrum) of a cryo-EM movie or tilt series, 
/// allowing visualization of CTF (Contrast Transfer Function) fitting.
/// This component presents both the 2D power spectrum image and the 1D plot showing 
/// experimental data, fitted model, and quality metrics across spatial frequencies.
/// </summary>
public partial class AmplitudeSpectrumViewer : IAsyncDisposable
{
    [Inject] private ILogger<AmplitudeSpectrumViewer> Logger { get; set; }
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
    private string _imageSource = string.Empty;
    private readonly int _chartWidth = 400;
    private readonly int _chartHeight = 400;
    private bool _isChartInitialized;
    private bool _isLoading = true;
    
    // Tilt series specific properties
    private int _tiltCount = 0;
    private float[] _tiltAngles = [];
    private int _zeroTiltIndex = 0;
    private int _currentTiltIndex = 0;
    private Dictionary<int, string> _cachedTiltSpectrumImages = new();
    private Dictionary<int, WarpSeriesData> _cachedTiltSeriesData = new();

    // Reusable buffers for image processing
    private byte[]? _monoBuffer;
    private byte[]? _rightHalfBuffer;
    private SKBitmap? _reusableBitmap;
    
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
        if (MovieFilePath != null && MovieFilePath != _previousMoviePath)
        {
            _previousMoviePath = MovieFilePath;
            needsUpdate = true;
        }
        
        // Check if tilt series path has changed
        if (TiltSeriesPath != null && TiltSeriesPath != _previousTiltSeriesPath)
        {
            _previousTiltSeriesPath = TiltSeriesPath;
            _cachedTiltSpectrumImages.Clear();
            _cachedTiltSeriesData.Clear();
            needsUpdate = true;
        }
        
        if (!needsUpdate)
            return;
            
        _isLoading = true;
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
        _zeroTiltIndex = tiltSeries.IndicesSortedDose.First();
        _currentTiltIndex = 0;
        
        // Preload all tilt power spectrum images
        for (int tiltIndex = 0; tiltIndex < _tiltCount; tiltIndex++)
        {
            var powerSpectrumImage = GetTiltPowerSpectrumImage(tiltSeries, tiltIndex);
            _cachedTiltSpectrumImages[tiltIndex] = powerSpectrumImage;
            
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
            if (_cachedTiltSpectrumImages.TryGetValue(_currentTiltIndex, out var imageSource))
                _imageSource = imageSource;
                
            if (_cachedTiltSeriesData.TryGetValue(_currentTiltIndex, out var warpSeries))
            {
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
                        QualityValues = warpSeries.QualityValues.Select(v => float.IsNaN(v) ? 0f : MathF.Round(v, 2)).ToArray()
                    });
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
            var powerSpectrumImage = GetPowerSpectrumImage(movieFilePath);
            _imageSource = powerSpectrumImage;

            var warpSeries = GetWarpSeries(movieFilePath, FittingRangeMin, FittingRangeMax);

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
                    QualityValues = warpSeries.QualityValues.Select(v => float.IsNaN(v) ? 0f : MathF.Round(v, 2)).ToArray()
                });

            _isChartInitialized = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating movie visualization");
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Processes the power spectrum image from a movie file and converts it to a base64-encoded PNG string
    /// for display in the browser.
    /// </summary>
    /// <param name="basePath">Path to the movie file</param>
    /// <returns>A data URL containing the base64-encoded PNG of the power spectrum</returns>
    private string GetPowerSpectrumImage(string basePath)
    {
        var movie = new Movie(basePath);
        var header = MapHeader.ReadFromFile(movie.PowerSpectrumPath);
        var data = Image.FromFile(movie.PowerSpectrumPath).GetHostContinuousCopy();

        var width = header.Dimensions.X;
        var halfWidth = width / 2;
        var height = header.Dimensions.Y;

        var radiusMin2 = (int)(movie.OptionsCTF.RangeMin * halfWidth);
        radiusMin2 *= radiusMin2;
        var radiusMax2 = (int)(movie.OptionsCTF.RangeMax * halfWidth);
        radiusMax2 *= radiusMax2;

        // Calculate statistics
        double sum1 = 0, sum2 = 0;
        var samples = 0;
        var index = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var xCentered = x - halfWidth;
                var yCentered = height - 1 - y;
                var radius2 = xCentered * xCentered + yCentered * yCentered;

                if (radius2 >= radiusMin2 && radius2 <= radiusMax2)
                {
                    sum1 += data[index];
                    sum2 += data[index] * data[index];
                    samples++;
                }
                index++;
            }
        }

        var mean = (float)(sum1 / samples);
        var std = (float)(Math.Sqrt(samples * sum2 - sum1 * sum1) / samples);
        var valueMin = mean - 1.5f * std;
        var valueMax = mean + 3.0f * std;
        var range = valueMax - valueMin;

        if (range <= 0f)
            return "data:image/jpeg;base64," + Convert.ToBase64String(new byte[data.Length]);

        // Reuse or create mono buffer
        if (_monoBuffer == null || _monoBuffer.Length != data.Length)
            _monoBuffer = new byte[data.Length];

        // Convert to grayscale
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                _monoBuffer[(height - 1 - y) * width + x] = 
                    (byte)(Math.Max(Math.Min(1f, (data[y * width + x] - valueMin) / range), 0f) * 255f);
            }
        }

        // Reuse or create right half buffer
        var rightHalfSize = _monoBuffer.Length / 2;
        if (_rightHalfBuffer == null || _rightHalfBuffer.Length != rightHalfSize)
            _rightHalfBuffer = new byte[rightHalfSize];

        // Extract right half
        var dataIndex = 0;
        var rightIndex = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= halfWidth)
                    _rightHalfBuffer[rightIndex++] = _monoBuffer[dataIndex];
                dataIndex++;
            }
        }

        var pngBytes = CreatePngFromMonoBytes(_rightHalfBuffer, halfWidth, height);
        return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
    }
    
    /// <summary>
    /// Processes the power spectrum image from a tilt series file for a specific tilt and converts it to a base64-encoded PNG string
    /// for display in the browser.
    /// </summary>
    /// <param name="tiltSeries">The tilt series object</param>
    /// <param name="tiltIndex">The index of the tilt to process</param>
    /// <returns>A data URL containing the base64-encoded PNG of the power spectrum for the specified tilt</returns>
    private string GetTiltPowerSpectrumImage(TiltSeries tiltSeries, int tiltIndex)
    {
        var header = MapHeader.ReadFromFile(tiltSeries.PowerSpectrumPath);
        var powerSpectrumImage = Image.FromFile(tiltSeries.PowerSpectrumPath);
        
        // Get the slice corresponding to the specific tilt
        var data = powerSpectrumImage.GetHost(Intent.Read)[tiltIndex];

        var width = header.Dimensions.X;
        var halfWidth = width / 2;
        var height = header.Dimensions.Y;

        var radiusMin2 = (int)(tiltSeries.OptionsCTF.RangeMin * halfWidth);
        radiusMin2 *= radiusMin2;
        var radiusMax2 = (int)(tiltSeries.OptionsCTF.RangeMax * halfWidth);
        radiusMax2 *= radiusMax2;

        // Calculate statistics
        double sum1 = 0, sum2 = 0;
        var samples = 0;
        var index = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var xCentered = x - halfWidth;
                var yCentered = height - 1 - y;
                var radius2 = xCentered * xCentered + yCentered * yCentered;

                if (radius2 >= radiusMin2 && radius2 <= radiusMax2)
                {
                    sum1 += data[index];
                    sum2 += data[index] * data[index];
                    samples++;
                }
                index++;
            }
        }

        var mean = (float)(sum1 / samples);
        var std = (float)(Math.Sqrt(samples * sum2 - sum1 * sum1) / samples);
        var valueMin = mean - 1.5f * std;
        var valueMax = mean + 3.0f * std;
        var range = valueMax - valueMin;

        if (range <= 0f)
            return "data:image/jpeg;base64," + Convert.ToBase64String(new byte[data.Length]);

        // Reuse or create mono buffer
        if (_monoBuffer == null || _monoBuffer.Length != data.Length)
            _monoBuffer = new byte[data.Length];

        // Convert to grayscale
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                _monoBuffer[(height - 1 - y) * width + x] = 
                    (byte)(Math.Max(Math.Min(1f, (data[y * width + x] - valueMin) / range), 0f) * 255f);
            }
        }

        // Reuse or create right half buffer
        var rightHalfSize = _monoBuffer.Length / 2;
        if (_rightHalfBuffer == null || _rightHalfBuffer.Length != rightHalfSize)
            _rightHalfBuffer = new byte[rightHalfSize];

        // Extract right half
        var dataIndex = 0;
        var rightIndex = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= halfWidth)
                    _rightHalfBuffer[rightIndex++] = _monoBuffer[dataIndex];
                dataIndex++;
            }
        }

        var pngBytes = CreatePngFromMonoBytes(_rightHalfBuffer, halfWidth, height);
        return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
    }
    
    /// <summary>
    /// Converts a raw byte array of grayscale values to a compressed PNG image. 
    /// Each byte in the input array represents a grayscale pixel value (0-255).
    /// Uses Gray8 color type for efficient memory usage and reuses bitmap when possible.
    /// </summary>
    /// <param name="rawBytes">The raw grayscale bytes to convert</param>
    /// <param name="width">Width of the image in pixels</param>
    /// <param name="height">Height of the image in pixels</param>
    /// <returns>Byte array containing the PNG-encoded image data</returns>
    private byte[] CreatePngFromMonoBytes(byte[] rawBytes, int width, int height)
    {
        // Create or reuse SKBitmap with Gray8 color type (8-bit grayscale)
        if (_reusableBitmap == null || 
            _reusableBitmap.Width != width || 
            _reusableBitmap.Height != height || 
            _reusableBitmap.ColorType != SKColorType.Gray8)
        {
            _reusableBitmap?.Dispose();
            _reusableBitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        }
        
        // Get direct access to bitmap pixels
        IntPtr pixelsAddr = _reusableBitmap.GetPixels();
        
        // Copy bytes directly - Gray8 format means one byte per pixel
        System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, pixelsAddr, rawBytes.Length);

        // Create image data
        using var stream = SkBitmapToMemoryStream(_reusableBitmap);
        return stream.ToArray();
    }
    
    /// <summary>
    /// Encodes an SkiaSharp bitmap to a JPEG in a memory stream.
    /// </summary>
    /// <param name="bitmap">The bitmap to encode</param>
    /// <returns>A memory stream containing the JPEG-encoded bitmap</returns>
    private static MemoryStream SkBitmapToMemoryStream(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);

        var memoryStream = new MemoryStream();
        data.SaveTo(memoryStream);

        return memoryStream;
    }

    /// <summary>
    /// Extracts and prepares the CTF fitting data from a movie file for visualization.
    /// </summary>
    /// <param name="basePath">Path to the movie file</param>
    /// <param name="rangeMin">Minimum range for CTF fitting (fraction of Nyquist)</param>
    /// <param name="rangeMax">Maximum range for CTF fitting (fraction of Nyquist)</param>
    /// <returns>A WarpSeriesData object containing all data for the chart visualization</returns>
    private WarpSeriesData GetWarpSeries(string basePath, decimal rangeMin, decimal rangeMax)
    {
        var movie = new Movie(basePath);

        var fittingRangeMin = movie.CTF.PixelSize * 2 / rangeMin;
        var fittingRangeMax = movie.CTF.PixelSize * 2 / rangeMax;

        var experimentalData = movie.PS1D;
        var simulatedData = movie.Simulated1D;
        var scaleData = movie.SimulatedScale;
        var ctf = movie.CTF;

        var quality = ctf.EstimateQuality(experimentalData.Select(p => p.Y).ToArray(),
                                          scaleData.Interp(experimentalData.Select(p => p.X).ToArray()),
                                          (float)fittingRangeMin,
                                          16);

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
                                  quality
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
    private WarpSeriesData GetTiltWarpSeries(TiltSeries tiltSeries, int tiltIndex, decimal rangeMin, decimal rangeMax)
    {
        var fittingRangeMin = tiltSeries.CTF.PixelSize * 2 / rangeMin;
        var fittingRangeMax = tiltSeries.CTF.PixelSize * 2 / rangeMax;

        // Get the CTF for the specific tilt
        var ctf = tiltSeries.GetTiltCTF(tiltIndex);
        
        // Get the experimental and simulated data for the specific tilt
        var experimentalData = tiltSeries.TiltPS1D[tiltIndex];
        var simulatedData = tiltSeries.GetTiltSimulated1D(tiltIndex);
        var scaleData = tiltSeries.TiltSimulatedScale[tiltIndex];

        var quality = ctf.EstimateQuality(experimentalData.Select(p => p.Y).ToArray(),
                                          scaleData.Interp(experimentalData.Select(p => p.X).ToArray()),
                                          (float)fittingRangeMin,
                                          16);

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
                                  quality
        );
    }

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
            _monoBuffer = null;
            _rightHalfBuffer = null;
            _reusableBitmap?.Dispose();
            _reusableBitmap = null;
            _cachedTiltSpectrumImages.Clear();
            _cachedTiltSeriesData.Clear();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error disposing Fourier visualization");
        }
    }
}

/// <summary>
/// Record class containing all data needed for visualizing a CTF series.
/// This includes data for the experimental and simulated CTF curves, as well as
/// quality metrics and display range parameters.
/// </summary>
/// <param name="BinnedPixelSize">The pixel size after binning, used for Angstrom calculations</param>
/// <param name="MinRange">The minimum index for the fitting range</param>
/// <param name="MaxRange">The maximum index for the fitting range</param>
/// <param name="MinNormalized">The minimum normalized value for chart Y-axis scaling</param>
/// <param name="MaxNormalized">The maximum normalized value for chart Y-axis scaling</param>
/// <param name="ExperimentalValues">Array of experimental CTF values from the power spectrum</param>
/// <param name="SimulatedValues">Array of simulated CTF values from the fitted model</param>
/// <param name="QualityValues">Array of quality metrics for the fit at different spatial frequencies</param>
public record WarpSeriesData(
    float BinnedPixelSize,
    int MinRange,
    int MaxRange,
    float MinNormalized,
    float MaxNormalized,
    float[] ExperimentalValues,
    float[] SimulatedValues,
    float[] QualityValues);

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
    
    /// <summary>
    /// Array of quality metrics for the fit at different spatial frequencies
    /// </summary>
    public float[] QualityValues { get; init; } = Array.Empty<float>();
}