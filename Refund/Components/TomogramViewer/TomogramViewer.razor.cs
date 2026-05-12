using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Refund.Components.MicrographViewer;
using Warp;
using Warp.Headers;
using Warp.Tools;
using static Refund.Components.MicrographViewer.MicrographViewer;

namespace Refund.Components.TomogramViewer;

/// <summary>
/// Represents a particle with 3D position, Euler angles, and score.
/// </summary>
public class Particle
{
    /// <summary>
    /// 3D position coordinates in voxels
    /// </summary>
    public float3 Position { get; set; } = new float3(0, 0, 0);

    /// <summary>
    /// Euler angles in degrees (rot, tilt, psi)
    /// </summary>
    public float3 Angles { get; set; } = new float3(0, 0, 0);

    /// <summary>
    /// Particle score or confidence metric
    /// </summary>
    public float Score { get; set; } = 0;

    public Particle() { }

    public Particle(float3 position, float3 angles = default, float score = 0)
    {
        Position = position;
        Angles = angles;
        Score = score;
    }
}

/// <summary>
/// A named group of particles with optional appearance defaults and 3D model volume.
/// </summary>
public class ParticleSpecies
{
    public string Name { get; set; }
    public List<Particle> Particles { get; set; } = new();
    public string ModelVolumePath { get; set; }
    public string Color { get; set; }
    public float? Diameter { get; set; }
}

/// <summary>
/// Defines the three orthogonal planes that can be visualized in the tomogram.
/// Used by TomogramSliceViewer to determine which slice orientation to render.
/// </summary>
public enum PlaneType { XY, XZ, ZY }

/// <summary>
/// A comprehensive viewer component for 3D tomographic data.
/// Provides visualization of three orthogonal slices (XY, XZ, ZY) with synchronized navigation,
/// particle overlay visualization, and interactive controls.
/// 
/// This component reuses the ParticleControls from the MicrographViewer namespace to 
/// provide consistent UI for particle visualization and manipulation across both 2D and 3D data.
/// 
/// The component supports automatic resizing when the parent container changes dimensions.
/// </summary>
public partial class TomogramViewer : IDisposable
{
    private string elementId = "tomogram-viewer-" + Guid.NewGuid().ToString("N");
    private ElementReference containerElement;
    private DotNetObjectReference<TomogramViewer> dotNetRef;
    private bool isDisposed = false;
    private IJSObjectReference module;
    
    /// <summary>
    /// Cancellation token source for managing component lifecycle
    /// </summary>
    private CancellationTokenSource _componentCts = new();
    
    /// <summary>
    /// Semaphore to limit concurrent volume loading operations
    /// </summary>
    private readonly SemaphoreSlim _volumeLoadingSemaphore = new(1, 1);
    
    /// <summary>
    /// Tracks the last parameter change to debounce rapid updates
    /// </summary>
    private CancellationTokenSource _parameterChangeCts = new();
    
    // Reference to slice viewers
    private TomogramSliceViewer xyViewer;
    private TomogramSliceViewer xzViewer;
    private TomogramSliceViewer zyViewer;

    [Inject]
    private IJSRuntime JsRuntime { get; set; }
    
    [Inject]
    private ILogger<TomogramViewer> Logger { get; set; } = default!;

    [Parameter]
    public string TomogramPath { get; set; }
    private string _tomogramPath;

    [Parameter]
    public List<Particle> Particles { get; set; }
    private List<Particle> _particles;

    [Parameter]
    public int MinWidth { get; set; } = 800;

    [Parameter]
    public int MinHeight { get; set; } = 600;

    [Parameter]
    public bool CanShowParticlesControls { get; set; } = true;

    // ViewerWidth and ViewerHeight are now internal properties
    // They represent the current size of the component
    protected int ViewerWidth { get; private set; } = 800;
    protected int ViewerHeight { get; private set; } = 600;

    // 3D volume info
    protected int3 volDims;
    protected float PixelSize = 1f;
    protected float[][] volumeData = null; // All volume XY slices

    protected bool IsLoadingCentralSlice = true;
    protected bool IsLoadingVolume = true;

    // Slices
    protected float[] XYSlice = null;
    private int _sliceZ = -1;
    protected float[] XZSlice = null;
    private int _sliceY = -1;
    protected float[] ZYSlice = null;
    private int _sliceX = -1;

    // Coordinates
    protected int3 ViewPoint;

