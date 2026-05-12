using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkiaSharp;
using Warp;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Components.MicrographViewer;

/// <summary>
/// A comprehensive viewer component for electron microscopy images (micrographs).
/// Provides functionality for viewing, zooming, panning, showing overlays such as masks and particle positions,
/// and editing masks and particles when permitted.
/// 
/// This component exposes an enum ParticleShapes that is used by ParticleControls to configure the 
/// display style of particles on micrographs. The ParticleShapes enum uses a bitwise flag system to 
/// allow displaying particles as circles, squares, or both simultaneously.
/// </summary>
public partial class MicrographViewer : ComponentBase, IDisposable
{
    private bool showControls = false;
    private string elementId = "micrograph-viewer-" + Guid.NewGuid().ToString("N");
    private ElementReference containerElement;
    private DotNetObjectReference<MicrographViewer> dotNetRef;
    private bool imageLoaded = false;
    private float aspectRatio = 1.0f;
    private bool isFitToViewport = true;
    
    /// <summary>
    /// Cancellation token source for managing async operations lifecycle
    /// </summary>
    private CancellationTokenSource _componentCts = new();
    
    /// <summary>
    /// Semaphore to limit concurrent image loading operations
    /// </summary>
    private readonly SemaphoreSlim _imageLoadingSemaphore = new(1, 1);
    
