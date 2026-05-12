using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Home;

public partial class ProjectCard : ListingCardLogic<ReadOnlyProject>
{
    protected override string GetNavigationUrl()
        => RelaySession.BuildUrl(new() { ProjectId = Item.Id });

    protected override Task HeaderClick(MouseEventArgs args)
    {
        return base.Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Item.Id
        });
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

        // Subscribe to job events to update activity calendar
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Item.Id, null, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(Item.Id, null, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Item.Id, null, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));
    }
}