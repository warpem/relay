using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.TomogramViewer;
using Refund.Services;

namespace Refund.Components.TomogramViewerJs;

public partial class TomogramViewerJs : IAsyncDisposable
{
    private readonly string _elementId = "tomogram-viewer-js-" + Guid.NewGuid().ToString("N");
    private ElementReference _containerElement;
    private DotNetObjectReference<TomogramViewerJs> _dotNetRef;
    private IJSObjectReference _module;
    private bool _initialized;

    private string _tomogramPath;
    private List<ParticleSpecies> _species;

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    [Inject]
    private FileService FileService { get; set; }

    [Parameter]
    public string TomogramPath { get; set; }

    [Parameter]
    public List<ParticleSpecies> Species { get; set; }

    [Parameter]
    public int MinWidth { get; set; } = 800;

    [Parameter]
    public int MinHeight { get; set; } = 600;

    [Parameter]
    public EventCallback<(int SpeciesIndex, Particle Particle)> OnParticleAdded { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            _module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Refund/Components/TomogramViewerJs/TomogramViewerJs.razor.js");

            string fileUrl = null;
            if (!string.IsNullOrEmpty(TomogramPath))
            {
                fileUrl = FileService.GetUrl(TomogramPath);
                _tomogramPath = TomogramPath;
            }

            await _module.InvokeVoidAsync("initialize", _containerElement, _dotNetRef, fileUrl,
                new
                {
                    minWidth = MinWidth,
                    minHeight = MinHeight,
                });

            _initialized = true;

            if (Species != null && Species.Count > 0)
            {
                _species = Species;
                await PushSpeciesToJs();
            }
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized) return;

        if (TomogramPath != _tomogramPath)
        {
            _tomogramPath = TomogramPath;
            if (!string.IsNullOrEmpty(TomogramPath))
            {
                var url = FileService.GetUrl(TomogramPath);
                await _module.InvokeVoidAsync("loadVolumeByUrl", _elementId, url);
            }
        }

        if (Species != _species)
        {
            _species = Species;
            await PushSpeciesToJs();
        }
    }

    private async Task PushSpeciesToJs()
    {
        if (_module == null) return;

        if (_species == null || _species.Count == 0)
        {
            await _module.InvokeVoidAsync("setSpecies", _elementId, Array.Empty<object>());
            return;
        }

        var serialized = _species.Select(s => new
        {
            name = s.Name ?? "Species",
            particles = (s.Particles ?? new()).Select(p => new
            {
                x = p.Position.X,
                y = p.Position.Y,
                z = p.Position.Z,
                rot = p.Angles.X,
                tilt = p.Angles.Y,
                psi = p.Angles.Z
            }).ToArray(),
            modelVolumeUrl = !string.IsNullOrEmpty(s.ModelVolumePath)
                ? FileService.GetUrl(s.ModelVolumePath)
                : null,
            color = s.Color,
            diameter = s.Diameter
        }).ToArray();

        await _module.InvokeVoidAsync("setSpecies", _elementId, serialized);
    }

    [JSInvokable]
    public async Task OnParticleAddedFromJs(int speciesIndex, float x, float y, float z)
    {
        var particle = new Particle(new Warp.Tools.float3(x, y, z));
        await OnParticleAdded.InvokeAsync((speciesIndex, particle));
    }

    public async ValueTask DisposeAsync()
    {
        if (_module != null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _elementId);
                await _module.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors (e.g. circuit disconnected)
            }
        }

        _dotNetRef?.Dispose();
    }
}
