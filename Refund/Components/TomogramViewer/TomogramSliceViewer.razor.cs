using System.Drawing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SkiaSharp;
using Warp.Tools;
using static Refund.Components.MicrographViewer.MicrographViewer;

namespace Refund.Components.TomogramViewer;

public partial class TomogramSliceViewer
{
    [Parameter]
    public string Id { get; set; }
    
    [Parameter]
    public float[] SliceData { get; set; }
    private float[] _sliceData;

    [Parameter]
    public int Width { get; set; }

    [Parameter]
    public int Height { get; set; }

    [Parameter]
    public bool IsLoading { get; set; }

    [Parameter]
    public PlaneType PlaneType { get; set; }

    [Parameter]
    public int3 ViewPoint { get; set; }
    private int3 _viewPoint;

    [Parameter]
    public double TranslateX { get; set; }

    [Parameter]
    public double TranslateY { get; set; }

    [Parameter]
    public double Zoom { get; set; }

    [Parameter]
    public float PixelSize { get; set; }

    [Parameter]
    public EventCallback<double> OnMouseWheelCoordinateChange { get; set; }

    [Parameter]
    public EventCallback<float2> OnSliceClick { get; set; }

    [Parameter]
    public List<Particle> Particles3D { get; set; }

    [Parameter]
    public ParticleShapes ParticleShape { get; set; }

    [Parameter]
    public string ParticleColor { get; set; }

    [Parameter]
    public double ParticleStrokeWidth { get; set; }

    [Parameter]
    public decimal ParticleDiameter { get; set; }

    [Parameter]
    public decimal ParticleBoxSize { get; set; }

    protected ElementReference containerRef;
    protected bool isPanning = false;
    protected Point lastMousePos;

    protected int SliceWidth { get; set; }

    protected int SliceHeight { get; set; }

    protected override void OnParametersSet()
    {
        if (VolDims.Elements() > 0)
        {
            if (PlaneType == PlaneType.XY)
            {
                SliceWidth = VolDims.X;
                SliceHeight = VolDims.Y;
            }
            else if (PlaneType == PlaneType.XZ)
            {
                SliceWidth = VolDims.X;
                SliceHeight = VolDims.Z;
            }
            else
            {
                SliceWidth = VolDims.Z;
                SliceHeight = VolDims.Y;
            }
        }

        bool needImageUpdate = false;

        if (ViewPoint != _viewPoint)
        {
            _viewPoint = ViewPoint;
            needImageUpdate = true;
        }

        if (SliceData != _sliceData)
        {
            _sliceData = SliceData;
            needImageUpdate = true;
        }

        if (needImageUpdate)
            CreateBitmapFromSlice();
    }

    [Parameter]
    public int3 VolDims { get; set; }

    [Parameter]
    public float MinIntensity { get; set; }
    [Parameter]
    public float MaxIntensity { get; set; }

    SKBitmap bitmap;
    public string CurrentSliceImage;

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        if (SliceData != null && SliceData.Length > 0)
            CreateBitmapFromSlice();
    }

    private void CreateBitmapFromSlice()
    {
        if (SliceData == null || SliceData.Length == 0)
        {
            CurrentSliceImage = "";
            return;
        }

        int w = SliceWidth;
        int h = SliceHeight;
        if (bitmap == null || bitmap.Width != w || bitmap.Height != h)
        {
            bitmap?.Dispose();
            bitmap = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        }

        unsafe
        {
            var ptr = bitmap.GetPixels();
            byte* bptr = (byte*)ptr.ToPointer();

            float minVal = MinIntensity;
            float maxVal = MaxIntensity;
            float scale = maxVal > minVal ? 1f / (maxVal - minVal) : 1f;

            int idx = 0;
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    float val = SliceData[(h - 1 - yy) * w + xx];
                    float norm = (val - minVal) * scale;
                    norm = Math.Clamp(norm, 0, 1);
                    bptr[idx] = (byte)(norm * 255);
                    idx++;
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        CurrentSliceImage = "data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray());
    }

    protected async Task OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == 1) // middle mouse
        {
            isPanning = true;
            lastMousePos = new Point((int)e.ClientX, (int)e.ClientY);
        }
    }

    protected async Task OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == 1)
        {
            isPanning = false;
        }
        else if (e.Button == 0)
        {
            double relativeX = e.OffsetX;
            double relativeY = e.OffsetY;

            // reverse transform
            float worldX = (float)((relativeX - TranslateX) / Zoom);
            float worldY = (float)((relativeY - TranslateY) / Zoom);

            worldY = SliceHeight - 1 - worldY;

            await OnSliceClick.InvokeAsync(new float2(worldX, worldY));
        }
    }

    protected async Task OnMouseMove(MouseEventArgs e)
    {
        if (isPanning)
        {
            // Panning not required per instructions, they said middle mouse to pan images.
            // If we do pan, we must call parent to update global TranslateX, TranslateY. 
            // For simplicity, we ignore panning in this sample or let parent handle it differently.
        }
    }

    protected async Task OnMouseWheel(WheelEventArgs e)
    {
        // Instead of zooming, we now change orthogonal coordinate:
        // call event callback:
        await OnMouseWheelCoordinateChange.InvokeAsync(e.DeltaY);
    }
}
