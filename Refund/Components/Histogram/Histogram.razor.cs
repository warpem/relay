using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Globalization;

namespace Refund.Components.Histogram;

public partial class Histogram : IAsyncDisposable
{
    #region Parameters

    /// <summary>
    /// The bin sizes for the primary histogram. Each value represents the height of a bin.
    /// </summary>
    [Parameter] public decimal[] BinSizes { get; set; } = Array.Empty<decimal>();
    private decimal[] _previousBinSizes = Array.Empty<decimal>();

    /// <summary>
    /// The bin sizes for the secondary histogram. Each value represents the height of a bin.
    /// If provided, will render as an overlapping histogram with the primary one.
    /// Must have the same length as BinSizes to render properly.
    /// </summary>
    [Parameter] public decimal[] SecondaryBinSizes { get; set; } = Array.Empty<decimal>();
    private decimal[] _previousSecondaryBinSizes = Array.Empty<decimal>();

    /// <summary>
    /// The color for the secondary histogram bars.
    /// </summary>
    [Parameter] public string SecondaryColor { get; set; } = "#8A2BE2"; // Default to BlueViolet

    /// <summary>
    /// The minimum value represented in the histogram (left edge of first bin).
    /// </summary>
    [Parameter] public decimal MinRange { get; set; } = 0;

    /// <summary>
    /// The maximum value represented in the histogram (right edge of last bin).
    /// </summary>
    [Parameter] public decimal MaxRange { get; set; } = 1;

    /// <summary>
    /// The fill color for the histogram bars.
    /// </summary>
    [Parameter] public string Color { get; set; } = "#0078D4";

    /// <summary>
    /// Whether to enable range selection on the histogram.
    /// </summary>
    [Parameter] public bool RangeSelectionEnabled { get; set; } = false;
    
    /// <summary>
    /// Whether the component is disabled (disables all interaction).
    /// </summary>
    [Parameter] public bool IsDisabled { get; set; } = false;

    /// <summary>
    /// The start value of the selected range.
    /// </summary>
    [Parameter] public decimal SelectedRangeStart { get; set; } = 0;

    /// <summary>
    /// The end value of the selected range.
    /// </summary>
    [Parameter] public decimal SelectedRangeEnd { get; set; } = 1;
    
    /// <summary>
    /// Event fired when the selected range changes.
    /// </summary>
    [Parameter] public EventCallback<(decimal Start, decimal End)> SelectedRangeChanged { get; set; }

    /// <summary>
    /// The step size for range adjustments.
    /// </summary>
    [Parameter] public decimal StepSize { get; set; } = 0.1m;

    /// <summary>
    /// The minimum gap (in steps) between the start and end of the range.
    /// </summary>
    [Parameter] public int MinGap { get; set; } = 1;
    
    /// <summary>
    /// Optional CSS class for the container.
    /// </summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// Optional inline style for the container.
    /// </summary>
    [Parameter] public string? Style { get; set; }

    /// <summary>
    /// Catch-all for additional attributes.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }

    #endregion

    #region Injected Services

    [Inject] private IJSRuntime JsRuntime { get; set; }
    [Inject] private ILogger<Histogram> Logger { get; set; } = default!;

    #endregion

    #region Internal State

    // Unique element IDs
    private readonly string HistogramId = $"histogram-{Guid.NewGuid():N}";
    private readonly string RangeId = $"histogram-range-{Guid.NewGuid():N}";
    private readonly string RangeStartHandleId = $"histogram-range-start-{Guid.NewGuid():N}";
    private readonly string RangeEndHandleId = $"histogram-range-end-{Guid.NewGuid():N}";

    // JS module reference
    private IJSObjectReference? _module;
    private DotNetObjectReference<Histogram>? _dotNetRef;

    private string MinRangeFormatted => FormatValue(MinRange);
    private string MaxRangeFormatted => FormatValue(MaxRange);
    
    #endregion

    #region Lifecycle

    protected override async Task OnInitializedAsync()
    {
        // Create .NET reference for JS callbacks
        _dotNetRef = DotNetObjectReference.Create(this);
        await base.OnInitializedAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Load JS module
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/Histogram/Histogram.razor.js");

            // Initialize histogram
            await InitializeHistogramAsync();
        }
        
