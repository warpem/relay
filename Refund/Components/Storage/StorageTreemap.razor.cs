using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.Jobs;
using Refund.DataModel.ReadOnly;
using Refund.Services.Storage;

namespace Refund.Components.Storage;

/// <summary>
/// A cushion treemap of disk usage: area is bytes on disk (or files), colour is age, pipeline stage or owner.
/// </summary>
/// <remarks>
/// Colours were checked with the dataviz validator against Relay's surfaces (#ffffff light, #333333 dark). A treemap can
/// put any two colours side by side, so categorical colour is limited to the three stage hues that stay distinct for every
/// pair (blue, orange, aqua; grey for the rest); exact job types live in labels, tooltips, the breakdown and the table.
/// Age uses a five-step single-hue ramp (light: recent pale, old dark; dark theme: old brightest).
/// </remarks>
public partial class StorageTreemap : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Parameter] public TreemapPayload Payload { get; set; }
    /// <summary>"bytes" or "files".</summary>
    [Parameter] public string Metric { get; set; } = "bytes";
    /// <summary>"age", "stage" or "owner".</summary>
    [Parameter] public string ColorBy { get; set; } = "age";
    [Parameter] public bool Dark { get; set; }
    /// <summary>The job to outline (hovered or selected in the table).</summary>
    [Parameter] public StorageRow Highlight { get; set; }
    [Parameter] public string EmptyText { get; set; } = "Nothing measured here yet.";
    [Parameter] public string AriaLabel { get; set; } = "Disk usage treemap";
    [Parameter] public EventCallback<TreemapTarget> OnSelect { get; set; }
    [Parameter] public EventCallback<TreemapTarget> OnZoom { get; set; }
    /// <summary>The canvas size in CSS pixels, so the caller can fold away tiles too small to see.</summary>
    [Parameter] public EventCallback<(double Width, double Height)> OnResized { get; set; }

    private readonly string _id = "treemap-" + Guid.NewGuid().ToString("N");
    private string _anchorId => _id + "-anchor";
    private string _cardSuffix => _id + "-card";
    private ElementReference _canvas, _overlay, _tooltip, _anchor;
    // Job cards come from the host (Relay); hosts without one (the component playground) keep the plain tooltip.
    private IJobPreviewRenderer _renderer;
    private HoverPreview<ReadOnlyJob> _preview;
    private string[] _summary;
    private IJSObjectReference _module;
    private DotNetObjectReference<StorageTreemap> _self;
    private TreemapPayload _sent;
    private (string Metric, string ColorBy, bool Dark, int Highlight) _sentView = (null, null, false, -2);
    private bool _disposed;
    private string _failed;

    protected override void OnInitialized()
    {
        _renderer = Services.GetService(typeof(IJobPreviewRenderer)) as IJobPreviewRenderer;
        _preview = new(() => InvokeAsync(StateHasChanged));
    }

    protected override void OnParametersSet()
    {
        // A new layout moves every tile: close a card that was opened for the old one.
        if (!ReferenceEquals(_sent, Payload) && _preview.Current != null)
            _preview.Reset();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                _module = await Js.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/Storage/StorageTreemap.razor.js");
                if (_disposed) { await _module.DisposeAsync(); return; }
                _self = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("create", _id, _canvas, _overlay, _tooltip, _self, _anchor, _renderer != null);
            }
            if (_module == null) return;
            if (!ReferenceEquals(_sent, Payload) && Payload != null)
            {
                _sent = Payload;
                _sentView = (null, null, false, -2);
                await _module.InvokeVoidAsync("setData", _id, Payload.Nodes, Payload.Labels, Payload.Details);
            }
            int highlight = HighlightIndex();
            var view = (Metric, ColorBy, Dark, highlight);
            if (view != _sentView)
            {
                _sentView = view;
                await _module.InvokeVoidAsync("setView", _id, new { metric = Metric, colorBy = ColorBy, dark = Dark, highlight });
            }
        }
        catch (JSDisconnectedException) { }
        catch (Exception e) when (_disposed && e is ObjectDisposedException or OperationCanceledException or JSException) { }
        catch (JSException e) when (_module == null)
        {
            // A map that can't load must not take the page down; the table still lists everything.
            _failed = $"The map couldn't load ({e.Message.Split('\n')[0]}). Reload the page to try again.";
            StateHasChanged();
        }
    }

    private int HighlightIndex()
    {
        if (Highlight == null || Payload == null) return -1;
        for (int i = 0; i < Payload.Targets.Count; i++)
            if (Payload.Targets[i] is { Kind: TreemapKind.Job } t && ReferenceEquals(t.Row, Highlight))
                return i;
        return -1;
    }

    [JSInvokable]
    public async Task Clicked(int index, bool zoom)
    {
        if (_disposed || Payload == null || index < 0 || index >= Payload.Targets.Count) return;
        var target = Payload.Targets[index];
        if (target == null) return;
        if (zoom && target.Kind is TreemapKind.Project or TreemapKind.Space or TreemapKind.Folder)
            await OnZoom.InvokeAsync(target);
        else if (!zoom && target.Row != null)
            await OnSelect.InvokeAsync(target);
    }

    /// <summary>The pointer entered a tile of a job (its index), or left every job (-1).</summary>
    [JSInvokable]
    public async Task Hovered(int jobIndex, string[] summary)
    {
        if (_disposed) return;
        var job = Payload != null && jobIndex >= 0 && jobIndex < Payload.Targets.Count ? Payload.Targets[jobIndex]?.Row?.Job : null;
        if (job == null)
        {
            _summary = null;
            await _preview.HideAsync();
            return;
        }
        _summary = summary;
        if (_preview.Current == job)
            StateHasChanged();
        else
            await _preview.ShowAsync(job, immediate: false);
    }

    [JSInvokable]
    public Task Resized(double width, double height) => _disposed ? Task.CompletedTask : OnResized.InvokeAsync((width, height));

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_module != null)
            {
                await _module.InvokeVoidAsync("dispose", _id);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (JSException) { }
        _self?.Dispose();
        _preview?.Dispose();
    }
}