    // Zoom/Pan
    protected double ZoomLevel = 1.0;
    protected double TranslateX = 0;
    protected double TranslateYXY = 0; // For XY plane
    protected double TranslateYXZ = 0; // We'll unify these if needed, but may keep separate if aspect differs
    protected double TranslateYZY = 0;
    protected double TranslateXZY = 0; // For ZY plane - we might need separate translates per plane if dimension ratios differ
    // However, we want synchronized panning. We'll treat the world coordinate system carefully.
    // For simplicity, let's maintain a single world transform referencing XY plane and derive others.

    protected bool showControls = false;
    private bool isFitToViewport = true;

    // Particle parameters
    protected decimal ParticleDiameter = 100;
    protected decimal ParticleBoxSize = 200;
    protected string ParticleColor = "#FFF700";
    protected double ParticleStrokeWidth = 2;
    protected ParticleShapes ParticleShape = ParticleShapes.Circle;
    protected bool IsPickingParticles = false;

    protected Dictionary<(float3 center, float radius), float> SphereParticleRadiusMap = new();
    protected float CubeParticleHalfSize => (float)ParticleBoxSize / (2 * PixelSize);

    // Intensity scaling
    protected float meanCentral;
    protected float stdCentral;
    protected float rangeStd = 3;
    protected float minIntensity;
    protected float maxIntensity;

    // Reusable buffers
    private float[] centralSliceBuffer = null;

    protected bool ShowScaleBar = true;

    private float toolbarHeight = 60;
    private float gutter = 10;
    private float viewportsWidth => ViewerWidth;
    private float viewportsHeight => ViewerHeight - toolbarHeight - gutter;

    // Dimensions for panels
    protected float slicePanelScale = 1;
    protected int2 SlicePanelDimsXY => new((int)Math.Round(volDims.X * slicePanelScale), (int)Math.Round(volDims.Y * slicePanelScale));
    protected int2 SlicePanelDimsXZ => new((int)Math.Round(volDims.X * slicePanelScale), (int)Math.Round(volDims.Z * slicePanelScale));
    protected int2 SlicePanelDimsZY => new((int)Math.Round(volDims.Z * slicePanelScale), (int)Math.Round(volDims.Y * slicePanelScale));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Create a reference to this component instance for JS interop
            dotNetRef = DotNetObjectReference.Create(this);
            
            // Start observing resize events on the container element
            module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/TomogramViewer/TomogramViewer.razor.js");
                
            await module.InvokeVoidAsync("observeResize", containerElement, dotNetRef, 100); // 100ms debounce
            
