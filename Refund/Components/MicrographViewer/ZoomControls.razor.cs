using Microsoft.AspNetCore.Components;

namespace Refund.Components.MicrographViewer;

/// <summary>
/// Controls component for zoom and viewport adjustment in the MicrographViewer.
/// Provides buttons for zooming in/out, fitting to viewport, resetting zoom,
/// and toggling the scale bar.
/// </summary>
public partial class ZoomControls : ComponentBase 
{
    /// <summary>
    /// Current zoom level of the micrograph viewer.
    /// 1.0 represents the original size, values greater than 1 represent zooming in,
    /// and values less than 1 represent zooming out.
    /// </summary>
    [Parameter]
    public double ZoomLevel { get; set; }

    /// <summary>
    /// Event callback that fires when the zoom level changes.
    /// Passes the new zoom level to the parent component.
    /// </summary>
    [Parameter]
    public EventCallback<double> OnZoomChanged { get; set; }

    /// <summary>
    /// Event callback that fires when the "Fit to Viewport" button is clicked.
    /// Notifies the parent component to adjust the zoom to fit the entire micrograph.
    /// </summary>
    [Parameter]
    public EventCallback OnFitToViewport { get; set; }
    
    /// <summary>
    /// Event callback that fires when the scale bar visibility toggle changes.
    /// Passes the new toggle state to the parent component.
    /// </summary>
    [Parameter]
    public EventCallback<bool> OnIsToggledScaleChanged { get; set; }

    private bool _IsToggledScale;
    
    /// <summary>
    /// Controls whether the scale bar is visible in the micrograph viewer.
    /// Changes to this property will automatically notify the parent component.
    /// </summary>
    [Parameter]
    public bool IsToggledScale
    {
        get => _IsToggledScale;
        set
        {
            if (_IsToggledScale != value)
            {
                _IsToggledScale = value;
                OnIsToggledScaleChanged.InvokeAsync(value);
            }
        }
    }

    /// <summary>
    /// Increases the zoom level by 20%, up to a maximum of 10x.
    /// Notifies the parent component of the new zoom level.
    /// </summary>
    private void ZoomIn()
    {
        ZoomLevel = Math.Min(ZoomLevel * 1.2, 10);
        OnZoomChanged.InvokeAsync(ZoomLevel);
    }

    /// <summary>
    /// Decreases the zoom level by ~17% (1/1.2), down to a minimum of 0.1x.
    /// Notifies the parent component of the new zoom level.
    /// </summary>
    private void ZoomOut()
    {
        ZoomLevel = Math.Max(ZoomLevel / 1.2, 0.1);
        OnZoomChanged.InvokeAsync(ZoomLevel);
    }

    /// <summary>
    /// Notifies the parent component to adjust the zoom to fit the entire micrograph
    /// within the viewport while maintaining the aspect ratio.
    /// </summary>
    private void FitToViewport()
    {
        OnFitToViewport.InvokeAsync();
    } 
    
    /// <summary>
    /// Resets the zoom level to 1.0 (100% or original size).
    /// Notifies the parent component of the new zoom level.
    /// </summary>
    private void FitToOriginal()
    {
        ZoomLevel = 1;
        OnZoomChanged.InvokeAsync(ZoomLevel);
    }
}