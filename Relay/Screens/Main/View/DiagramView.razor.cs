using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.View;

public partial class DiagramView : ComponentBase, IAsyncDisposable
{
    [Inject] private RelaySession Session { get; set; }
    [Inject] private IJSRuntime JSRuntime { get; set; }
    [Inject] private DiagramViewService DiagramService { get; set; }

    [Parameter] public DiagramLayout? Layout { get; set; }
    [Parameter] public IEnumerable<IViewItem> Items { get; set; }

    // Delegate events up to ViewScreen
    [Parameter] public EventCallback<(IViewItem Item, MouseEventArgs Args)> OnItemClick { get; set; }
    [Parameter] public EventCallback<(IViewItem Item, MouseEventArgs Args)> OnItemDoubleClick { get; set; }
    [Parameter] public EventCallback<PortClickArgs> OnPortClick { get; set; }
    [Parameter] public EventCallback<MouseEventArgs> OnContextMenu { get; set; }
    [Parameter] public EventCallback OnBackgroundClick { get; set; }
    [Parameter] public EventCallback<CardContextMenuArgs> OnCardContextMenu { get; set; }

    private ElementReference _viewport;
    private ElementReference _canvas;
    private bool _jsInitialized;
    private DiagramLayout? _prevLayout;
    private bool _needsZoomToFit;

    protected override void OnParametersSet()
    {
        if (Layout != _prevLayout)
        {
            _prevLayout = Layout;
            if (_jsInitialized)
                _needsZoomToFit = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_jsInitialized && Layout is { Nodes.Count: > 0 })
        {
            try
            {
                await JSRuntime.InvokeVoidAsync("diagramInterop.initialize", _viewport, _canvas);
                await JSRuntime.InvokeVoidAsync("diagramInterop.zoomToFit",
                    Layout.GraphWidth, Layout.GraphHeight);
                _jsInitialized = true;
                _needsZoomToFit = false;
                DiagramService.OnZoomChanged += HandleZoomToFit;
            }
            catch (Exception e) when (IsExpectedJsFailure(e)) { /* Component may have been disposed */ }
        }
        else if (_jsInitialized && _needsZoomToFit && Layout is { Nodes.Count: > 0 })
        {
            _needsZoomToFit = false;
            try
            {
                await JSRuntime.InvokeVoidAsync("diagramInterop.zoomToFit",
                    Layout.GraphWidth, Layout.GraphHeight);
            }
            catch (Exception e) when (IsExpectedJsFailure(e)) { }
        }
    }

    /// <summary>
    /// async void (OnZoomChanged is a plain event), so nothing observes its exceptions and any that escaped would
    /// terminate the circuit. Zooming is best-effort; swallow everything.
    /// </summary>
    private async void HandleZoomToFit()
    {
        try
        {
            var layout = Layout;
            if (layout == null) return;
            await JSRuntime.InvokeVoidAsync("diagramInterop.zoomToFit", layout.GraphWidth, layout.GraphHeight);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// JS interop failures to expect while the diagram or the circuit is going away: script errors, a dropped
    /// connection, the JS call timeout, or a disposed runtime.
    /// </summary>
    private static bool IsExpectedJsFailure(Exception e) =>
        e is JSException or JSDisconnectedException or OperationCanceledException or ObjectDisposedException;

    // Find the layout node for a given view item
    private DiagramLayoutNode? GetNode(IViewItem item)
    {
        if (Layout == null) return null;
        int id;
        bool isFolder;
        bool isFactoryInstance;
        if (item is ReadOnlyJob j) { id = j.Id; isFolder = false; isFactoryInstance = false; }
        else if (item is ReadOnlyFolder f) { id = f.Id; isFolder = true; isFactoryInstance = false; }
        else if (item is ReadOnlyFactoryInstance fi) { id = fi.Id; isFolder = false; isFactoryInstance = true; }
        else return null;

        foreach (var n in Layout.Nodes)
            if (n.ItemId == id && n.IsFolder == isFolder && n.IsFactoryInstance == isFactoryInstance)
                return n;
        return null;
    }

    private static string GetEdgePath(DiagramLayoutEdge edge) => DiagramEdgePath.Build(edge);

    private async Task HandleContextMenu(MouseEventArgs args)
    {
        // Only fire if the click target is the viewport/canvas background, not a card
        // Card context menus already stopPropagation, so this only fires on empty space
        await OnContextMenu.InvokeAsync(args);
    }

    private async Task HandleBackgroundClick(MouseEventArgs args)
    {
        await OnBackgroundClick.InvokeAsync();
    }

    private string GetJobUrl(ReadOnlyJob job) => RelaySession.BuildUrl(JobNavigation.ForJob(job, Session.View));

    private string GetExternalPath(DiagramExternalConnection connection)
    {
        double x = connection.IsOutput ? connection.X : connection.X + connection.Width;
        double y = connection.Y + connection.Height / 2;
        double midX = (connection.PortX + x) / 2;
        return $"M {F(connection.PortX)},{F(connection.PortY)} C {F(midX)},{F(connection.PortY)} {F(midX)},{F(y)} {F(x)},{F(y)}";
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        DiagramService.OnZoomChanged -= HandleZoomToFit;
        try
        {
            await JSRuntime.InvokeVoidAsync("diagramInterop.dispose");
        }
        catch (Exception e) when (IsExpectedJsFailure(e)) { }
    }
}
