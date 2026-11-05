using System.Text.Json;
using Microsoft.JSInterop;
using Refund.Services;
using Microsoft.AspNetCore.Components;

namespace Refund.Components.Class2DSelection;

/// <summary>Metadata bridge to the JavaScript class grid. Pixel buffers and UI state stay in the browser.</summary>
public partial class Class2DSelection : IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; }
    [Inject] private FileService FileService { get; set; }

    [Parameter, EditorRequired] public string MrcStackPath { get; set; }
    [Parameter] public Class2DSubclassGroup[] Subclasses { get; set; } = [];
    [Parameter] public bool SignedContrast { get; set; }
    /// <summary>One-based image numbers, independent of the displayed class IDs.</summary>
    [Parameter, EditorRequired] public int[] ImageNumbers { get; set; }
    [Parameter] public int[] ClassIds { get; set; }
    [Parameter] public string[] ClassLabels { get; set; }
    [Parameter] public float[] ClassResolutions { get; set; }
    [Parameter] public float[] ClassSizes { get; set; }
    [Parameter] public required int RenderedImageSidelengthPx { get; set; }
    [Parameter] public required int SpacingPx { get; set; }
    [Parameter] public int BorderWidthPx { get; set; } = 2;
    [Parameter] public string BorderColorUnselected { get; set; } = "transparent";
    [Parameter] public string BorderColorSelected { get; set; } = "mediumpurple";
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public Action OnSelectionChanged { get; set; }
    [Parameter] public bool SelectionIsMutable { get; set; } = true;

    private HashSet<int> _selectedClasses = new();
    public IReadOnlySet<int> SelectedClasses => _selectedClasses;
    private HashSet<int> _selectedSubclasses = new();
    public IReadOnlySet<int> SelectedSubclasses => _selectedSubclasses;
    private long _selectionVersion;
    private readonly string _id = $"class2d-{Guid.NewGuid()}";
    private ElementReference _root;
    private DotNetObjectReference<Class2DSelection> _callback;
    private Task<IJSObjectReference> _moduleTask;
    private bool _disposed;
    private string _previousPath;
    private string _interopError;
    private string _lastOptions;

    /// <summary>Explicit external selection changes supersede any pending browser callbacks.</summary>
    public void SetSelection(IEnumerable<int> classes)
    {
        _selectedClasses = classes.ToHashSet();
        _selectionVersion++;
        StateHasChanged();
    }

    [JSInvokable]
    public void ReceiveSelection(int[] classes, long version)
    {
        if (_disposed || !SelectionIsMutable || version != _selectionVersion) return;
        _selectedClasses = classes.Intersect(ImageNumbers ?? []).ToHashSet();
        OnSelectionChanged?.Invoke();
    }

    [JSInvokable]
    public void ReceiveSubclassSelection(int[] classes, long version)
    {
        if (_disposed || !SelectionIsMutable || version != _selectionVersion) return;
        _selectedSubclasses = classes.Intersect(Subclasses.SelectMany(g => g.Classes.Select(c => c.Id))).ToHashSet();
        OnSelectionChanged?.Invoke();
    }

    protected override void OnParametersSet()
    {
        if (_previousPath == MrcStackPath) return;
        _previousPath = MrcStackPath;
        _interopError = null;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _interopError != null) return;
        try
        {
            _callback ??= DotNetObjectReference.Create(this);
            _moduleTask ??= JsRuntime.InvokeAsync<IJSObjectReference>("import",
                "./_content/Refund/Components/Class2DSelection/Class2DSelection.razor.js").AsTask();
            var module = await _moduleTask;
            if (_disposed) return;
            var options = new
            {
                url = string.IsNullOrWhiteSpace(MrcStackPath) ? null : FileService.GetUrl(MrcStackPath),
                signed = SignedContrast,
                items = (ImageNumbers ?? []).Select((number, i) => new
                {
                    number, id = ClassIds?[i] ?? number,
                    label = ClassLabels?[i] ?? (ClassIds != null ? $"#{ClassIds[i]}" : null),
                    resolution = ClassResolutions?[i], size = ClassSizes?[i]
                }).ToArray(),
                subclasses = Subclasses.Select(g => new
                {
                    parentId = g.ParentId, url = g.Classes.Length == 0 ? null : FileService.GetUrl(g.MrcStackPath),
                    items = g.Classes, reason = g.Reason
                }).ToArray(),
                imageSize = RenderedImageSidelengthPx, spacing = SpacingPx,
                borderWidth = BorderWidthPx, borderColor = BorderColorUnselected, selectedColor = BorderColorSelected,
                allowSorting = AllowSorting, mutable = SelectionIsMutable,
                selectionVersion = _selectionVersion
            };
            var key = JsonSerializer.Serialize(options);
            if (_lastOptions == key) return;
            await module.InvokeVoidAsync("update", _root, options, _callback, _selectedClasses.ToArray());
            _lastOptions = key;
        }
        catch (JSDisconnectedException) { }
        catch (JSException error)
        {
            _interopError = error.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_moduleTask != null)
            {
                var module = await _moduleTask;
                await module.InvokeVoidAsync("dispose", _id);
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
        finally { _callback?.Dispose(); }
    }
}

/// <summary>Subclass IDs refer to the combined export stack; Numbers index their parent's stack.</summary>
public sealed record Class2DImageMetadata(int Number, int Id, string Label, float? Resolution, float? Size);
public sealed record Class2DSubclassGroup(int ParentId, string MrcStackPath, Class2DImageMetadata[] Classes, string Reason = null);