        await base.OnAfterRenderAsync(firstRender);
    }

    protected override async Task OnParametersSetAsync()
    {
        // Check if bin sizes changed
        bool binsChanged = false;
        bool secondaryBinsChanged = false;
        
        if (_previousBinSizes.Length != BinSizes.Length)
        {
            binsChanged = true;
        }
        else
        {
            for (int i = 0; i < BinSizes.Length; i++)
            {
                if (BinSizes[i] != _previousBinSizes[i])
                {
                    binsChanged = true;
                    break;
                }
            }
        }

        // Check if secondary bin sizes changed
        if (_previousSecondaryBinSizes.Length != SecondaryBinSizes.Length)
        {
            secondaryBinsChanged = true;
        }
        else
        {
            for (int i = 0; i < SecondaryBinSizes.Length; i++)
            {
                if (SecondaryBinSizes[i] != _previousSecondaryBinSizes[i])
                {
                    secondaryBinsChanged = true;
                    break;
                }
            }
        }

        // Update previous values for next comparison
        _previousBinSizes = BinSizes.ToArray();
        _previousSecondaryBinSizes = SecondaryBinSizes.ToArray();

        // Update JS if needed
        if (binsChanged || secondaryBinsChanged || MinRange != _previousMinRange || MaxRange != _previousMaxRange || 
            Color != _previousColor || SecondaryColor != _previousSecondaryColor || SelectedRangeStart != _previousRangeStart || 
            SelectedRangeEnd != _previousRangeEnd)
        {
            await UpdateHistogramAsync();
            
            _previousMinRange = MinRange;
            _previousMaxRange = MaxRange;
            _previousColor = Color;
            _previousSecondaryColor = SecondaryColor;
            _previousRangeStart = SelectedRangeStart;
            _previousRangeEnd = SelectedRangeEnd;
        }

        await base.OnParametersSetAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module != null)
            {
                await _module.InvokeVoidAsync("disposeHistogram", HistogramId);
                await _module.DisposeAsync();
            }

            _dotNetRef?.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    #endregion

    #region JS Interop

    private async Task InitializeHistogramAsync()
    {
        if (_module == null) return;
        
        try
        {
            await _module.InvokeVoidAsync("initializeHistogram",
                HistogramId,
                RangeId,
                RangeStartHandleId,
                RangeEndHandleId,
                _dotNetRef,
                GetConfigData());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing Histogram with ID {HistogramId}", HistogramId);
        }
    }

    private async Task UpdateHistogramAsync()
    {
        if (_module == null) return;
        
        try
        {
            await _module.InvokeVoidAsync("updateHistogram",
                HistogramId,
                GetConfigData());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating Histogram with ID {HistogramId}", HistogramId);
        }
    }

    private object GetConfigData()
    {
        return new
        {
            binSizes = BinSizes,
            secondaryBinSizes = SecondaryBinSizes,
            minRange = MinRange,
            maxRange = MaxRange,
            color = Color,
            secondaryColor = SecondaryColor,
            rangeSelectionEnabled = RangeSelectionEnabled,
            selectedRangeStart = SelectedRangeStart,
            selectedRangeEnd = SelectedRangeEnd,
            stepSize = StepSize,
            minGap = MinGap,
            disabled = IsDisabled
        };
    }

    #endregion

    #region JS -> .NET Callbacks

    [JSInvokable]
    public async Task OnRangeChanged(decimal start, decimal end)
    {
        if (start == SelectedRangeStart && end == SelectedRangeEnd)
        {
            return;
        }
        
        // Update the range values
        SelectedRangeStart = start;
        SelectedRangeEnd = end;
        
        // Update UI and notify parent component immediately
        // JavaScript already handles debouncing - this only gets called on mouse up or mouse leave
        await InvokeAsync(StateHasChanged);
        await SelectedRangeChanged.InvokeAsync((start, end));
    }

    #endregion

    #region Helpers

    // Value change handlers for number inputs
    private async Task OnRangeStartChanged(decimal value)
    {
        // Enforce min gap and constraints
        value = Math.Max(MinRange, value);
        value = Math.Min(SelectedRangeEnd - MinGapInValues, value);
        
        // Apply step size
        decimal stepSize = GetEffectiveStepSize();
        if (stepSize > 0)
        {
            value = Math.Round(value / stepSize) * stepSize;
        }
        
        if (value != SelectedRangeStart)
        {
            SelectedRangeStart = value;
            await UpdateHistogramAsync();
            await SelectedRangeChanged.InvokeAsync((SelectedRangeStart, SelectedRangeEnd));
        }
    }
    
    private async Task OnRangeEndChanged(decimal value)
    {
        // Enforce min gap and constraints
        value = Math.Min(MaxRange, value);
        value = Math.Max(SelectedRangeStart + MinGapInValues, value);
        
        // Apply step size
        decimal stepSize = GetEffectiveStepSize();
        if (stepSize > 0)
        {
            value = Math.Round(value / stepSize) * stepSize;
        }
        
        if (value != SelectedRangeEnd)
        {
            SelectedRangeEnd = value;
            await UpdateHistogramAsync();
            await SelectedRangeChanged.InvokeAsync((SelectedRangeStart, SelectedRangeEnd));
        }
    }
    
    // Calculate minimum gap in terms of values rather than steps
    private decimal MinGapInValues => MinGap * GetEffectiveStepSize();
    
    // Get effective step size, making it relative to the number of bins
    private decimal GetEffectiveStepSize()
    {
        // If step size is 0 or negative, default to 1 bin width
        if (StepSize <= 0)
        {
            return GetBinWidth();
        }
        
        return StepSize;
    }
    
    // Calculate the width of a single bin
    private decimal GetBinWidth()
    {
        if (BinSizes.Length <= 1)
        {
            return 1;
        }
        
        return (MaxRange - MinRange) / BinSizes.Length;
    }

    private string FormatValue(decimal value)
    {
        // Format values based on their magnitude
        if (Math.Abs(value) < 0.01m)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
        else if (Math.Abs(value) < 1m)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
        else if (Math.Abs(value) < 10m)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
        else
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    // Field tracking for parameter changes
    private decimal _previousMinRange;
    private decimal _previousMaxRange;
    private string _previousColor = "#0078D4";
    private string _previousSecondaryColor = "#8A2BE2";
    private decimal _previousRangeStart;
    private decimal _previousRangeEnd;

    #endregion
}