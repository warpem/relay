using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Services;

namespace Refund.Components.MicrographViewer;

/// <summary>Metadata and toolbar bridge. Pixel buffers and viewport interactions stay in the browser.</summary>
public partial class MicrographViewer : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; }
    [Inject] private FileService FileService { get; set; }
    [Parameter] public string MicrographPath { get; set; }
    [Parameter] public string MaskPath { get; set; }
    [Parameter] public string ThumbnailPath { get; set; }
    [Parameter] public string ParticleStarPath { get; set; }
    [Parameter] public int MinWidth { get; set; } = 800;
    [Parameter] public int MinHeight { get; set; } = 600;
    [Parameter] public bool CanEditMask { get; set; } = true;
    [Parameter] public bool CanEditParticles { get; set; } = true;
    [Parameter] public double DefaultParticleDiameter { get; set; } = 100;
    [Parameter] public double DefaultParticleBoxSize { get; set; } = 200;
    [Parameter] public bool CanControlParticleDisplay { get; set; } = true;
    public int Width { get; private set; } = 800;
    public int Height { get; private set; } = 600;

    [Flags] public enum ParticleShapes { None = 0, Circle = 1, Square = 2, Both = Circle | Square }
    private readonly string elementId = "micrograph-viewer-" + Guid.NewGuid().ToString("N");
    private ElementReference containerElement;
    private Task<IJSObjectReference> moduleTask;
    private DotNetObjectReference<MicrographViewer> dotNetRef;
    private bool disposed;
    private string lastOptions;
    private string previousPath;
    private double zoomLevel = 1;
    private bool showScaleBar = true, showMask = true, isPaintingMask, isPickingParticles;
    private decimal particleDiameter = 100, particleBoxSize = 200, maskBrushDiameter = 300;
    private string particleColor = "#FFF700";
    private ParticleShapes particleShape = ParticleShapes.Circle;

    protected override void OnParametersSet()
    {
        if (previousPath == MicrographPath) return;
        previousPath = MicrographPath;
        particleDiameter = (decimal)DefaultParticleDiameter;
        particleBoxSize = (decimal)DefaultParticleBoxSize;
    }

    private string Url(string path) => string.IsNullOrWhiteSpace(path) ? null : FileService.GetUrl(path);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        var path = MicrographPath;
        int layer = 0;
        // Preserve the original viewer's zero-based layer@path convention.
        if (!string.IsNullOrEmpty(path))
        {
            var separator = path.IndexOf('@');
            if (separator > 0 && int.TryParse(path[..separator], out var parsedLayer))
            {
                layer = parsedLayer;
                path = path[(separator + 1)..];
            }
        }
        var options = new
        {
            url = Url(path), layer, maskUrl = Url(MaskPath), thumbnailUrl = Url(ThumbnailPath),
            particlesUrl = Url(ParticleStarPath), minWidth = MinWidth, minHeight = MinHeight,
            showScaleBar, showMask, isPaintingMask = CanEditMask && isPaintingMask,
            isPickingParticles = CanEditParticles && isPickingParticles,
            particleDiameter, particleBoxSize, particleColor, particleShape, maskBrushDiameter
        };
        var serialized = JsonSerializer.Serialize(options);
        if (serialized == lastOptions) return;
        moduleTask ??= JsRuntime.InvokeAsync<IJSObjectReference>("import",
            "./_content/Refund/Components/MicrographViewer/MicrographViewer.razor.js").AsTask();
        var module = await moduleTask;
        if (disposed) return;
        dotNetRef ??= DotNetObjectReference.Create(this);
        lastOptions = serialized;
        await module.InvokeVoidAsync("update", containerElement, options, dotNetRef);
    }

    [JSInvokable]
    public Task OnViewportChanged(double zoom, int width, int height)
    {
        if (disposed) return Task.CompletedTask;
        zoomLevel = zoom;
        Width = width;
        Height = height;
        return InvokeAsync(StateHasChanged);
    }

    private async Task OnZoomChanged(double zoom) => await Command("zoom", zoom);
    private async Task OnFitToViewport() => await Command("fit");
    private Task OnScaleBarToggled(bool value) { showScaleBar = value; return Task.CompletedTask; }
    private async Task Command(string command, double value = 0)
    {
        if (disposed || moduleTask == null) return;
        await (await moduleTask).InvokeVoidAsync("command", elementId, command, value);
    }
    private void OnParticleSettingsChanged(ParticleControls.ParticleSettings settings)
    {
        isPickingParticles = settings.IsPicking;
        particleDiameter = settings.Diameter;
        particleBoxSize = settings.BoxSize;
        particleColor = settings.Color;
        particleShape = settings.Shape | (isPickingParticles ? ParticleShapes.Circle : ParticleShapes.None);
        if (isPickingParticles) isPaintingMask = false;
    }
    private void OnMaskSettingsChanged(MaskControls.MaskSettings settings)
    {
        showMask = settings.IsShowing || settings.IsPainting;
        isPaintingMask = settings.IsPainting;
        maskBrushDiameter = settings.Diameter;
        if (isPaintingMask) isPickingParticles = false;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (moduleTask != null)
            {
                var module = await moduleTask;
                await module.InvokeVoidAsync("dispose", elementId);
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        finally { dotNetRef?.Dispose(); }
    }
}
