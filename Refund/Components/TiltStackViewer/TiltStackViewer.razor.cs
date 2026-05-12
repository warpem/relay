using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Refund.Services;
using SkiaSharp;

namespace Refund.Components.TiltStackViewer;

/// <summary>
/// Tilt stack viewer component for displaying tilt series images with transforms applied.
/// Supports viewing individual tilts and overlaying multiple tilt images with transforms.
/// </summary>
public partial class TiltStackViewer : ComponentBase, IDisposable
{
    private bool isLoading = true;
    private string elementId = "tilt-stack-viewer-" + Guid.NewGuid().ToString("N");
    private ElementReference containerElement;
    private ElementReference viewportRef;
    private DotNetObjectReference<TiltStackViewer> dotNetRef;
    private bool imageLoaded = false;
    
    /// <summary>
    /// Cancellation token source for managing component lifecycle
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
    
    /// <summary>
    /// Tracks the last overlay count change to debounce rapid number field updates
    /// </summary>
    private CancellationTokenSource _overlayCountChangeCts = new();
    
    /// <summary>
    /// Tracks the last tilt index change to debounce rapid slider updates
    /// </summary>
    private CancellationTokenSource _tiltIndexChangeCts = new();
    
    [Inject]
    private IJSRuntime JsRuntime { get; set; }
    
    [Inject]
    private FileService FileService { get; set; }
    
    [Inject]
    private ILogger<TiltStackViewer> Logger { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Create a reference to this component instance for JS interop
            dotNetRef = DotNetObjectReference.Create(this);
            
            // Start observing resize events on the container element
            module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/TiltStackViewer/TiltStackViewer.razor.js");
                
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
        
        // Cancel and dispose component-level cancellation tokens
        _componentCts?.Cancel();
        _componentCts?.Dispose();
        
        _parameterChangeCts?.Cancel();
        _parameterChangeCts?.Dispose();
        
        _overlayCountChangeCts?.Cancel();
        _overlayCountChangeCts?.Dispose();
        
        _tiltIndexChangeCts?.Cancel();
        _tiltIndexChangeCts?.Dispose();
        
        // Dispose semaphores
        _imageLoadingSemaphore?.Dispose();
    }
    
    // Height of the control panel in pixels
    private const int ControlPanelHeight = 60;

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
        if (imageLoaded)
        {
            // Use the rotated bounding box dimensions to calculate aspect ratio
            var (boundingBoxWidth, boundingBoxHeight, _, _) = CalculateRotatedBoundingBox();
            
            // Make sure we have valid dimensions
            if (boundingBoxWidth <= 0 || boundingBoxHeight <= 0)
            {
                boundingBoxWidth = ImageDimensions.X;
                boundingBoxHeight = ImageDimensions.Y;
            }
            
            // Calculate the aspect ratio of the rotated bounding box
            float rotatedAspectRatio = boundingBoxWidth / boundingBoxHeight;
            
            // Account for the control panel height
            int availableHeight = newContainerHeight - ControlPanelHeight;
            if (availableHeight <= 0)
                availableHeight = newContainerHeight / 2; // Fallback if no space left
                
            // Calculate the available space
            float containerAspect = (float)newContainerWidth / availableHeight;
            
            int newMaxWidth, newMaxHeight;
            
            if (containerAspect > rotatedAspectRatio)
            {
                // Container is wider than image - height is the limiting factor
                newMaxHeight = availableHeight;
                newMaxWidth = (int)(newMaxHeight * rotatedAspectRatio);
            }
            else
            {
                // Container is taller than image - width is the limiting factor
                newMaxWidth = newContainerWidth;
                newMaxHeight = (int)(newMaxWidth / rotatedAspectRatio);
                
                // Make sure it fits in the available height
                if (newMaxHeight > availableHeight)
                {
                    newMaxHeight = availableHeight;
                    newMaxWidth = (int)(newMaxHeight * rotatedAspectRatio);
                }
            }
            
            // Total component height includes the control panel height
            newMaxHeight += ControlPanelHeight;
            
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
            // No image loaded - just use available space
            if (newContainerWidth != Width || newContainerHeight != Height)
            {
                Width = newContainerWidth;
                Height = newContainerHeight;
                dimensionsChanged = true;
            }
        }
        
        // Only update if dimensions actually changed
        if (dimensionsChanged)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    #region Parameters

