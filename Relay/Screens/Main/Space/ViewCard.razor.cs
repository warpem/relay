using Microsoft.AspNetCore.Components.Web;
using System.Globalization;
using System.Text;
using Refund.DataModel;
using Refund.Components.Jobs;
using Refund.Utils;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Space;

public partial class ViewCard : ListingCardLogic<ReadOnlyView>
{
    private readonly HoverPreview<ReadOnlyJob> _preview;
    private ReadOnlyView _previewView;

    public ViewCard()
    {
        _preview = new(() => InvokeAsync(StateHasChanged), job => Item.Jobs.Contains(job));
    }

    private string GetNodeId(ReadOnlyJob job)
        => $"view-node-p{Item.Space.Project.Id}-s{Item.Space.Id}-v{Item.Id}-j{job.Id}";

    private string GetJobUrl(ReadOnlyJob job)
        => RelaySession.BuildUrl(JobNavigation.ForJob(job, Item));

    private async Task OpenJob(ReadOnlyJob job, MouseEventArgs args)
    {
        if (!MouseUtils.IsPlainLeftClick(args))
            return;

        _preview.Reset();
        await Session.NavigateToAsync(JobNavigation.ForJob(job, Item));
    }

    private static (double Width, double Height, double Scale) GetGraphSize(FolderLayout layout)
    {
        double width = layout.GraphWidth + 16;
        double height = layout.GraphHeight + 16;
        double scale = Math.Min(1, Math.Min(464 / width, 192 / height));
        return (width * scale, height * scale, scale);
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string GetEdgePath(FolderLayoutEdge edge)
    {
        var points = new List<(double X, double Y)> { (edge.SourceX, edge.SourceY) };
        if (edge.BendPoints != null)
            points.AddRange(edge.BendPoints);
        points.Add((edge.TargetX, edge.TargetY));

        var path = new StringBuilder($"M {F(points[0].X)},{F(points[0].Y)}");
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            double tangent = (p2.X - p1.X) * 0.75;
            path.Append($" C {F(p1.X + tangent)},{F(p1.Y)} {F(p2.X - tangent)},{F(p2.Y)} {F(p2.X)},{F(p2.Y)}");
        }
        return path.ToString();
    }

    private void HandleNodeKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
            _preview.Reset();
    }

    private static int StatusOrder(JobStatus status) => status switch
    {
        JobStatus.Running or JobStatus.Finalizing => 0,
        JobStatus.Failed or JobStatus.Interrupted or JobStatus.Aborted => 1,
        JobStatus.Waiting or JobStatus.Staging => 2,
        JobStatus.Finished => 3,
        _ => 4
    };

    protected override void OnParametersSet()
    {
        if (_previewView != Item || (_preview.Current != null && !Item.Jobs.Contains(_preview.Current)))
            _preview.Reset();
        _previewView = Item;
        base.OnParametersSet();
    }

    public override void Dispose()
    {
        _preview.Dispose();
        base.Dispose();
    }

    protected override string GetNavigationUrl()
        => RelaySession.BuildUrl(new() { ProjectId = Item.Space.Project.Id, SpaceId = Item.Space.Id, ViewId = Item.Id });

    protected override Task HeaderClick(MouseEventArgs args)
    {
        return Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Item.Space.Project.Id,
            SpaceId = Item.Space.Id,
            ViewId = Item.Id
        });
    }

    private async Task<List<MenuAction>> GetContextMenuActions()
    {
        return MenuActions.GetViewActions([Item]);
    }

    private Task RefreshCard() => InvokeAsync(() =>
    {
        if (_preview.Current != null && !Item.Jobs.Contains(_preview.Current))
            _preview.Reset();
        StateHasChanged();
    });

    protected override void SubscribeToEvents()
    {
        _subscriptions.Add(DataManager.EdgeCreated.Add(GroupName.Edge(Item.Space.Project.Id, Item.Space.Id, null),
                                                       _ => RefreshCard()));
        _subscriptions.Add(DataManager.EdgeDeleted.Add(GroupName.Edge(Item.Space.Project.Id, Item.Space.Id, null),
                                                       _ => RefreshCard()));
        _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(Item.Space.Project.Id, Item.Space.Id, Item.Id),
                                                       _ => RefreshCard()));
            
        _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Item.Space.Project.Id, Item.Space.Id, Item.Id),
                                                       async _ => Dispose()));
            
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Item.Space.Project.Id, Item.Space.Id, null),
                                                      _ => RefreshCard()));
            
        _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(Item.Space.Project.Id, Item.Space.Id, null),
                                                      _ => RefreshCard()));
            
        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Item.Space.Project.Id, Item.Space.Id, null),
                                                      _ => RefreshCard()));
    }
}
