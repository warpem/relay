using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Refund.Services;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace Refund.Components.SingleAxisScatter;

public partial class SingleAxisScatter : IAsyncDisposable
{
    [Inject]
    private ILogger<SingleAxisScatter> Logger { get; set; } = default!;
    #region Parameters

    /// <summary>
    /// The raw data points (JS will compute the axis range, etc.).
    /// Kept for backward compatibility.
    /// </summary>
    [Parameter] public List<ScatterPoint> Points { get; set; } = new();
    // Previous Points collection for comparison
    private List<ScatterPoint> _previousPoints;

    /// <summary>
    /// Multiple collections of data points to display.
    /// Points at the same index across collections will share X coordinates.
    /// </summary>
    [Parameter] public List<List<ScatterPoint>> PointsCollections { get; set; } = new();
    // Previous PointsCollections for comparison
    private List<List<ScatterPoint>> _previousPointsCollections;

    /// <summary>
    /// The lower and upper bounds of the highlight band (in data coords).
    /// </summary>
    [Parameter] public double RangeHighlightMin { get; set; } = 0.0;
    [Parameter] public double RangeHighlightMax { get; set; } = 0.0;
    
    /// <summary>
    /// Optional minimum value for Y-axis. If null, calculated from data.
    /// </summary>
    [Parameter] public double? YAxisMin { get; set; } = null;
    
    /// <summary>
    /// Optional maximum value for Y-axis. If null, calculated from data.
    /// </summary>
    [Parameter] public double? YAxisMax { get; set; } = null;

    /// <summary>
    /// Radius in pixels for each point.
    /// </summary>
    [Parameter] public double PointRadius { get; set; } = 2.5;

    /// <summary>
    /// Horizontal zoom factor (1.0 = no zoom).
    /// </summary>
    [Parameter] public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// Custom tooltip template for hovered points.
    /// </summary>
    [Parameter] public RenderFragment<ScatterPoint?> TooltipTemplate { get; set; }

    /// <summary>
    /// Custom tooltip placeholder so we don't lose dimensions.
    /// </summary>
    [Parameter] public RenderFragment<ScatterPoint> TooltipPlaceholder { get; set; }

    /// <summary>
    /// Event: user clicks a point.
    /// </summary>
    [Parameter] public EventCallback<ScatterPoint> PointClicked { get; set; }

    /// <summary>
    /// Event: highlight index changes (hover).
    /// </summary>
    [Parameter] public EventCallback<int> HighlightChanged { get; set; }

    /// <summary>
    /// Optional CSS class on the outermost div.
    /// </summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// Optional inline style on the outermost div.
    /// </summary>
    [Parameter] public string? Style { get; set; }

    /// <summary>
    /// Catch-all for extra attributes.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }

    #endregion

    #region Injected Services

    [Inject] private IJSRuntime JsRuntime { get; set; }
    [Inject] private ScatterHighlightService HighlightService { get; set; }
    [Inject] private GlobalTooltipService TooltipService { get; set; }

    #endregion

    #region Internal State

    // The index of the currently hovered point, for tooltip usage
    private int HighlightIndex { get; set; } = -1;

    // Minimal tooltip subscription
    private readonly TooltipSubscription _tooltipSubscription = new();

    // The unique element IDs used in the .razor file
    private readonly string CanvasId = $"scatter-canvas-{Guid.NewGuid():N}";
    private readonly string HistogramId = $"scatter-histogram-{Guid.NewGuid():N}";
    private readonly string HighlightCircleId = $"scatter-highlight-{Guid.NewGuid():N}";
    private readonly string TooltipId = $"scatter-highlight-{Guid.NewGuid():N}";
    private readonly string RangeHighlightId = $"scatter-range-{Guid.NewGuid():N}";
    private readonly string TopLineId = $"scatter-line-top-{Guid.NewGuid():N}";
    private readonly string CenterLineId = $"scatter-line-center-{Guid.NewGuid():N}";
    private readonly string BottomLineId = $"scatter-line-bottom-{Guid.NewGuid():N}";
    private readonly string TopLabelId = $"scatter-label-top-{Guid.NewGuid():N}";
    private readonly string CenterLabelId = $"scatter-label-center-{Guid.NewGuid():N}";
    private readonly string BottomLabelId = $"scatter-label-bottom-{Guid.NewGuid():N}";

    // Reference to the JS module
    private IJSObjectReference? _module;
    private DotNetObjectReference<SingleAxisScatter>? _dotNetRef;

    #endregion

    #region Lifecycle