    /// <summary>
    /// Paths to the tilt images to be displayed.
    /// </summary>
    [Parameter]
    public string[] ImagePaths { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// Tilt angles for each image in degrees.
    /// </summary>
    [Parameter]
    public float[] TiltAngles { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Index of the first image when sorted by accumulated exposure.
    /// </summary>
    [Parameter]
    public int? ZeroTiltIndex { get; set; } = null;
    
    /// <summary>
    /// Rotation angles for each image in degrees.
    /// </summary>
    [Parameter]
    public float[] AxisAngles { get; set; } = Array.Empty<float>();
    
    /// <summary>
    /// X-axis shifts for each image in pixels.
    /// </summary>
    [Parameter]
    public float[] AxisShiftsX { get; set; } = Array.Empty<float>();
    
    /// <summary>
    /// Y-axis shifts for each image in pixels.
    /// </summary>
    [Parameter]
    public float[] AxisShiftsY { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Minimum width of the viewer component in pixels.
    /// </summary>
    [Parameter]
    public int MinWidth { get; set; } = 800;

    /// <summary>
    /// Minimum height of the viewer component in pixels.
    /// </summary>
    [Parameter]
    public int MinHeight { get; set; } = 600;
    
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

    #region State

    private int currentImageIndex = 0;
    
    /// <summary>
    /// Current tilt image index to display.
    /// </summary>
    public int CurrentImageIndex
    {
        get => currentImageIndex;
        set
        {
            if (value != currentImageIndex && value >= 0 && value < ImagePaths.Length)
            {
                currentImageIndex = value;
                StateHasChanged();
            }
        }
    }
    
    private int overlayCount = 1;
    
    /// <summary>
    /// Number of images to overlay simultaneously.
    /// </summary>
    public int OverlayCount
    {
        get => overlayCount;
        set
        {
            if (value != overlayCount && value >= 1 && value <= ImagePaths.Length)
            {
                overlayCount = value;
                StateHasChanged();
            }
        }
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Handle tilt index changes from the slider with debouncing.
    /// </summary>
    private async Task HandleTiltIndexChanged(int newValue)
    {
        // Update immediately for UI responsiveness, then debounce state change
        if (newValue >= 0 && newValue < ImagePaths?.Length)
        {
            await UpdateTiltIndexWithDebouncing(newValue);
        }
    }
    
    /// <summary>
    /// Handle overlay count changes from the number field with debouncing.
    /// </summary>
    private async Task HandleOverlayCountChanged(int newValue)
    {
        // Update immediately for UI responsiveness, then debounce state change
        if (newValue >= 1 && newValue <= ImagePaths?.Length)
        {
            overlayCount = newValue;
            await UpdateOverlayCountWithDebouncing();
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Check if an image index is within the overlay range.
    /// </summary>
    private bool IsInOverlayRange(int index)
    {
        if (OverlayCount < 1)
            return false;
        
        int halfCount = OverlayCount / 2;
        int lowerBound = Math.Max(0, CurrentImageIndex - halfCount);
        int upperBound = Math.Min(ImagePaths.Length - 1, CurrentImageIndex + halfCount);

        if (upperBound - lowerBound + 1 > OverlayCount)
            upperBound--;
        
        return index >= lowerBound && index <= upperBound;
    }
    
    /// <summary>
    /// Get the opacity for an image based on its position in the overlay.
    /// </summary>
    private float GetImageOpacity(int index)
    {
        if (OverlayCount <= 1)
            return 1;
        
        var overlayIndices = Enumerable.Range(0, ImagePaths.Length).Where(IsInOverlayRange).ToList();
        if (overlayIndices.Any())
        {
            if (index == overlayIndices.Min())
                return 1;
            else
                return 1f / overlayIndices.Count;
        }
        
        return 1f / OverlayCount;
    }
    
    /// <summary>
    /// Get the X translation for an image based on its transform.
    /// </summary>
    private float GetTranslateX(int index)
    {
        if (AxisShiftsX == null || index >= AxisShiftsX.Length)
            return 0;

        return -AxisShiftsX[index];
    }
    
    /// <summary>
    /// Get the Y translation for an image based on its transform.
    /// </summary>
    private float GetTranslateY(int index)
    {
        if (AxisShiftsY == null || index >= AxisShiftsY.Length)
            return 0;

        return AxisShiftsY[index];
    }
    
    /// <summary>
    /// Get the rotation angle for an image based on its transform.
    /// </summary>
    private float GetRotation(int index)
    {
        if (AxisAngles == null || index >= AxisAngles.Length)
            return 0;

        return AxisAngles[index];
    }
    
    /// <summary>
    /// Calculates the bounding box for all rotated and translated images.
    /// </summary>
    private (float width, float height, float offsetX, float offsetY) CalculateRotatedBoundingBox()
    {
        if (ImageDimensions.X <= 0 || ImageDimensions.Y <= 0 || ImagePaths == null || ImagePaths.Length == 0)
            return (ImageDimensions.X, ImageDimensions.Y, 0, 0);
        
        float originalWidth = ImageDimensions.X;
        float originalHeight = ImageDimensions.Y;
        float centerX = originalWidth / 2;
        float centerY = originalHeight / 2;
        
        // Initialize min/max coordinates to extreme values to ensure they get properly set
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        
        // For each image, calculate its bounding box and update the overall bounds
        for (int i = 0; i < ImagePaths.Length; i++)
        {
            float angleRadians = (float)(GetRotation(i) * Math.PI / 180.0);
            float translateX = GetTranslateX(i);
            float translateY = GetTranslateY(i);
            
            // First translate, then rotate - apply the transforms in the right order
            // Calculate the transformed coordinates of the four corners
            var corners = new[]
            {
                // Translate then rotate each corner point
                RotatePoint(0 + translateX, 0 + translateY, centerX + translateX, centerY + translateY, angleRadians),
                RotatePoint(originalWidth + translateX, 0 + translateY, centerX + translateX, centerY + translateY, angleRadians),
                RotatePoint(0 + translateX, originalHeight + translateY, centerX + translateX, centerY + translateY, angleRadians),
                RotatePoint(originalWidth + translateX, originalHeight + translateY, centerX + translateX, centerY + translateY, angleRadians)
            };
            
            // Find the min and max coordinates
            float imgMinX = corners.Min(p => p.x);
            float imgMinY = corners.Min(p => p.y);
            float imgMaxX = corners.Max(p => p.x);
            float imgMaxY = corners.Max(p => p.y);
            
            // Update the overall bounding box
            minX = Math.Min(minX, imgMinX);
            minY = Math.Min(minY, imgMinY);
            maxX = Math.Max(maxX, imgMaxX);
            maxY = Math.Max(maxY, imgMaxY);
        }
        
        // Calculate the size and offset of the bounding box
        float width = maxX - minX;
        float height = maxY - minY;
        float offsetX = -minX;
        float offsetY = -minY;
        
        return (width, height, offsetX, offsetY);
    }
    
    /// <summary>
    /// Rotates a point around a center point.
    /// </summary>
    private (float x, float y) RotatePoint(float x, float y, float centerX, float centerY, float angleRadians)
    {
        // Translate point to origin
        float translatedX = x - centerX;
        float translatedY = y - centerY;
        
        // Rotate
        float rotatedX = translatedX * (float)Math.Cos(angleRadians) - translatedY * (float)Math.Sin(angleRadians);
        float rotatedY = translatedX * (float)Math.Sin(angleRadians) + translatedY * (float)Math.Cos(angleRadians);
        
        // Translate back
        float resultX = rotatedX + centerX;
        float resultY = rotatedY + centerY;
        
        return (resultX, resultY);
    }
    
    /// <summary>
    /// Applies a translation to a point.
    /// </summary>
    private (float x, float y) TranslatePoint((float x, float y) point, float translateX, float translateY)
    {
        return (point.x + translateX, point.y + translateY);
    }
    
    /// <summary>
    /// Gets the current viewBox dimensions and offsets for the SVG.
    /// </summary>
    public (float width, float height, float offsetX, float offsetY) ViewBoxDimensions => CalculateRotatedBoundingBox();

    #endregion

    #region Lifecycle Methods

    /// <summary>
    /// Image dimensions for aspect ratio calculations
    /// </summary>
    private (int X, int Y) ImageDimensions { get; set; } = (0, 0);
    
    protected override async Task OnParametersSetAsync()
    {
        // Cancel any pending parameter change operations
        _parameterChangeCts.Cancel();
        _parameterChangeCts.Dispose();
        _parameterChangeCts = new CancellationTokenSource();
        
        // Debounce rapid parameter changes
        try
        {
            await Task.Delay(200, _parameterChangeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Another parameter change occurred
        }
        
        bool stateChanged = false;
        
        // Check if we have images to display
        if (ImagePaths != null && ImagePaths.Length > 0)
        {
            // Reset indices if needed
            if (CurrentImageIndex >= ImagePaths.Length)
            {
                currentImageIndex = 0;
                stateChanged = true;
            }
            
            // Limit overlay count to image count
            if (OverlayCount > ImagePaths.Length)
            {
                overlayCount = ImagePaths.Length;
                stateChanged = true;
            }
            
            // Load image dimensions asynchronously with cancellation support
            if (!imageLoaded && ImageDimensions.X <= 0 || ImageDimensions.Y <= 0)
            {
                await LoadImageDimensionsWithCancellation();
                stateChanged = true;
            }
            
            // Finish loading
            if (isLoading)
            {
                isLoading = false;
                stateChanged = true;
            }
        }
        
        if (stateChanged)
        {
            StateHasChanged();
        }
    }

    #endregion
    
    #region Cancellable Async Wrappers

    /// <summary>
    /// Updates overlay count with debouncing to prevent rapid-fire state changes
    /// </summary>
    private async Task UpdateOverlayCountWithDebouncing()
    {
        // Cancel any pending overlay count change operations
        _overlayCountChangeCts.Cancel();
        _overlayCountChangeCts.Dispose();
        _overlayCountChangeCts = new CancellationTokenSource();
        
        try
        {
            // Debounce rapid overlay count changes (200ms delay)
            await Task.Delay(200, _overlayCountChangeCts.Token);
            
            // Trigger state change
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
            // Another overlay count change occurred, ignore this one
        }
    }

    /// <summary>
    /// Updates tilt index with debouncing to prevent rapid-fire state changes
    /// </summary>
    private async Task UpdateTiltIndexWithDebouncing(int newValue)
    {
        // Cancel any pending tilt index change operations
        _tiltIndexChangeCts.Cancel();
        _tiltIndexChangeCts.Dispose();
        _tiltIndexChangeCts = new CancellationTokenSource();
        
        try
        {
            // Debounce rapid tilt index changes (50ms delay)
            await Task.Delay(50, _tiltIndexChangeCts.Token);
            
            currentImageIndex = newValue;
            
            // Trigger state change
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // Another tilt index change occurred, ignore this one
        }
    }

    /// <summary>
    /// Loads image dimensions with cancellation support and concurrency limiting
    /// </summary>
    private async Task LoadImageDimensionsWithCancellation()
    {
        if (ImagePaths == null || ImagePaths.Length == 0)
            return;
            
        var acquired = await _imageLoadingSemaphore.WaitAsync(TimeSpan.FromSeconds(10), _componentCts.Token);
        if (!acquired)
        {
            Logger.LogWarning("Could not acquire image loading semaphore within 10 seconds for image: {ImagePath}", ImagePaths[0]);
            return;
        }
        
        try
        {
            await Task.Run(() =>
            {
                string imagePath = ImagePaths[0];
                _componentCts.Token.ThrowIfCancellationRequested();

                if (File.Exists(imagePath))
                {
                    _componentCts.Token.ThrowIfCancellationRequested();
                    using var stream = File.OpenRead(imagePath);
                    using var bitmap = SKBitmap.Decode(stream);

                    _componentCts.Token.ThrowIfCancellationRequested();
                    if (bitmap != null)
                    {
                        ImageDimensions = (bitmap.Width, bitmap.Height);
                        imageLoaded = true;
                    }
                    else
                    {
                        // Fall back to default dimensions if decode fails
                        ImageDimensions = (800, 800);
                        imageLoaded = true;
                    }
                }
                else
                {
                    // Fall back to default dimensions if file doesn't exist
                    ImageDimensions = (800, 800);
                    imageLoaded = true;
                }
            }, _componentCts.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Image dimension loading cancelled for component disposal or parameter change");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error reading image dimensions from {ImagePath}", ImagePaths[0]);
            // Fall back to default dimensions
            ImageDimensions = (800, 800);
            imageLoaded = true;
        }
        finally
        {
            _imageLoadingSemaphore.Release();
        }
    }

    #endregion
}