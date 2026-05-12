using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.View;

public partial class FolderCard : ComponentBase, IDisposable
{
    [Parameter, EditorRequired]
    public ReadOnlyFolder Folder { get; set; }

    [Parameter]
    public bool DiagramMode { get; set; }

    [Parameter]
    public double DiagramWidth { get; set; }

    [Parameter]
    public double DiagramHeight { get; set; }

    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }

    [Parameter]
    public EventCallback<MouseEventArgs> OnDoubleClick { get; set; }

    [Parameter]
    public EventCallback<CardContextMenuArgs> OnDiagramContextMenu { get; set; }

    [Inject]
    private CardSelectionService Selection { get; set; }

    [Inject]
    private MenuActionService MenuActions { get; set; }

    [Inject]
    private ViewDragDropService DragDrop { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    [Inject]
    private IToastService ToastService { get; set; }

    private List<MenuAction> _contextMenuActions;
    private bool _isDragOver;
    private ReadOnlyFolder _folder;
    private readonly List<GroupEventSubscription> _subscriptions = new();

    private SelectionKey SelectionKey => SelectionKey.ForFolder(Folder.Id);

    private string ItemCountText
    {
        get
        {
            var count = Folder.Items.Count;
            return count switch
            {
                0 => "Empty",
                1 => "1 item",
                _ => $"{count} items"
            };
        }
    }

    protected override void OnParametersSet()
    {
        if (Folder != _folder)
        {
            _folder = Folder;
            _subscriptions.UnsubscribeAndClear();

            if (_folder?.View?.Space != null)
            {
                var projectId = _folder.View.Space.Project.Id;
                var spaceId = _folder.View.Space.Id;
                var viewId = _folder.View.Id;

                _subscriptions.Add(DataManager.ViewUpdated.Add(
                    GroupName.View(projectId, spaceId, viewId),
                    async _ => await InvokeAsync(StateHasChanged)));

                _subscriptions.Add(DataManager.EdgeCreated.Add(
                    GroupName.Edge(projectId, spaceId, null),
                    async _ => await InvokeAsync(StateHasChanged)));

                _subscriptions.Add(DataManager.EdgeDeleted.Add(
                    GroupName.Edge(projectId, spaceId, null),
                    async _ => await InvokeAsync(StateHasChanged)));

                _subscriptions.Add(DataManager.JobUpdated.Add(
                    GroupName.Job(projectId, spaceId, null),
                    async _ => await InvokeAsync(StateHasChanged)));

                _subscriptions.Add(DataManager.JobDeleted.Add(
                    GroupName.Job(projectId, spaceId, null),
                    async _ => await InvokeAsync(StateHasChanged)));
            }
        }
    }

    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }

    private async Task HandleClick(MouseEventArgs args)
    {
        await OnClick.InvokeAsync(args);
    }

    private async Task HandleDoubleClick(MouseEventArgs args)
    {
        await OnDoubleClick.InvokeAsync(args);
    }

    private async Task HandleContextMenu(bool value)
    {
        if (value)
        {
            await Selection.Replace([SelectionKey]);
            _contextMenuActions = MenuActions.GetFolderActions(Folder);

            if (OnClick.HasDelegate)
            {
                await OnClick.InvokeAsync(new MouseEventArgs
                {
                    Button = 2,
                    Type = "contextmenu",
                });
            }
        }
        else
        {
            _contextMenuActions = null;
        }
    }

    private async Task HandleRightClick(MouseEventArgs args)
    {
        if (!DiagramMode) return; // FluentMenu handles it in list mode

        await Selection.Replace([SelectionKey]);
        var actions = MenuActions.GetFolderActions(Folder);

        await OnDiagramContextMenu.InvokeAsync(new CardContextMenuArgs
        {
            MouseEventArgs = args,
            Header = Folder.Alias,
            Actions = actions
        });
    }

    private void HandleDragStart(DragEventArgs args)
    {
        if (DiagramMode) return;
        DragDrop.StartDrag([Folder]);
    }

    private void HandleDragEnd(DragEventArgs args)
    {
        if (DiagramMode) return;
        DragDrop.EndDrag();
        _isDragOver = false;
    }

    private void HandleDragOver(DragEventArgs args)
    {
        if (DiagramMode) return;
        if (DragDrop.IsDragging && !DragDrop.DraggedItems.Contains(Folder))
            _isDragOver = true;
    }

    private void HandleDragLeave(DragEventArgs args)
    {
        if (DiagramMode) return;
        _isDragOver = false;
    }

    private async Task HandleDrop(DragEventArgs args)
    {
        if (DiagramMode) return;
        _isDragOver = false;
        if (!DragDrop.IsDragging || DragDrop.DraggedItems.Contains(Folder))
            return;

        var draggedItems = DragDrop.DraggedItems.ToList();
        DragDrop.EndDrag();

        try
        {
            foreach (var item in draggedItems)
            {
                if (item is ReadOnlyJob job)
                    await DataManager.MoveJobToFolder(Session.User, Session.View, job, Folder);
                else if (item is ReadOnlyFolder folder)
                    await DataManager.MoveFolderToFolder(Session.User, Session.View, folder, Folder);
            }
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't move item: {exc.Message}");
        }
    }

    // Card inner width: total width minus borders (6px left + 1px right) and padding (8px each side)
    private static readonly double MinimapMaxWidth = VisualProvider.GetWidth(1) - 23;
    // Card inner height minus borders (1+1), padding (10+8), icon (~20), title 2-line (~34), count (~15), gaps (3×2)
    private static readonly double MinimapMaxHeight = VisualProvider.GetHeight(1) - 20 - 75;

    private (double Width, double Height) GetMinimapSize(FolderLayout layout)
    {
        double scale = Math.Min(MinimapMaxWidth / layout.GraphWidth, MinimapMaxHeight / layout.GraphHeight);
        if (scale > 1) scale = 1; // don't upscale
        return (layout.GraphWidth * scale, layout.GraphHeight * scale);
    }

    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private string GetNodeColor(FolderLayoutNode node)
    {
        var job = Folder.Items.OfType<ReadOnlyJob>().FirstOrDefault(j => j.Id == node.ItemId);
        return job != null ? Refund.Utils.JobStatusExtensions.GetStatusHexColor(job.Status) : "#888";
    }

    private static string GetEdgePath(FolderLayoutEdge edge)
    {
        var pts = new List<(double X, double Y)> { (edge.SourceX, edge.SourceY) };
        if (edge.BendPoints != null)
            foreach (var bp in edge.BendPoints)
                pts.Add(bp);
        pts.Add((edge.TargetX, edge.TargetY));

        var sb = new StringBuilder();
        sb.Append($"M {F(pts[0].X)},{F(pts[0].Y)}");

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p1 = pts[i];
            var p2 = pts[i + 1];
            double tangent = (p2.X - p1.X) * 0.75;

            sb.Append($" C {F(p1.X + tangent)},{F(p1.Y)}" +
                      $" {F(p2.X - tangent)},{F(p2.Y)}" +
                      $" {F(p2.X)},{F(p2.Y)}");
        }

        return sb.ToString();
    }
}