    protected override async Task OnInitializedAsync()
    {
        // Create a .NET reference so JS can call back into this instance
        _dotNetRef = DotNetObjectReference.Create(this);

        // Subscribe to highlight sync
        HighlightService.HighlightChanged += OnExternalHighlightChanged;

        await base.OnInitializedAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Load the JS module
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/SingleAxisScatter/SingleAxisScatter.razor.js");

            // Initialize the scatter in JS
            await InitializeScatterAsync();
        }
        await base.OnAfterRenderAsync(firstRender);
    }

    protected override async Task OnParametersSetAsync()
    {
        bool pointsChanged = false;
        
        // Check if the legacy Points property has changed
        if (_previousPoints == null || 
            _previousPoints.Count != Points.Count || 
            !Points.SequenceEqual(_previousPoints))
        {
            // Legacy mode - update PointsCollections from Points
            if (Points.Count > 0)
            {
                PointsCollections = new List<List<ScatterPoint>> { Points.ToList() };
                pointsChanged = true;
            }
        }
        // If we're not using legacy mode with Points, check if PointsCollections changed
        else if (PointsCollections != null && PointsCollections.Count > 0)
        {
            // Check if PointsCollections structure has changed
            if (_previousPointsCollections == null || 
                _previousPointsCollections.Count != PointsCollections.Count)
            {
                pointsChanged = true;
            }
            else
            {
                // Compare each collection
                for (int collectionIndex = 0; collectionIndex < PointsCollections.Count; collectionIndex++)
                {
                    var currentCollection = PointsCollections[collectionIndex];
                    var previousCollection = _previousPointsCollections[collectionIndex];
                    
                    if (currentCollection.Count != previousCollection.Count)
                    {
                        pointsChanged = true;
                        break;
                    }
                    
                    // Compare each point within the collection
                    for (int pointIndex = 0; pointIndex < currentCollection.Count; pointIndex++)
                    {
                        if (currentCollection[pointIndex] != previousCollection[pointIndex])
                        {
                            pointsChanged = true;
                            break;
                        }
                    }
                    
                    if (pointsChanged) break;
                }
            }
        }

        // Store current state for next comparison
        _previousPointsCollections = PointsCollections.Select(collection => collection.ToList()).ToList();
        _previousPoints = Points.ToList();

        // Update JS if points or other parameters changed
        if (pointsChanged)
            await UpdateScatterAsync();

        await base.OnParametersSetAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Unsubscribe from highlight sync
        HighlightService.HighlightChanged -= OnExternalHighlightChanged;

        if (_module != null)
        {
            await _module.InvokeVoidAsync("disposeScatterPlot", CanvasId);
            await _module.DisposeAsync();
        }

        _dotNetRef?.Dispose();
        _tooltipSubscription.Dispose();
    }

    #endregion

    #region JS Interop

    private async Task InitializeScatterAsync()
    {
        if (_module == null) return;
        try
        {
            await _module.InvokeVoidAsync("initializeScatterPlot",
                                          CanvasId,
                                          HistogramId,
                                          HighlightCircleId,
                                          RangeHighlightId,
                                          TopLineId,
                                          CenterLineId,
                                          BottomLineId,
                                          TopLabelId,
                                          CenterLabelId,
                                          BottomLabelId,
                                          _dotNetRef,
                                          GetConfigData());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing SingleAxisScatter with canvas ID {CanvasId}", CanvasId);
        }
    }

    private async Task UpdateScatterAsync()
    {
        if (_module == null) return;
        try
        {
            await _module.InvokeVoidAsync("updateScatterPlot",
                CanvasId,
                GetConfigData()
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating SingleAxisScatter with canvas ID {CanvasId}", CanvasId);
        }
    }
    
    private object GetConfigData()
    {
        return new
        {
            pointRadius = PointRadius,
            zoom = Zoom,
            rangeMin = RangeHighlightMin,
            rangeMax = RangeHighlightMax,
            pointCollections = GetPointCollectionsData(),
            opacity = 1.0,
            yAxisMin = YAxisMin,
            yAxisMax = YAxisMax
        };
    }

    #endregion

    #region JS -> .NET Callbacks

    private async Task OnExternalHighlightChanged(object sender, int index, ScatterPoint? item)
    {
        if (sender == this) return;
        
        // Handle tooltip state through Blazor
        if (index >= 0 && PointsCollections.Count > 0 && index < PointsCollections[0].Count)
            await SetHighlightIndex(index);
        else
            await SetHighlightIndex(-1);
    }

    [JSInvokable]
    public async Task OnPointHovered(int index)
    {
        // We consider a point valid if it exists in at least one collection
        bool isValidIndex = false;
        ScatterPoint? pointForHighlight = null;
        
        if (index >= 0 && PointsCollections.Count > 0)
        {
            // Use first non-null point at this index for highlight service
            foreach (var collection in PointsCollections)
            {
                if (index < collection.Count)
                {
                    isValidIndex = true;
                    var point = collection[index];
                    if (point.Value.HasValue)
                    {
                        pointForHighlight = point;
                        break;
                    }
                }
            }
        }
        
        if (!isValidIndex)
        {
            await SetHighlightIndex(-1);
            await HighlightService.SetHighlight(this, -1, null);
            return;
        }
        
        await SetHighlightIndex(index);
        await HighlightService.SetHighlight(this, index, pointForHighlight);
    }

    [JSInvokable]
    public async Task OnPointClicked(int index, int collectionIndex)
    {
        if (index < 0 || collectionIndex < 0 || collectionIndex >= PointsCollections.Count || 
            index >= PointsCollections[collectionIndex].Count) return;
            
        await PointClicked.InvokeAsync(PointsCollections[collectionIndex][index]);
    }

    [JSInvokable]
    public async Task RequestRedraw()
    {
        // If JS wants a forced redraw, we re‐send updated data
        await UpdateScatterAsync();
    }

    #endregion

    #region Helpers

    private async Task SetHighlightIndex(int index)
    {
        if (HighlightIndex == index) 
            return;
        
        HighlightIndex = index;

        if (TooltipTemplate != null)
        {
            ScatterPoint? pointToShow = null;
            
            // For tooltip display, use the first non-null point from any collection
            if (index >= 0)
            {
                foreach (var collection in PointsCollections)
                {
                    if (index < collection.Count && collection[index].Value.HasValue)
                    {
                        pointToShow = collection[index];
                        break;
                    }
                }
                
                if (pointToShow != null)
                {
                    // Show tooltip
                    if (_tooltipSubscription.OpenCallback != null)
                        await _tooltipSubscription.OpenCallback();
            
                    await TooltipService.ShowTooltip(TooltipId);
                }
                else
                {
                    // No valid point found at this index
                    if (_tooltipSubscription.CloseCallback != null)
                        await _tooltipSubscription.CloseCallback();
            
                    await TooltipService.HideTooltip(TooltipId);
                }
            }
            else
            {
                // Hide tooltip for negative index
                if (_tooltipSubscription.CloseCallback != null)
                    await _tooltipSubscription.CloseCallback();
        
                await TooltipService.HideTooltip(TooltipId);
            }
        }

        await HighlightChanged.InvokeAsync(index);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Convert the C# scatter points into JS-friendly objects.</summary>
    private object GetPointCollectionsData()
    {
        // For backward compatibility, if Points has data and PointsCollections is empty,
        // we'll use Points as a single collection
        if (Points.Count > 0 && (PointsCollections == null || PointsCollections.Count == 0))
        {
            // Create a direct format of the Points collection for JS without trying to nest it
            var pointsData = Points.Select((p, i) => new
            {
                index = i,
                collectionIndex = 0,
                val = p.Value,
                rgb = new[] { p.Color.R, p.Color.G, p.Color.B }
            }).ToList();
            
            return new List<object> { pointsData };
        }
        
        // Format the points collections for JS
        return PointsCollections.Select((collection, collectionIndex) => 
            collection.Select((p, i) => new
            {
                index = i,
                collectionIndex = collectionIndex,
                val = p.Value,
                rgb = new[] { p.Color.R, p.Color.G, p.Color.B }
            }).ToList()
        ).ToList();
    }

    #endregion
}

/// <summary>
/// A single data point with a Y-value, color, optional metadata.
/// </summary>
public struct ScatterPoint : IEquatable<ScatterPoint>
{
    public double? Value { get; set; }
    public Color Color { get; set; }
    public object? Metadata { get; set; }

    public ScatterPoint(double? value, Color color, object? metadata = null)
    {
        Value = value;
        Color = color;
        Metadata = metadata;
    }

    public override bool Equals(object? obj)
    {
        return obj is ScatterPoint point && Equals(point);
    }

    public bool Equals(ScatterPoint other)
    {
        return Value == other.Value &&
               Color.Equals(other.Color) &&
               ReferenceEquals(Metadata, other.Metadata);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Value, Color, Metadata);
    }

    public static bool operator ==(ScatterPoint left, ScatterPoint right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ScatterPoint left, ScatterPoint right)
    {
        return !left.Equals(right);
    }
}