    /// <summary>
    /// Tracks the last parameter change to debounce rapid updates
    /// </summary>
    private CancellationTokenSource _parameterChangeCts = new();

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    [Inject]
    private ILogger<MicrographViewer> Logger { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Create a reference to this component instance for JS interop
            dotNetRef = DotNetObjectReference.Create(this);
            
            // Start observing resize events on the container element
            module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/MicrographViewer/MicrographViewer.razor.js");
                
            await module.InvokeVoidAsync("observeResize", containerElement, dotNetRef, 100); // 100ms debounce
                
            Width = MinWidth;
            Height = MinHeight;
        }
    }
    
    private bool isDisposed = false;
    private IJSObjectReference module;
    
    public void Dispose()
    {
        if (isDisposed)
            return;
            
        isDisposed = true;
            
        // Clean up the resize observer when the component is disposed
        if (JsRuntime != null && module != null)
        {
            try
            {
                module.InvokeVoidAsync("disposeResizeObserver", containerElement);
                module.DisposeAsync();
                dotNetRef?.Dispose();
            }
            catch
            {
                // Ignore exceptions during disposal
            }
        }
        
        // Clean up any pending cancellation token source
        showControlsCts?.Cancel();
        showControlsCts?.Dispose();
        showControlsCts = null;
        
        // Cancel and dispose component-level cancellation tokens
        _componentCts?.Cancel();
        _componentCts?.Dispose();
        
        _parameterChangeCts?.Cancel();
        _parameterChangeCts?.Dispose();
        
        // Dispose semaphores
        _imageLoadingSemaphore?.Dispose();
        
        // Clean up all bitmap resources
        DisposeBitmaps();
        
        // Clean up mask bitmap
        maskBitmap?.Dispose();
    }
    
    /// <summary>
    /// Callback method invoked from JavaScript when the container is resized
    /// </summary>
    [JSInvokable]
    public async Task OnResized(int newContainerWidth, int newContainerHeight)
    {
        // Apply minimum constraints
        newContainerWidth = Math.Max(newContainerWidth, MinWidth);
        newContainerHeight = Math.Max(newContainerHeight, MinHeight);
        
        if (newContainerWidth == 0 || newContainerHeight == 0)
            return;
            
        // Track if dimensions actually changed
        bool dimensionsChanged = false;
            
        // Calculate max dimensions that maintain aspect ratio
        if (imageLoaded && aspectRatio > 0)
        {
            // Calculate the available space
            float containerAspect = (float)newContainerWidth / newContainerHeight;
            
            int newMaxWidth, newMaxHeight;
            
            if (containerAspect > aspectRatio)
            {
                // Container is wider than image - height is the limiting factor
                newMaxHeight = newContainerHeight;
                newMaxWidth = (int)(newMaxHeight * aspectRatio);
            }
            else
            {
                // Container is taller than image - width is the limiting factor
                newMaxWidth = newContainerWidth;
                newMaxHeight = (int)(newMaxWidth / aspectRatio);
            }
            
            // Check if max dimensions have actually changed
            if (newMaxWidth != Width || newMaxHeight != Height)
            {
                Width = newMaxWidth;
                Height = newMaxHeight;
                dimensionsChanged = true;
            }
        }
        else
        {
            // No image loaded or no aspect ratio - just use available space
            if (newContainerWidth != Width || newContainerHeight != Height)
            {
                Width = newContainerWidth;
                Height = newContainerHeight;
                dimensionsChanged = true;
            }
        }
        
        // Only update viewport if dimensions actually changed
        if (dimensionsChanged)
        {
            previousViewportWidth = Width;
            previousViewportHeight = Height;
            
            // If currently using fit-to-viewport, maintain that zoom level when resizing
            if (isFitToViewport)
            {
                FitToViewport();
            }
            else
            {
                // Update viewport without changing zoom
                UpdateViewport();
            }
            
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Updates the viewport without changing the zoom level,
    /// keeping the same center point of the image.
    /// </summary>
    private void UpdateViewport()
    {
        if (previousViewportWidth == 0 || previousViewportHeight == 0)
        {
            previousViewportWidth = Width;
            previousViewportHeight = Height;
            return;
        }
        
        // Calculate the center point before update
        double centerX = (previousViewportWidth / 2.0 - translateX) / ZoomLevel;
        double centerY = (previousViewportHeight / 2.0 - translateY) / ZoomLevel;
        
        // Update the previous viewport dimensions
        previousViewportWidth = Width;
        previousViewportHeight = Height;
        
        // Adjust translations to keep the center point consistent
        translateX = Width / 2.0 - centerX * ZoomLevel;
        translateY = Height / 2.0 - centerY * ZoomLevel;
        
        // Update the scale bar
        UpdateScaleBar();
    }

    #region Overrides

    /// <summary>
    /// Handles parameter changes by loading and rendering the micrograph, mask, thumbnail, and particles as needed.
    /// Runs tasks asynchronously with debouncing to improve performance and responsiveness.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // Cancel any pending parameter change operations
        _parameterChangeCts.Cancel();
        _parameterChangeCts.Dispose();
        _parameterChangeCts = new CancellationTokenSource();
        
        // Debounce rapid parameter changes
        await Task.Delay(50, _parameterChangeCts.Token);
        
        var changeState = false;
        List<Task> tasks = new();

        if (MicrographPath != micrographPathLoaded)
        {
            isLoading = true;
            imageLoaded = false;
            StateHasChanged();

            micrographPathLoaded = MicrographPath;

            tasks.Add(LoadMicrographWithCancellation());

            changeState = true;
        }

        if (Width != previousViewportWidth || Height != previousViewportHeight)
        {
            if (isFitToViewport)
            {
                FitToViewport();
            }
            else
            {
                UpdateViewport();
            }

            changeState = true;
        }

        if (MaskPath != maskPathLoaded)
        {
            maskPathLoaded = MaskPath;

            tasks.Add(LoadMaskWithCancellation());

            changeState = true;
        }

        if (ThumbnailPath != thumbnailPathLoaded)
        {
            thumbnailPathLoaded = ThumbnailPath;

            tasks.Add(LoadThumbnailWithCancellation());

            changeState = true;
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Component is being disposed or parameters changed again
                Logger.LogDebug("Image loading operations cancelled for component disposal or parameter change");
                return;
            }
        }

        // Particles need micrograph dimensions to be loaded
        if (ParticleStarPath != particleStarPathLoaded)
        {
            particleStarPathLoaded = ParticleStarPath;
            LoadParticles();

            changeState = true;
        }

        if (changeState)
        {
            isLoading = false;
            
            // Force an aspect ratio adjustment immediately if an image was loaded
            if (imageLoaded && aspectRatio > 0)
            {
                // Ensure the resize occurs on the UI thread
                await InvokeAsync(() => OnResized(Width, Height));
            }
            
            StateHasChanged();
        }
    }

    #endregion

    #region Parameters

    /// <summary>
    /// Path to the micrograph image file to be displayed.
    /// </summary>
    [Parameter]
    public string MicrographPath { get; set; }

    /// <summary>
    /// Path to the mask file that will be overlaid on the micrograph.
    /// </summary>
    [Parameter]
    public string MaskPath { get; set; }

    /// <summary>
    /// Path to a thumbnail version of the micrograph for the minimap display.
    /// </summary>
    [Parameter]
    public string ThumbnailPath { get; set; }

    /// <summary>
    /// Path to a STAR file containing particle coordinates to be displayed on the micrograph.
    /// </summary>
    [Parameter]
    public string ParticleStarPath { get; set; }

    /// <summary>
    /// Minimum width of the viewer component in pixels.
    /// The actual width will be determined by the parent container.
    /// </summary>
    [Parameter]
    public int MinWidth { get; set; } = 800;

    /// <summary>
    /// Minimum height of the viewer component in pixels.
    /// The actual height will be determined by the parent container.
    /// </summary>
    [Parameter]
    public int MinHeight { get; set; } = 600;

    /// <summary>
    /// Controls whether the user is allowed to edit the mask overlay.
    /// </summary>
    [Parameter]
    public bool CanEditMask { get; set; } = true;

    /// <summary>
    /// Controls whether the user is allowed to edit particle positions.
    /// </summary>
    [Parameter]
    public bool CanEditParticles { get; set; } = true;

    /// <summary>
    /// Default diameter for particles in pixels.
    /// </summary>
    [Parameter]
    public double DefaultParticleDiameter { get; set; } = 100;

    /// <summary>
    /// Default box size for particles in pixels.
    /// </summary>
    [Parameter]
    public double DefaultParticleBoxSize { get; set; } = 200;

    /// <summary>
    /// Controls whether the user can modify particle display settings.
    /// </summary>
    [Parameter]
    public bool CanControlParticleDisplay { get; set; } = true;
    
    /// <summary>
    /// Current width of the component in pixels.
    /// This is updated dynamically based on the parent container's size.
    /// </summary>
    public int Width { get; private set; } = 800;

    /// <summary>
    /// Current height of the component in pixels.
    /// This is updated dynamically based on the parent container's size.
    /// </summary>
    public int Height { get; private set; } = 600;

    #endregion

    #region Micrograph

    private bool isLoading = false;

    private string micrographPathLoaded;
    private float[] micrographData = null;
    private int2 micrographDims = new(2);
    private float pixelSize = 1;

    private float micrographRangeStd = 2.5f;

    private bool showScaleBar = true;
    private int2 scaleBarDims = new(2);
    private string scaleBarLabel = "";

    // Mipmap data structure
    private class MipmapLevel
    {
        public float[] Data { get; set; }
        public int2 Dimensions { get; set; }
        public float Mean { get; set; }
        public float StdDev { get; set; }
        public SKBitmap Bitmap { get; set; }
        public string ImageBase64 { get; set; }
    }

    // List of mipmap levels (level 0 is original resolution)
    private List<MipmapLevel> mipmapLevels = new();
    
    // Current visible mipmaps and their opacities
    private int currentLowerMipmapLevel = 0;
    private int currentUpperMipmapLevel = 0;
    private float mipmapBlendFactor = 0f;

    // Cached bitmap info for reuse
    private Dictionary<int, SKBitmap> cachedBitmaps = new();

    /// <summary>
    /// Loads a micrograph from a file and prepares it for display.
    /// Handles both regular paths and layer-specific paths (format: "layer@path").
    /// Creates a mipmap pyramid for multi-resolution display.
    /// </summary>
    private void LoadMicrograph()
    {
        if (string.IsNullOrWhiteSpace(MicrographPath))
            return;

        var watch = Stopwatch.StartNew();

        string micrographPath = MicrographPath;
        int layer = 0;
        if (MicrographPath.Contains('@'))
        {
            string[] parts = MicrographPath.Split('@', StringSplitOptions.RemoveEmptyEntries);
            layer = int.Parse(parts[0]);
            micrographPath = parts[1];
        }

        if (!File.Exists(micrographPath))
            return;

        var header = MapHeader.ReadFromFile(micrographPath);
        int2 newDims = new int2(header.Dimensions);
        pixelSize = header.PixelSize.X;

        particleBoxSize = (decimal)DefaultParticleBoxSize;
        particleDiameter = (decimal)DefaultParticleDiameter;

        // Calculate thumbnail dimensions with proper aspect ratio
        const float maxThumbnailSize = 140f;
        float thumbnailScale = maxThumbnailSize / Math.Max(newDims.X, newDims.Y);
        thumbnailDims = new float2(newDims) * thumbnailScale; // This preserves aspect ratio

        // Check if dimensions have changed - if so, clean up existing resources
        bool dimensionsChanged = micrographDims.X != newDims.X || micrographDims.Y != newDims.Y;
        if (dimensionsChanged)
        {
            // Store new dimensions
            micrographDims = newDims;
            
            // Clean up existing mipmaps before creating new ones
            CleanupMipmaps();
        }

        // Reuse or create the data buffer
        if (micrographData == null || micrographData.Length != micrographDims.Elements())
            micrographData = new float[micrographDims.Elements()];

        // Read the micrograph data
        IOHelper.ReadMapFloat(micrographPath, [layer], null, [micrographData]);

        // Generate mipmap pyramid
        GenerateMipmapPyramid();

        Logger.LogDebug("Loaded micrograph in {ElapsedMs} ms", watch.ElapsedMilliseconds);
    }
    
    /// <summary>
    /// Cleans up resources used by mipmaps
    /// </summary>
    private void CleanupMipmaps()
    {
        // Clean up bitmaps in mipmap levels
        foreach (var level in mipmapLevels)
        {
            // Don't dispose bitmaps stored in the cache
            if (level.Bitmap != null && !cachedBitmaps.ContainsValue(level.Bitmap))
            {
                level.Bitmap.Dispose();
            }
        }
        
        mipmapLevels.Clear();
    }
    
    /// <summary>
    /// Generates a pyramid of mipmaps (progressively downsampled versions of the image).
    /// Each mipmap level is half the size of the previous level, down to a minimum size.
    /// Each level has its own statistics used for consistent contrast.
    /// Reuses cached bitmaps when dimensions match.
    /// </summary>
    private void GenerateMipmapPyramid()
    {
        var watch = Stopwatch.StartNew();
        
        // Clear any existing mipmaps but preserve the bitmaps for reuse
        CleanupMipmaps();
        
        // Add the original resolution as level 0
        float2 meanStd = MathHelper.MeanAndStd(micrographData);
        float originalMean = meanStd.X;
        float originalStdDev = meanStd.Y;
        
        mipmapLevels.Add(new MipmapLevel
        {
            Data = micrographData,
            Dimensions = micrographDims,
            Mean = originalMean,
            StdDev = originalStdDev,
            Bitmap = GetOrCreateBitmap(micrographDims.X, micrographDims.Y, 0)
        });
        
        // Generate progressively smaller levels
        int2 currentDims = micrographDims;
        float[] currentData = micrographData;
        int levelIndex = 0;
        
        // Continue generating mipmaps until a minimum size is reached (512x512 or smaller)
        while (Math.Max(currentDims.X, currentDims.Y) > 512)
        {
            levelIndex++;
            
            // Generate the next mipmap level by binning the current one
            var (nextData, nextDims) = MathHelper.Bin2(currentData, currentDims);
            
            // Calculate statistics for this level
            float2 levelMeanStd = MathHelper.MeanAndStd(nextData);
            
            // Add this level to our pyramid
            mipmapLevels.Add(new MipmapLevel
            {
                Data = nextData,
                Dimensions = nextDims,
                Mean = levelMeanStd.X,
                StdDev = levelMeanStd.Y,
                Bitmap = GetOrCreateBitmap(nextDims.X, nextDims.Y, levelIndex)
            });
            
            // Prepare for next iteration
            currentData = nextData;
            currentDims = nextDims;
        }
        
        // Render all mipmap levels
        foreach (var level in mipmapLevels)
        {
            RenderMipmapLevel(level);
        }
        
        Logger.LogDebug("Generated {MipmapLevelCount} mipmap levels in {ElapsedMs} ms", mipmapLevels.Count, watch.ElapsedMilliseconds);
    }
    
    /// <summary>
    /// Gets a bitmap from the cache or creates a new one for the given dimensions.
    /// </summary>
    /// <param name="width">Width of the bitmap</param>
    /// <param name="height">Height of the bitmap</param>
    /// <param name="levelIndex">Index of the mipmap level</param>
    /// <returns>A bitmap suited for the requested dimensions</returns>
    private SKBitmap GetOrCreateBitmap(int width, int height, int levelIndex)
    {
        // Create a unique key for the bitmap based on dimensions and level
        int key = levelIndex;
        
        // Check if we have a cached bitmap that matches
        if (cachedBitmaps.TryGetValue(key, out SKBitmap cachedBitmap))
        {
            // If the dimensions match, reuse the bitmap
            if (cachedBitmap.Width == width && cachedBitmap.Height == height)
                return cachedBitmap;
            
            // Otherwise, dispose the old one
            cachedBitmap.Dispose();
            cachedBitmaps.Remove(key);
        }
        
        // Create a new bitmap and cache it
        SKBitmap newBitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        cachedBitmaps[key] = newBitmap;
        return newBitmap;
    }
    
    /// <summary>
    /// Renders a single mipmap level to a bitmap with consistent contrast.
    /// </summary>
    /// <param name="level">The mipmap level to render</param>
    private void RenderMipmapLevel(MipmapLevel level)
    {
        if (level.Data == null || level.Data.Length != level.Dimensions.Elements() || level.Bitmap == null)
        {
            return;
        }

        // Calculate contrast range based on mean and standard deviation for this level
        float min = level.Mean - micrographRangeStd * level.StdDev;
        float max = level.Mean + micrographRangeStd * level.StdDev;
        float scale = max - min != 0 ? 1f / (max - min) : 0;

        int width = level.Dimensions.X;
        int height = level.Dimensions.Y;

        unsafe
        {
            IntPtr bitmapPtr = level.Bitmap.GetPixels();
            var bitmapBytePtr = (byte*)bitmapPtr.ToPointer();
            var tempArray = new byte[16];

            fixed (float* hostPtr = level.Data)
            fixed (byte* tempArrayPtr = tempArray)
            {
                if (!float.IsNaN(max) && !float.IsNaN(min))
                {
                    if (Avx2.IsSupported)
                    {
                        // Optimized SIMD path for AVX2-enabled processors
                        var vNewMin = Vector256.Create(min);
                        var vScale = Vector256.Create(scale);
                        var v255 = Vector256.Create(255.0f);
                        Vector256<float> vZero = Vector256<float>.Zero;
                        var vOne = Vector256.Create(1.0f);

                        for (var y = 0; y < height; y++)
                        {
                            int x;

                            for (x = 0; x <= width - 8; x += 8)
                            {
                                Vector256<float> vHost = Avx.LoadVector256(hostPtr + (height - 1 - y) * width + x);
                                Vector256<float> vValue = Avx.Subtract(vHost, vNewMin);
                                vValue = Avx.Multiply(vValue, vScale);
                                vValue = Avx.Max(vValue, vZero);
                                vValue = Avx.Min(vValue, vOne);
                                Vector256<float> vColor = Avx.Multiply(vValue, v255);

                                // Convert each Vector128<float> to Vector128<int>
                                Vector128<int> vColorIntLow = Sse2.ConvertToVector128Int32(vColor.GetLower());
                                Vector128<int> vColorIntHigh = Sse2.ConvertToVector128Int32(vColor.GetUpper());

                                // Pack the results into Vector128<short>
                                Vector128<short> vColorShort = Sse2.PackSignedSaturate(vColorIntLow, vColorIntHigh);

                                // Pack the results into Vector128<byte>
                                Vector128<byte> vColorByte = Sse2.PackUnsignedSaturate(vColorShort, vColorShort);

                                // Store only the lower 64 bits (8 bytes)
                                Sse2.Store(tempArrayPtr, vColorByte);
                                Marshal.Copy(tempArray, 0, (IntPtr)(bitmapBytePtr + y * width + x), 8);
                            }

                            // Handle remaining elements
                            for (; x < width; x++)
                            {
                                int index = (height - 1 - y) * width + x;
                                float value = (hostPtr[index] - min) * scale;
                                var color = (byte)(Math.Clamp(value, 0, 1) * 255);
                                bitmapBytePtr[y * width + x] = color;
                            }
                        }
                    }
                    else
                    {
                        // Standard path for processors without AVX2 support
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                float value = (hostPtr[(height - 1 - y) * width + x] - min) * scale;
                                var color = (byte)(Math.Clamp(value, 0, 1) * 255);
                                bitmapBytePtr[y * width + x] = color;
                            }
                        }
                    }
                }
                else
                {
                    Logger.LogWarning("Invalid min/max values (NaN) encountered during mipmap rendering");
                }
            }
        }

        using var image = SKImage.FromBitmap(level.Bitmap);
        using SKData imageData = image.Encode(SKEncodedImageFormat.Jpeg, 70);
        level.ImageBase64 = "data:image/jpeg;base64," + Convert.ToBase64String(imageData.ToArray());
    }

    /// <summary>
    /// Renders the micrograph by selecting the appropriate mipmap levels based on the current zoom.
    /// Updates mipmapLevel properties without regenerating the mipmaps themselves.
    /// </summary>
    private void RenderMicrograph()
    {
        if (mipmapLevels.Count == 0)
            return;
            
        var watch = Stopwatch.StartNew();
        
        // Calculate which mipmap levels to use based on current zoom
        UpdateMipmapSelection();
        
        Logger.LogDebug("Updated micrograph rendering in {ElapsedMs} ms. Zoom: {ZoomLevel}, Mipmap levels: {LowerLevel}/{UpperLevel}, Blend: {BlendFactor}", 
            watch.ElapsedMilliseconds, ZoomLevel, currentLowerMipmapLevel, currentUpperMipmapLevel, mipmapBlendFactor);
    }
    
    /// <summary>
    /// Determines which mipmap levels to display based on the current zoom level.
    /// Calculates the blend factor between the two levels for smooth transitions.
    /// </summary>
    private void UpdateMipmapSelection()
    {
        if (mipmapLevels.Count <= 1)
        {
            // Only one mipmap level available, use it
            currentLowerMipmapLevel = 0;
            currentUpperMipmapLevel = 0;
            mipmapBlendFactor = 0;
            return;
        }
            
        // Calculate the ideal mipmap level based on zoom
        // When ZoomLevel = 1.0, we're at original resolution (level 0)
        // When ZoomLevel = 0.5, we want level 1 (half size)
        // When ZoomLevel = 0.25, we want level 2 (quarter size)
        double idealLevel = Math.Max(0, -Math.Log2(ZoomLevel));
        
        // Clamp to available levels
        idealLevel = Math.Clamp(idealLevel, 0, mipmapLevels.Count - 1);
        
        // Get the two closest levels
        int lowerLevel = (int)Math.Floor(idealLevel);
        int upperLevel = (int)Math.Ceiling(idealLevel);
        
        // Make sure they're different for interpolation, unless we're at an exact level
        if (lowerLevel == upperLevel && lowerLevel < mipmapLevels.Count - 1)
            upperLevel = lowerLevel + 1;
            
        // Calculate blend factor (0.0 = use lower level only, 1.0 = use upper level only)
        float blendFactor = (float)(idealLevel - lowerLevel);
        
        // Update the current mipmap selection
        currentLowerMipmapLevel = lowerLevel;
        currentUpperMipmapLevel = upperLevel;
        mipmapBlendFactor = blendFactor;
        
    }
    
    /// <summary>
    /// Gets the lower mipmap level for display (higher resolution).
    /// </summary>
    private MipmapLevel LowerMipmap 
    { 
        get
        {
            if (mipmapLevels.Count == 0)
                return null;
                
            // Make sure index is in bounds
            int index = Math.Clamp(currentLowerMipmapLevel, 0, mipmapLevels.Count - 1);
            return mipmapLevels[index];
        }
    }
    
    /// <summary>
    /// Gets the upper mipmap level for display (lower resolution).
    /// </summary>
    private MipmapLevel UpperMipmap
    {
        get
        {
            if (mipmapLevels.Count == 0)
                return null;
                
            // Make sure index is in bounds
            int index = Math.Clamp(currentUpperMipmapLevel, 0, mipmapLevels.Count - 1);
            return mipmapLevels[index];
        }
    }
    
    /// <summary>
    /// Properly disposes all SKBitmap objects
    /// </summary>
    private void DisposeBitmaps()
    {
        // Dispose all cached bitmaps
        foreach (var bitmap in cachedBitmaps.Values)
        {
            bitmap?.Dispose();
        }
        cachedBitmaps.Clear();
        
        // Clear mipmap levels without disposing bitmaps (already handled above)
        mipmapLevels.Clear();
    }

    /// <summary>
    /// Updates the scale bar dimensions and label based on the current zoom level and pixel size.
    /// Implements an algorithm to create a visually pleasing scale bar with rounded values.
    /// Adapted from Napari's implementation which was in turn adapted from Google Maps.
    /// </summary>
    private void UpdateScaleBar()
    {
        float scaledPixelSizeNm = pixelSize / (float)ZoomLevel / 10;
        var resolutions = new List<int> { 1, 2, 3, 5, 8, 10 };  // Nice round numbers for scale bars
        var targetLengthPixels = 80f;  // Target length in pixels
        float targetLengthNm = targetLengthPixels * scaledPixelSizeNm;
        float targetLengthNmLog10 = MathF.Log10(targetLengthNm);
        
        // Find the closest nice round number to make a readable scale bar
        float chosenResolution = resolutions.MinBy(r => Math.Abs(MathF.Log10(r) - targetLengthNmLog10 % 1.0));
        float targetLengthNmRounded = chosenResolution * MathF.Pow(10, MathF.Floor(targetLengthNmLog10));
        float targetLengthPixelsRounded = targetLengthNmRounded / scaledPixelSizeNm;

        scaleBarLabel = $"{targetLengthNmRounded} nm";
        scaleBarDims = new int2((int)Math.Round(targetLengthPixelsRounded), 20);
    }

    /// <summary>
    /// Event handler for toggling the scale bar visibility.
    /// </summary>
    /// <param name="isToggled">True to show the scale bar, false to hide it.</param>
    private void OnScaleBarToggled(bool isToggled)
    {
        showScaleBar = isToggled;
        StateHasChanged();
    }

    #endregion

    #region Zoom and Pan

    private bool isPanning = false;
    private Point lastMousePosition;
    private ElementReference viewportRef;

    private int previousViewportWidth;
    private int previousViewportHeight;

    private double zoomLevel = 1.0;

    /// <summary>
    /// Gets or sets the current zoom level. Updates the scale bar when the zoom level changes.
    /// </summary>
    private double ZoomLevel
    {
        get => zoomLevel;
        set
        {
            if (value != zoomLevel)
            {
                zoomLevel = value;
                UpdateScaleBar();
            }
        }
    }

    /// <summary>
    /// Horizontal translation of the micrograph in screen pixels.
    /// </summary>
    private double translateX = 0;
    
    /// <summary>
    /// Vertical translation of the micrograph in screen pixels.
    /// </summary>
    private double translateY = 0;

    /// <summary>
    /// Zoom level that fits the entire micrograph in the viewport.
    /// Used as a reference for minimum zoom level calculations.
    /// </summary>
    private double fitZoomLevel = 1.0;

    /// <summary>
    /// Event handler for zoom level changes from the ZoomControls component.
    /// Maintains the center point of the viewport when zooming.
    /// </summary>
    /// <param name="newZoomLevel">The new zoom level to apply.</param>
    private void OnZoomChanged(double newZoomLevel)
    {
        // If zoom changes, we're no longer in "fit to viewport" mode
        isFitToViewport = false;
        
        // Calculate the center point before zoom change
        double centerX = (Width / 2.0 - translateX) / ZoomLevel;
        double centerY = (Height / 2.0 - translateY) / ZoomLevel;

        // Update zoom level with appropriate constraints
        ZoomLevel = Math.Clamp(newZoomLevel, Math.Min(0.05, fitZoomLevel), Math.Max(2, fitZoomLevel));

        // Adjust translations to keep the center point consistent
        translateX = Width / 2.0 - centerX * ZoomLevel;
        translateY = Height / 2.0 - centerY * ZoomLevel;
        
        // Update mipmap selection based on new zoom level
        UpdateMipmapSelection();

        StateHasChanged();
    }

    /// <summary>
    /// Event handler for the "Fit to Viewport" button in ZoomControls.
    /// Adjusts the zoom and translation to fit the entire micrograph in the viewport.
    /// </summary>
    private void OnFitToViewport()
    {
        FitToViewport();
        isFitToViewport = true;
        StateHasChanged();
    }

    /// <summary>
    /// Calculates and applies the zoom level and translation needed to fit 
    /// the entire micrograph in the viewport.
    /// Centers the micrograph in the viewport.
    /// </summary>
    private void FitToViewport()
    {
        previousViewportWidth = Width;
        previousViewportHeight = Height;

        // Make sure we have valid dimensions
        if (micrographDims.X <= 0 || micrographDims.Y <= 0 || Width <= 0 || Height <= 0)
            return;

        // Calculate scale factors to fit width and height
        double scaleX = (double)Width / micrographDims.X;
        double scaleY = (double)Height / micrographDims.Y;
        
        // Use the smaller scale to ensure entire micrograph fits
        ZoomLevel = Math.Min(scaleX, scaleY);
        
        // Center the micrograph in the viewport
        translateX = (Width - micrographDims.X * ZoomLevel) / 2;
        translateY = (Height - micrographDims.Y * ZoomLevel) / 2;

        // Store the fit zoom level for reference
        fitZoomLevel = ZoomLevel;
        
        // Update mipmap selection based on new zoom level
        UpdateMipmapSelection();
    }

    #endregion

    #region Mask

    private bool showMask = true;
    private string maskPathLoaded;
    private float[] maskData = null;
    private SKBitmap maskBitmap = null;
    private string maskImageBase64 = null;
    private int2 maskDims = new(2);

    private bool isPaintingMask = false;
    private decimal maskBrushDiameter = 300;

    /// <summary>
    /// Loads a binary mask from a file to overlay on the micrograph.
    /// The mask typically represents selected or excluded regions in the micrograph.
    /// </summary>
    private void LoadMask()
    {
        if (string.IsNullOrWhiteSpace(MaskPath) || !File.Exists(MaskPath))
        {
            return;
        }

        var watch = Stopwatch.StartNew();

        var header = MapHeader.ReadFromFile(MaskPath);

        maskDims = new int2(header.Dimensions);

        //if (MaskData == null || MaskData.Length != header.Dimensions.Elements())
        //    MaskData = new float[header.Dimensions.Elements()];

        maskData = IOHelper.ReadMapFloat(MaskPath)[0];

        Logger.LogDebug("Loaded mask in {ElapsedMs} ms", watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Renders the mask data as a displayable image overlay.
    /// Converts the float mask data (values 0.0-1.0) to a grayscale bitmap
    /// and then to a base64-encoded JPEG for display.
    /// </summary>
    private void RenderMask()
    {
        if (maskData == null || maskData.Length != maskDims.Elements())
        {
            return;
        }

        var watch = Stopwatch.StartNew();

        if (maskBitmap == null || maskBitmap.Width != maskDims.X || maskBitmap.Height != maskDims.Y)
        {
            maskBitmap?.Dispose();
            maskBitmap = new SKBitmap(maskDims.X, maskDims.Y, SKColorType.Gray8, SKAlphaType.Opaque);
        }

        int width = maskDims.X;
        int height = maskDims.Y;

        unsafe
        {
            IntPtr pixelsPtr = maskBitmap.GetPixels();
            var pixelsPtrByte = (byte*)pixelsPtr.ToPointer();

            fixed (float* dataPtr = maskData)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        pixelsPtrByte[y * width + x] = (byte)(Math.Clamp(dataPtr[(height - 1 - y) * width + x], 0, 1) * 255);
                    }
                }
            }
        }

        using var image = SKImage.FromBitmap(maskBitmap);
        using SKData imageData = image.Encode(SKEncodedImageFormat.Jpeg, 70);

        //using (var Stream = File.OpenWrite("d_mask.jpg"))
        //    imageData.SaveTo(Stream);

        maskImageBase64 = "data:image/jpeg;base64," + Convert.ToBase64String(imageData.ToArray());

        Logger.LogDebug("Rendered mask in {ElapsedMs} ms", watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Event handler for changes to mask settings from the MaskControls component.
    /// Updates visibility, painting mode, and brush diameter.
    /// Also manages interaction with particle picking mode.
    /// </summary>
    /// <param name="settings">Settings object containing the updated mask-related values.</param>
    private void OnMaskSettingsChanged(MaskControls.MaskSettings settings)
    {
        showMask = settings.IsShowing;
        isPaintingMask = settings.IsPainting;
        maskBrushDiameter = settings.Diameter;

        // Always show mask when painting is active
        showMask |= isPaintingMask;

        // Disable particle picking when mask painting is active
        if (isPaintingMask && isPickingParticles)
        {
            isPickingParticles = false;
        }

        StateHasChanged();
    }

    #endregion

    #region Particles

    /// <summary>
    /// Defines the different shapes that can be used to display particles.
    /// Can be combined using bitwise OR to show multiple shapes.
    /// </summary>
    [Flags]
    public enum ParticleShapes
    {
        /// <summary>No shape (particles are hidden)</summary>
        None = 0,
        
        /// <summary>Display particles as circles</summary>
        Circle = 1,
        
        /// <summary>Display particles as squares (box)</summary>
        Square = 1 << 1,
        
        /// <summary>Display particles as both circles and squares</summary>
        Both = Circle|Square
    }

    private bool showParticles = true;
    private string particleStarPathLoaded;
    private List<float2> particlePositions = new();

    private bool isPickingParticles = false;

    private string particleColor = "#FFF700";
    private ParticleShapes particleShape = ParticleShapes.Circle;
    private decimal particleDiameter = 100;
    private decimal particleBoxSize = 200;
    private double particleStrokeWidth = 2;

    /// <summary>
    /// Loads particle coordinates from a STAR file to display on the micrograph.
    /// Handles both normalized (0-1 range) and absolute coordinates.
    /// Also flips Y coordinates to match the display convention.
    /// </summary>
    private void LoadParticles()
    {
        particlePositions.Clear();

        if (string.IsNullOrWhiteSpace(ParticleStarPath) || !File.Exists(ParticleStarPath))
            return;

        // Load X,Y coordinates from the STAR file
        float2[] positions = Star.LoadFloat2(ParticleStarPath, "rlnCoordinateX", "rlnCoordinateY");

        // Check if coordinates are normalized (in 0-1 range)
        bool areNormalized = positions.Select(v => Math.Max(v.X, v.Y)).Max() < 1.1;

        // Convert normalized coordinates to absolute pixel positions if needed
        if (areNormalized)
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i].X *= micrographDims.X - 1;
                positions[i].Y *= micrographDims.Y - 1;
            }

        // Flip Y coordinates to match display convention (origin at top-left)
        for (var i = 0; i < positions.Length; i++)
            positions[i].Y = micrographDims.Y - 1 - positions[i].Y;

        particlePositions.AddRange(positions);
    }

    /// <summary>
    /// Event handler for changes to particle settings from the ParticleControls component.
    /// Updates picking mode, particle appearance, and handles interaction with mask painting mode.
    /// </summary>
    /// <param name="settings">Settings object containing the updated particle-related values.</param>
    private void OnParticleSettingsChanged(ParticleControls.ParticleSettings settings)
    {
        isPickingParticles = settings.IsPicking;
        particleDiameter = settings.Diameter;
        particleBoxSize = settings.BoxSize;
        particleShape = settings.Shape;
        particleColor = settings.Color;

        // Always show circle shape when in picking mode for better visibility
        if (isPickingParticles)
        {
            particleShape |= ParticleShapes.Circle;
        }

        // Disable mask painting when particle picking is active
        if (isPickingParticles && isPaintingMask)
        {
            isPaintingMask = false;
        }

        StateHasChanged();
    }

    #endregion

    #region Mouse events

    /// <summary>
    /// Event handler for mouse button press.
    /// Initiates panning when the middle mouse button is pressed.
    /// </summary>
    /// <param name="e">Mouse event arguments containing button and position information.</param>
    private void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == 1) // Middle mouse button
        {
            isPanning = true;
            lastMousePosition = new Point((int)e.ClientX, (int)e.ClientY);
        }
    }

    /// <summary>
    /// Event handler for mouse button release.
    /// Stops panning when the middle mouse button is released.
    /// </summary>
    /// <param name="e">Mouse event arguments containing button information.</param>
    private void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == 1) // Middle mouse button
        {
            isPanning = false;
        }
    }

    /// <summary>
    /// Event handler for mouse movement.
    /// Updates translation values when panning, moving the micrograph with the mouse.
    /// Also handles the case where buttons are released outside the component.
    /// </summary>
    /// <param name="e">Mouse event arguments containing position and button state information.</param>
    private void OnMouseMove(MouseEventArgs e)
    {
        if (isPanning)
        {
            // Check if middle mouse button (bit value 4) is still pressed
            if ((e.Buttons&4) == 0)
            {
                isPanning = false;
                return;
            }

            // Calculate the change in mouse position
            double deltaX = e.ClientX - lastMousePosition.X;
            double deltaY = e.ClientY - lastMousePosition.Y;

            // Update translation values to move the micrograph
            translateX += deltaX;
            translateY += deltaY;

            // Update the last known position
            lastMousePosition = new Point((int)e.ClientX, (int)e.ClientY);

            StateHasChanged();
        }
    }

    /// <summary>
    /// Event handler for mouse wheel movement.
    /// Adjusts zoom level while maintaining the center point of the viewport.
    /// </summary>
    /// <param name="e">Wheel event arguments containing delta information.</param>
    private void OnMouseWheel(WheelEventArgs e)
    {
        // When zooming with mouse wheel, we're no longer in "fit to viewport" mode
        isFitToViewport = false;
        
        // Store current zoom level for calculations
        double oldZoomLevel = ZoomLevel;
        
        // Calculate zoom factor based on wheel delta, with larger deltas causing larger changes
        double zoomChange = 1 + Math.Abs(e.DeltaY) / 10.0 * 0.2;
        
        // Apply zoom in or out based on wheel direction
        zoomChange = e.DeltaY < 0 ? zoomChange : 1 / zoomChange;
        
        // Apply zoom with constraints
        ZoomLevel = Math.Clamp(ZoomLevel * zoomChange, Math.Min(0.05, fitZoomLevel), Math.Max(2, fitZoomLevel));

        // Keep the center of the viewport at the same position in the micrograph
        double centerX = (Width / 2.0 - translateX) / oldZoomLevel;
        double centerY = (Height / 2.0 - translateY) / oldZoomLevel;
        translateX = Width / 2.0 - centerX * ZoomLevel;
        translateY = Height / 2.0 - centerY * ZoomLevel;
        
        // Update mipmap selection based on new zoom level
        UpdateMipmapSelection();

        StateHasChanged();
    }

    /// <summary>
    /// Cancellation token source for the control visibility delay.
    /// </summary>
    private CancellationTokenSource showControlsCts;
    
    /// <summary>
    /// Delay in milliseconds before showing or hiding controls.
    /// </summary>
    private const int DelayMilliseconds = 200;

    /// <summary>
    /// Helper method to show or hide controls with a delay.
    /// Uses a cancellation token to prevent rapid toggling.
    /// </summary>
    /// <param name="newValue">True to show controls, false to hide them.</param>
    private async Task ChangeShowControlsWithDelayAsync(bool newValue)
    {
        // Cancel any previous hiding operation
        showControlsCts?.Cancel();

        // Create a new cancellation token for this operation
        showControlsCts = new CancellationTokenSource();

        try
        {
            // Wait for a short delay before showing the controls
            await Task.Delay(DelayMilliseconds, showControlsCts.Token);

            if (showControls != newValue)
            {
                showControls = newValue;
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
            // If the task was canceled, do nothing
        }
    }

    /// <summary>
    /// Event handler for mouse entering the component.
    /// Shows the controls after a short delay.
    /// </summary>
    /// <param name="e">Mouse event arguments.</param>
    private async Task OnMouseOver(MouseEventArgs e)
    {
        await ChangeShowControlsWithDelayAsync(true);
    }

    /// <summary>
    /// Event handler for mouse leaving the component.
    /// Hides the controls after a short delay.
    /// </summary>
    /// <param name="e">Mouse event arguments.</param>
    private async Task OnMouseOut(MouseEventArgs e)
    {
        await ChangeShowControlsWithDelayAsync(false);
    }

    /// <summary>
    /// Prevents controls from being hidden when the mouse is over specific UI elements.
    /// Used for elements like color pickers that need to remain visible.
    /// </summary>
    /// <param name="e">Mouse event arguments.</param>
    private void PreventHide(MouseEventArgs e)
    {
        // Cancel any hide operation when the mouse is over the color picker
        showControlsCts?.Cancel();
    }

    /// <summary>
    /// Helper class to store element dimensions from JavaScript.
    /// Used for coordinate calculations.
    /// </summary>
    private class DomRect
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    #endregion

    #region Minimap

    private string thumbnailPathLoaded;
    private string thumbnailImageBase64 = null;
    private float2 thumbnailDims = new(2);

    /// <summary>
    /// Loads a thumbnail image file for the minimap display.
    /// Converts the image to a base64-encoded data URL for inline display.
    /// </summary>
    private void LoadThumbnail()
    {
        if (string.IsNullOrWhiteSpace(ThumbnailPath) || !File.Exists(ThumbnailPath))
        {
            return;
        }

        byte[] imageBytes = File.ReadAllBytes(ThumbnailPath);
        string base64String = Convert.ToBase64String(imageBytes);
        string mimeType = GetMimeType(ThumbnailPath);

        thumbnailImageBase64 = $"data:{mimeType};base64,{base64String}";
    }

    /// <summary>
    /// Determines the MIME type from a file extension.
    /// Used to properly format the data URL for images.
    /// </summary>
    /// <param name="fileName">Path or filename with extension.</param>
    /// <returns>The corresponding MIME type.</returns>
    private string GetMimeType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", 
            ".png" => "image/png", 
            ".gif" => "image/gif", 
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Event handler for clicks on the minimap.
    /// Adjusts the main viewport to center on the clicked location.
    /// </summary>
    /// <param name="x">X coordinate in image space.</param>
    /// <param name="y">Y coordinate in image space.</param>
    private void OnMiniMapClick(double x, double y)
    {
        // Center the viewport on the clicked position in the mini-map
        translateX = -x * ZoomLevel + Width / 2.0;
        translateY = -y * ZoomLevel + Height / 2.0;
        StateHasChanged();
    }

    /// <summary>
    /// Calculates the rectangle representing the current viewport position
    /// and size within the full micrograph, for display in the minimap.
    /// </summary>
    private RectangleF ViewportRectangle => new()
    {
        X = (float)(-translateX / ZoomLevel),
        Y = (float)(-translateY / ZoomLevel),
        Width = (float)(Width / ZoomLevel),
        Height = (float)(Height / ZoomLevel)
    };

    #endregion
    
    #region Cancellable Async Wrappers

    /// <summary>
    /// Loads micrograph with cancellation support and concurrency limiting
    /// </summary>
    private async Task LoadMicrographWithCancellation()
    {
        var acquired = await _imageLoadingSemaphore.WaitAsync(TimeSpan.FromSeconds(10), _componentCts.Token);
        if (!acquired)
        {
            Logger.LogWarning("Could not acquire image loading semaphore within 10 seconds for micrograph: {MicrographPath}", MicrographPath);
            return;
        }
        
        try
        {
            await Task.Run(() =>
            {
                _componentCts.Token.ThrowIfCancellationRequested();
                LoadMicrograph();

                _componentCts.Token.ThrowIfCancellationRequested();
                RenderMicrograph();

                // Calculate and set the aspect ratio when loading a new image
                if (micrographDims.X > 0 && micrographDims.Y > 0)
                {
                    aspectRatio = (float)micrographDims.X / micrographDims.Y;
                    imageLoaded = true;

                    // Use current dimensions to calculate target size
                    int availableWidth = Math.Max(MinWidth, Width);
                    int availableHeight = Math.Max(MinHeight, Height);

                    float containerAspect = (float)availableWidth / availableHeight;

                    if (containerAspect > aspectRatio)
                    {
                        // Container is wider than image - height is the limiting factor
                        Height = availableHeight;
                        Width = (int)(Height * aspectRatio);
                    }
                    else
                    {
                        // Container is taller than image - width is the limiting factor
                        Width = availableWidth;
                        Height = (int)(Width / aspectRatio);
                    }
                }

                FitToViewport();
                isFitToViewport = true;
            }, _componentCts.Token);
        }
        finally
        {
            _imageLoadingSemaphore.Release();
        }
    }

    /// <summary>
    /// Loads mask with cancellation support
    /// </summary>
    private async Task LoadMaskWithCancellation()
    {
        await Task.Run(() =>
        {
            _componentCts.Token.ThrowIfCancellationRequested();
            LoadMask();

            _componentCts.Token.ThrowIfCancellationRequested();
            RenderMask();
        }, _componentCts.Token);
    }

    /// <summary>
    /// Loads thumbnail with cancellation support
    /// </summary>
    private async Task LoadThumbnailWithCancellation()
    {
        await Task.Run(() =>
        {
            _componentCts.Token.ThrowIfCancellationRequested();
            LoadThumbnail();
        }, _componentCts.Token);
    }

    #endregion
}