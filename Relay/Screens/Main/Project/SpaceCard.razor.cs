using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Project;

public partial class SpaceCard : ListingCardLogic<ReadOnlySpace>
{
    /// <summary>
    /// Most recent jobs listed in the card body.
    /// </summary>
    private const int MaxJobRows = 4;

    /// <summary>
    /// View shortcuts shown in the footer before collapsing the rest into "+N more".
    /// </summary>
    private const int MaxViewShortcuts = 6;

    protected override string GetNavigationUrl()
        => RelaySession.BuildUrl(new() { ProjectId = Item.Project.Id, SpaceId = Item.Id });

    protected override Task HeaderClick(MouseEventArgs args)
    {
        return Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Item.Project.Id,
            SpaceId = Item.Id
        });
    }

    private string GetViewUrl(ReadOnlyView view)
        => RelaySession.BuildUrl(new() { ProjectId = Item.Project.Id, SpaceId = Item.Id, ViewId = view.Id });

    /// <summary>
    /// View shortcuts navigate on plain clicks, keep native link behavior for modified/middle clicks,
    /// and never select the space card.
    /// </summary>
    private async Task HandleViewClick(ReadOnlyView view, MouseEventArgs args)
    {
        if (!MouseUtils.IsPlainLeftClick(args))
            return;
        await Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Item.Project.Id,
            SpaceId = Item.Id,
            ViewId = view.Id
        });
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private async Task<List<MenuAction>> GetContextMenuActions()
    {
        return MenuActions.GetSpaceActions([Item]);
    }

    protected override void SubscribeToEvents()
    {
        // Space updates/deletion
        _subscriptions.Add(DataManager.SpaceUpdated.Add(GroupName.Space(Item.Project.Id, Item.Id),
                                                        async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.SpaceDeleted.Add(GroupName.Space(Item.Project.Id, Item.Id),
                                                        async _ => Dispose()));

        // Job events
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Item.Project.Id, Item.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(Item.Project.Id, Item.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Item.Project.Id, Item.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        // View events update the view count and shortcuts
        _subscriptions.Add(DataManager.ViewCreated.Add(GroupName.View(Item.Project.Id, Item.Id, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(Item.Project.Id, Item.Id, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Item.Project.Id, Item.Id, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));
    }
}
