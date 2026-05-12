using System.Drawing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Refund.Components.MicrographViewer;

/// <summary>
/// A thumbnail overview component that displays a small version of the micrograph with
/// an indicator showing the current viewport position.
/// Allows quick navigation by clicking on the desired location.
/// </summary>
public partial class MiniMap : ComponentBase
{
    /// <summary>
    /// Base64-encoded thumbnail image to display in the minimap.
    /// </summary>
    [Parameter]
    public string ThumbnailImageBase64 { get; set; }

    /// <summary>
    /// Rectangle representing the currently visible viewport within the full micrograph.
    /// Used to draw a highlight box on the minimap.
    /// </summary>
    [Parameter]
    public RectangleF ViewportRectangle { get; set; }

    /// <summary>
    /// Event callback that fires when the user clicks on the minimap.
    /// Contains the X,Y coordinates in micrograph space where the user clicked.
    /// </summary>
    [Parameter]
    public EventCallback<(double X, double Y)> OnMiniMapClick { get; set; }

    /// <summary>
    /// JavaScript runtime for DOM manipulation.
    /// </summary>
    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    /// <summary>
    /// Reference to the minimap div element for position calculations.
    /// </summary>
    private ElementReference miniMapDiv;

    /// <summary>
    /// Width of the thumbnail image in pixels.
    /// </summary>
    [Parameter]
    public double ThumbnailWidth { get; set; }

    /// <summary>
    /// Height of the thumbnail image in pixels.
    /// </summary>
    [Parameter]
    public double ThumbnailHeight { get; set; }

    /// <summary>
    /// Width of the full micrograph in pixels.
    /// </summary>
    [Parameter]
    public double MicrographWidth { get; set; }

    /// <summary>
    /// Height of the full micrograph in pixels.
    /// </summary>
    [Parameter]
    public double MicrographHeight { get; set; }

    /// <summary>
    /// Calculated X position of the viewport rectangle within the minimap.
    /// </summary>
    private double ViewportRectX => (ViewportRectangle.X / MicrographWidth) * ThumbnailWidth;
    
    /// <summary>
    /// Calculated Y position of the viewport rectangle within the minimap.
    /// </summary>
    private double ViewportRectY => (ViewportRectangle.Y / MicrographHeight) * ThumbnailHeight;
    
    /// <summary>
    /// Calculated width of the viewport rectangle within the minimap.
    /// </summary>
    private double ViewportRectWidth => (ViewportRectangle.Width / MicrographWidth) * ThumbnailWidth;
    
    /// <summary>
    /// Calculated height of the viewport rectangle within the minimap.
    /// </summary>
    private double ViewportRectHeight => (ViewportRectangle.Height / MicrographHeight) * ThumbnailHeight;

    /// <summary>
    /// Event handler for clicks on the minimap.
    /// Converts click coordinates from client space to minimap space,
    /// then to full micrograph coordinates before invoking the callback.
    /// </summary>
    /// <param name="e">Mouse event arguments containing click position.</param>
    private async Task OnClick(MouseEventArgs e)
    {
        // Get the bounding rectangle of the minimap element
        var rect = await JsRuntime.InvokeAsync<DomRect>("getElementBoundingClientRect", miniMapDiv);
        
        if (rect.Width == 0 || rect.Height == 0)
            return;
            
        // Calculate click position relative to the minimap as a percentage
        var clickXPercent = (e.ClientX - rect.Left) / rect.Width;
        var clickYPercent = (e.ClientY - rect.Top) / rect.Height;

        // Convert percentage position to micrograph coordinates
        var micrographX = clickXPercent * MicrographWidth;
        var micrographY = clickYPercent * MicrographHeight;

        // Notify parent component about the click
        await OnMiniMapClick.InvokeAsync((micrographX, micrographY));
    }

    /// <summary>
    /// Helper class to store element dimensions from JavaScript.
    /// Used for converting client coordinates to element-relative coordinates.
    /// </summary>
    private class DomRect
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}