            // Set initial dimensions
            ViewerWidth = MinWidth;
            ViewerHeight = MinHeight;
        }
        
        // Initialize the particle preview system if we have the necessary data
        if (module != null && !IsLoadingVolume && volumeData != null)
        {
            await InitializeParticlePreviewSystem();
        }
    }
    
    private async Task InitializeParticlePreviewSystem()
    {
        if (module == null) return;
        
        // Create plane configurations
        var planeConfigs = new Dictionary<string, object>
        {
            [$"{elementId}-xy"] = new {
                type = "XY",
                width = SlicePanelDimsXY.X,
                height = SlicePanelDimsXY.Y,
                translateX = TranslateX,
                translateY = TranslateYXY,
                zoom = ZoomLevel,
                dimensions = new { x = volDims.X, y = volDims.Y, z = volDims.Z }
            },
            [$"{elementId}-xz"] = new {
                type = "XZ",
                width = SlicePanelDimsXZ.X,
                height = SlicePanelDimsXZ.Y,
                translateX = TranslateX,
                translateY = TranslateYXZ,
                zoom = ZoomLevel,
                dimensions = new { x = volDims.X, y = volDims.Y, z = volDims.Z }
            },
            [$"{elementId}-zy"] = new {
                type = "ZY",
                width = SlicePanelDimsZY.X,
                height = SlicePanelDimsZY.Y,
                translateX = TranslateXZY,
                translateY = TranslateYZY,
                zoom = ZoomLevel,
                dimensions = new { x = volDims.X, y = volDims.Y, z = volDims.Z }
            }
        };
        
        // Create view point object
        var viewPoint = new { x = ViewPoint.X, y = ViewPoint.Y, z = ViewPoint.Z };
        
        // Initialize the preview system
        await module.InvokeVoidAsync("initializeParticlePreview", 
            IsPickingParticles,
            planeConfigs,
            viewPoint,
            ParticleColor,
            ParticleDiameter,
            PixelSize);
    }

    private async Task UpdateParticlePreviewSystem()
    {
        if (module == null) return;
        
        var viewPoint = new { x = ViewPoint.X, y = ViewPoint.Y, z = ViewPoint.Z };
        
        await module.InvokeVoidAsync("updateParticlePreview",
            IsPickingParticles,
            viewPoint,
            ParticleColor,
            ParticleDiameter,
            PixelSize);
    }

    /// <summary>
    /// Update all plane transforms in the JS preview system
    /// </summary>
    private async Task UpdateAllPlaneTransforms()
    {
        if (module == null) return;
        
        // Update XY plane
        await module.InvokeVoidAsync("updatePlaneTransform",
            $"{elementId}-xy",
            TranslateX,
            TranslateYXY,
            ZoomLevel);
            
        // Update XZ plane
        await module.InvokeVoidAsync("updatePlaneTransform",
            $"{elementId}-xz",
            TranslateX,
            TranslateYXZ,
            ZoomLevel);
            
        // Update ZY plane
        await module.InvokeVoidAsync("updatePlaneTransform",
            $"{elementId}-zy",
            TranslateXZY,
            TranslateYZY,
            ZoomLevel);
    }
    
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
                // Clean up resize observer
                module.InvokeVoidAsync("disposeResizeObserver", containerElement);
                
                // Clean up particle preview event handlers and references
                module.InvokeVoidAsync("disposeParticlePreview");
                
                // Dispose module
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
        
        // Dispose semaphores
        _volumeLoadingSemaphore?.Dispose();
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
            
        // Update dimensions if they've changed
        if (newContainerWidth != ViewerWidth || newContainerHeight != ViewerHeight)
        {
            ViewerWidth = newContainerWidth;
            ViewerHeight = newContainerHeight;
            
            // Recalculate panel scale based on new dimensions
            if (volDims.X > 0 && volDims.Y > 0 && volDims.Z > 0)
            {
                slicePanelScale = Math.Min((float)(ViewerWidth - gutter) / (volDims.X + volDims.Z),
                                           (float)(ViewerHeight - 2 * gutter - toolbarHeight) / (volDims.Y + volDims.Z));
                
                // If we're in fit-to-viewport mode, update the fit
                if (isFitToViewport)
                {
                    FitToViewport();
                }
            }
            
            await InvokeAsync(StateHasChanged);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // Cancel any pending parameter change operations
        _parameterChangeCts.Cancel();
        _parameterChangeCts.Dispose();
        _parameterChangeCts = new CancellationTokenSource();
        
        // Debounce rapid parameter changes
        try
        {
            await Task.Delay(50, _parameterChangeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Another parameter change occurred
        }
        
        if (TomogramPath != _tomogramPath)
        {
            _tomogramPath = TomogramPath;
            if (File.Exists(TomogramPath))
            {
                IsLoadingCentralSlice = true;
                IsLoadingVolume = true;
                
                // Reset slice tracking to force recreation when new volume is loaded
                _sliceX = -1;
                _sliceY = -1;
                _sliceZ = -1;
                
                // Clear existing slice data to avoid showing stale data
                XYSlice = null;
                XZSlice = null;
                ZYSlice = null;
                centralSliceBuffer = null; // Clear central slice buffer to avoid showing previous tomogram data
                
                StateHasChanged();

                // Load header quickly
                var header = MapHeader.ReadFromFile(TomogramPath);
                volDims = new int3(header.Dimensions);
                PixelSize = header.PixelSize.X;

                // Figure out panel dimensions
                slicePanelScale = Math.Min((float)(ViewerWidth - gutter) / (volDims.X + volDims.Z),
                                           (float)(ViewerHeight - 2 * gutter - toolbarHeight) / (volDims.Y + volDims.Z));

                await FitToViewport();
                isFitToViewport = true;

                ViewPoint.X = volDims.X / 2;
                ViewPoint.Y = volDims.Y / 2;
                ViewPoint.Z = volDims.Z / 2;

                // Load central XY slice quickly with cancellation support
                await Task.Run(() => LoadCentralXYSlice(), _componentCts.Token);
                IsLoadingCentralSlice = false;
                StateHasChanged();

                // Start async full volume loading with cancellation support
                _ = LoadFullVolumeWithCancellation();
            }
        }

        if (Particles != _particles)
        {
            _particles = Particles;
            // Update the local state to reflect the new particles
            StateHasChanged();
        }
    }

    private void LoadCentralXYSlice()
    {
        int zc = volDims.Z / 2;

        // We'll read only that slice
        // We'll allocate a buffer if needed
        int sliceElements = volDims.X * volDims.Y;
        if (centralSliceBuffer == null || centralSliceBuffer.Length != sliceElements)
            centralSliceBuffer = new float[sliceElements];

        IOHelper.ReadMapFloat(TomogramPath, [zc], null, [centralSliceBuffer]);

        XYSlice = (float[])centralSliceBuffer.Clone();
    }

    private void ComputeIntensityFromCentralPortion()
    {
        float2 meanStd = MathHelper.MedianAndStd(centralSliceBuffer);

        meanCentral = meanStd.X;
        stdCentral = meanStd.Y;
    }

    private void UpdateIntensityScaling()
    {
        minIntensity = meanCentral - rangeStd * stdCentral;
        maxIntensity = meanCentral + rangeStd * stdCentral;
    }

    private void LoadFullVolume()
    {
        if (volumeData == null || volumeData.Length != volDims.Z || volumeData[0].Length != volDims.ElementsSlice())
            volumeData = Warp.Tools.Helper.ArrayOfFunction(z => new float[volDims.ElementsSlice()], volDims.Z);

        _sliceX = -1;
        _sliceY = -1;
        _sliceZ = -1;

        IOHelper.ReadMapFloat(TomogramPath, null, null, volumeData);
        IsLoadingVolume = false;
    }

    private async Task UpdateAllSlices()
    {
        if (IsLoadingVolume) return;

        // XY slice at Z
        ExtractXYSlice(ViewPoint.Z, ref XYSlice);

        // XZ slice at Y
        ExtractXZSlice(ViewPoint.Y, ref XZSlice);

        // ZY slice at X
        ExtractZYSlice(ViewPoint.X, ref ZYSlice);
        
        // Update the preview system with new view point
        await UpdateParticlePreviewSystem();
    }

    private void ExtractXYSlice(int z, ref float[] target)
    {
        if (_sliceZ == z && target != null)
            return;

        if (target == null || target.Length != volDims.X * volDims.Y)
            target = new float[volDims.X * volDims.Y];

        Array.Copy(volumeData[z], 0, target, 0, volDims.X * volDims.Y);

        _sliceZ = z;
    }

    private void ExtractXZSlice(int y, ref float[] target)
    {
        if (_sliceY == y && target != null)
            return;

        if (target == null || target.Length != volDims.X * volDims.Z)
            target = new float[volDims.X * volDims.Z];

        for (int z = 0; z < volDims.Z; z++)
        {
            int volBase = y * volDims.X;
            int sliceBase = z * volDims.X;
            Array.Copy(volumeData[z], volBase, target, sliceBase, volDims.X);
        }

        _sliceY = y;
    }

    private void ExtractZYSlice(int x, ref float[] target)
    {
        if (_sliceX == x && target != null)
            return;

        if (target == null || target.Length != volDims.Z * volDims.Y)
            target = new float[volDims.Z * volDims.Y];

        for (int z = 0; z < volDims.Z; z++)
            for (int y = 0; y < volDims.Y; y++)
                target[y * volDims.Z + z] = volumeData[z][y * volDims.X + x];

        _sliceX = x;
    }


    // Mouse wheel coordinate change:
    protected async Task OnMouseWheelCoordinateChange(PlaneType plane, double delta)
    {
        if (IsLoadingVolume) return; // locked out

        int increment = Math.Sign(delta);

        // If over XY plane, change Z
        // If over XZ plane, change Y
        // If over ZY plane, change X
        if (plane == PlaneType.XY)
            ViewPoint.Z = Math.Clamp(ViewPoint.Z + increment, 0, volDims.Z - 1);
        else if (plane == PlaneType.XZ)
            ViewPoint.Y = Math.Clamp(ViewPoint.Y + increment, 0, volDims.Y - 1);
        else if (plane == PlaneType.ZY)
            ViewPoint.X = Math.Clamp(ViewPoint.X + increment, 0, volDims.X - 1);

        await UpdateAllSlices();
        StateHasChanged();
    }

    protected async Task OnSliceClickXY(float xx, float yy)
    {
        if (IsLoadingVolume) return;

        ViewPoint.X = Math.Clamp((int)Math.Round(xx), 0, volDims.X - 1);
        ViewPoint.Y = Math.Clamp((int)Math.Round(yy), 0, volDims.Y - 1);

        Logger.LogDebug("Clicked XY plane at coordinates X={X}, Y={Y}", ViewPoint.X, ViewPoint.Y);

        await UpdateAllSlices();
        StateHasChanged();
    }

    protected async Task OnSliceClickXZ(float xx, float zz)
    {
        if (IsLoadingVolume) return;

        ViewPoint.X = Math.Clamp((int)Math.Round(xx), 0, volDims.X - 1);
        ViewPoint.Z = Math.Clamp((int)Math.Round(zz), 0, volDims.Z - 1);

        Logger.LogDebug("Clicked XZ plane at coordinates X={X}, Z={Z}", ViewPoint.X, ViewPoint.Z);

        await UpdateAllSlices();
        StateHasChanged();
    }

    protected async Task OnSliceClickZY(float zz, float yy)
    {
        if (IsLoadingVolume) return;

        ViewPoint.Z = Math.Clamp((int)Math.Round(zz), 0, volDims.Z - 1);
        ViewPoint.Y = Math.Clamp((int)Math.Round(yy), 0, volDims.Y - 1);

        Logger.LogDebug("Clicked ZY plane at coordinates Z={Z}, Y={Y}", ViewPoint.Z, ViewPoint.Y);

        await UpdateAllSlices();
        StateHasChanged();
    }

    // Zoom and pan from toolbar
    protected async Task OnZoomChanged(double newZoom)
    {
        // If zoom changes, we're no longer in "fit to viewport" mode
        isFitToViewport = false;
        
        ZoomLevel = newZoom;
        
        // Update plane transforms in JS
        await UpdateAllPlaneTransforms();
        
        StateHasChanged();
    }

    protected async Task FitToViewport()
    {
        // Fit XY slice into the XY panel area for simplicity:
        double scaleX = (double)SlicePanelDimsXY.X / volDims.X;
        double scaleY = (double)SlicePanelDimsXY.Y / volDims.Y;
        ZoomLevel = Math.Min(scaleX, scaleY);

        TranslateX = (SlicePanelDimsXY.X - volDims.X * ZoomLevel) / 2;
        TranslateYXY = (SlicePanelDimsXY.Y - volDims.Y * ZoomLevel) / 2;
        // For simplicity, align other panels similarly. They represent different planes but we keep consistent world scaling.
        TranslateYXZ = TranslateYXY;
        TranslateYZY = TranslateYXY;
        TranslateXZY = TranslateX; // We'll keep them aligned at center.
        
        // Update plane transforms in JS
        await UpdateAllPlaneTransforms();
    }

    protected async Task OnFitToViewport()
    {
        await FitToViewport();
        isFitToViewport = true;
        StateHasChanged();
    }

    protected void OnScaleBarToggled(bool val)
    {
        ShowScaleBar = val;
        StateHasChanged();
    }

    private async Task OnParticleSettingsChanged(ParticleControls.ParticleSettings settings)
    {
        IsPickingParticles = settings.IsPicking;
        ParticleDiameter = settings.Diameter;
        ParticleBoxSize = settings.BoxSize;
        ParticleShape = settings.Shape;
        ParticleColor = settings.Color;

        await UpdateParticlePreviewSystem();
        StateHasChanged();
    }

    private async Task OnViewPointXChanged(int value)
    {
        ViewPoint.X = Math.Clamp(value, 0, volDims.X - 1);
        await UpdateAllSlices();
    }

    private async Task OnViewPointYChanged(int value)
    {
        ViewPoint.Y = Math.Clamp(value, 0, volDims.Y - 1);
        await UpdateAllSlices();
    }

    private async Task OnViewPointZChanged(int value)
    {
        ViewPoint.Z = Math.Clamp(value, 0, volDims.Z - 1);
        await UpdateAllSlices();
    }
    
    #region Cancellable Async Wrappers

    /// <summary>
    /// Loads full volume with cancellation support and concurrency limiting
    /// </summary>
    private async Task LoadFullVolumeWithCancellation()
    {
        var acquired = await _volumeLoadingSemaphore.WaitAsync(TimeSpan.FromSeconds(30), _componentCts.Token);
        if (!acquired)
        {
            Logger.LogWarning("Could not acquire volume loading semaphore within 30 seconds for tomogram: {TomogramPath}", TomogramPath);
            return;
        }
        
        try
        {
            await Task.Run(async () =>
            {
                _componentCts.Token.ThrowIfCancellationRequested();
                LoadFullVolume();

                _componentCts.Token.ThrowIfCancellationRequested();
                ComputeIntensityFromCentralPortion();

                _componentCts.Token.ThrowIfCancellationRequested();
                UpdateIntensityScaling();

                _componentCts.Token.ThrowIfCancellationRequested();

                // Set loading to false BEFORE calling UpdateAllSlices so it doesn't exit early
                IsLoadingVolume = false;
                await UpdateAllSlices();

                await InvokeAsync(StateHasChanged);

                // Initialize the particle preview system after data is loaded
                _componentCts.Token.ThrowIfCancellationRequested();
                await InitializeParticlePreviewSystem();
            }, _componentCts.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Volume loading cancelled for component disposal or parameter change");
        }
        finally
        {
            _volumeLoadingSemaphore.Release();
        }
    }

    #endregion
}
