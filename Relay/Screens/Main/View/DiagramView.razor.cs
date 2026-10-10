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
            catch (JSException) { /* Component may have been disposed */ }
        }
        else if (_jsInitialized && _needsZoomToFit && Layout is { Nodes.Count: > 0 })
        {
            _needsZoomToFit = false;
            try
            {
                await JSRuntime.InvokeVoidAsync("diagramInterop.zoomToFit",
                    Layout.GraphWidth, Layout.GraphHeight);
            }
            catch (JSException) { }
        }
    }

    private async void HandleZoomToFit()
    {
        if (Layout == null) return;
        try
        {
            await JSRuntime.InvokeVoidAsync("diagramInterop.zoomToFit",
                Layout.GraphWidth, Layout.GraphHeight);
        }
        catch (JSException) { }
    }

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

    // Build SVG bezier path from polyline bend points
    private string GetEdgePath(DiagramLayoutEdge edge)
    {
        var pts = new List<(double X, double Y)> { (edge.SourceX, edge.SourceY) };
        if (edge.BendPoints != null)
            foreach (var bp in edge.BendPoints)
                pts.Add(bp);
        pts.Add((edge.TargetX, edge.TargetY));

        // The local annotation detours contain vertical segments. Round those
        // routes with tangent quadratic joins instead of joining independent
        // horizontal-tangent Beziers at right angles. Keep ordinary flowing
        // dependency curves unchanged.
        if (pts.Zip(pts.Skip(1)).Any(pair =>
                Math.Abs(pair.First.X - pair.Second.X) < 0.01 &&
                Math.Abs(pair.First.Y - pair.Second.Y) > 0.01))
            return GetRoundedPath(pts);

        var sb = new System.Text.StringBuilder();
        sb.Append($"M {F(pts[0].X)},{F(pts[0].Y)}");
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p1 = pts[i];
            var p2 = pts[i + 1];
            double tangent = (p2.X - p1.X) * 0.75;
            sb.Append($" C {F(p1.X + tangent)},{F(p1.Y)} {F(p2.X - tangent)},{F(p2.Y)} {F(p2.X)},{F(p2.Y)}");
        }
        return sb.ToString();
    }

    private static string GetRoundedPath(List<(double X, double Y)> points)
    {
        // Trimming each adjacent segment by at most half its length prevents
        // rounding from overshooting the reserved routing corridor.
        var path = new System.Text.StringBuilder($"M {F(points[0].X)},{F(points[0].Y)}");
        for (int i = 1; i < points.Count - 1; i++)
        {
            var previous = points[i - 1];
            var corner = points[i];
            var next = points[i + 1];
            double incoming = Math.Sqrt(Math.Pow(corner.X - previous.X, 2) + Math.Pow(corner.Y - previous.Y, 2));
            double outgoing = Math.Sqrt(Math.Pow(next.X - corner.X, 2) + Math.Pow(next.Y - corner.Y, 2));
            if (incoming < 0.01 || outgoing < 0.01) continue;
            double radius = Math.Min(16, Math.Min(incoming, outgoing) / 2);
            double entryX = corner.X + (previous.X - corner.X) * radius / incoming;
            double entryY = corner.Y + (previous.Y - corner.Y) * radius / incoming;
            double exitX = corner.X + (next.X - corner.X) * radius / outgoing;
            double exitY = corner.Y + (next.Y - corner.Y) * radius / outgoing;
            path.Append($" L {F(entryX)},{F(entryY)} Q {F(corner.X)},{F(corner.Y)} {F(exitX)},{F(exitY)}");
        }
        path.Append($" L {F(points[^1].X)},{F(points[^1].Y)}");
        return path.ToString();
    }

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
        catch (JSException) { }
    }
}
