using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Project;

public partial class SpaceCard : ListingCardLogic<ReadOnlySpace>
{
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
    }
}