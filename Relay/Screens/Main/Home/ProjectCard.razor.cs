using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Home;

public partial class ProjectCard : ListingCardLogic<ReadOnlyProject>
{
    /// <summary>
    /// Seven 28px shortcuts and the 20px "+N more" line fit the shared 312px card.
    /// </summary>
    private const int MaxSpaceRows = 7;

    protected override string GetNavigationUrl()
        => RelaySession.BuildUrl(new() { ProjectId = Item.Id });

    protected override Task HeaderClick(MouseEventArgs args)
    {
        return base.Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Item.Id
        });
    }

    private string GetSpaceUrl(ReadOnlySpace space)
        => RelaySession.BuildUrl(new() { ProjectId = Item.Id, SpaceId = space.Id });

    /// <summary>
    /// Space rows are shortcuts: plain clicks navigate, modified/middle clicks keep native link behavior,
    /// and neither selects the project card.
    /// </summary>
    private async Task HandleSpaceClick(ReadOnlySpace space, MouseEventArgs args)
    {
        if (!MouseUtils.IsPlainLeftClick(args))
            return;
        await Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Item.Id,
            SpaceId = space.Id
        });
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string GetUserName(ReadOnlyUser user)
        => string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name;

    private string GetMembersSummary()
    {
        var owner = Item.Owner;
        int others = Item.Members.Count(m => m.Id != owner.Id);
        return others == 0 ? GetUserName(owner) : $"{GetUserName(owner)} + {Plural(others, "member")}";
    }

    private string GetMembersTooltip()
    {
        var owner = Item.Owner;
        var names = Item.Members.Where(m => m.Id != owner.Id).Select(GetUserName);
        return string.Join(", ", names.Prepend($"{GetUserName(owner)} (owner)"));
    }

    private async Task<List<MenuAction>> GetContextMenuActions()
    {
        return MenuActions.GetProjectActions([Item]);
    }

    protected override void SubscribeToEvents()
    {
        // Project updates/deletion
        _subscriptions.Add(DataManager.ProjectUpdated.Add(GroupName.Project(Item.Id),
                                                          async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(Item.Id),
                                                          async _ => Dispose()));

        // Space events update the shortcut rows
        _subscriptions.Add(DataManager.SpaceCreated.Add(GroupName.Space(Item.Id, null),
                                                        async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.SpaceUpdated.Add(GroupName.Space(Item.Id, null),
                                                        async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.SpaceDeleted.Add(GroupName.Space(Item.Id, null),
                                                        async _ => await InvokeAsync(StateHasChanged)));

        // View events update view counts
        _subscriptions.Add(DataManager.ViewCreated.Add(GroupName.View(Item.Id, null, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(Item.Id, null, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Item.Id, null, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        // Job creation/deletion updates job counts
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Item.Id, null, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Item.Id, null, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));
    }
}
