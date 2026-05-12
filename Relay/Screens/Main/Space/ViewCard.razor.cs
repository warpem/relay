using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Space;

public partial class ViewCard : ListingCardLogic<ReadOnlyView>
{
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

    protected override void SubscribeToEvents()
    {
        _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(Item.Space.Project.Id, Item.Space.Id, Item.Id),
                                                       async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Item.Space.Project.Id, Item.Space.Id, Item.Id),
                                                       async _ => Dispose()));
            
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Item.Space.Project.Id, Item.Space.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(Item.Space.Project.Id, Item.Space.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Item.Space.Project.Id, Item.Space.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));
    }
}