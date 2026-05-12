using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Home;

public partial class HomeScreen : ListingScreenLogic<ReadOnlyProject>
{
    protected override SelectionKey GetSelectionKey(ReadOnlyProject item) => SelectionKey.ForProject(item.Id);

    protected override string GetTitle() => "Projects";
    protected override string GetCreateButtonText() => "Create new project";
    
    protected override IEnumerable<ReadOnlyProject> GetItems() => DataManager.GetUserProjects(Session.User);

    protected override Task ShowCreateDialogAsync() => CreateProjectDialog.Show(DialogService, this, OnCreateDialogClosedAsync);

    protected override async Task OnCreateDialogClosedAsync(DialogResult result)
    {
        if (result.Data is CreateProjectDialogResult { Success: true } createResult)
        {
            await Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = createResult.ProjectId
            });
        }
    }

    protected override Task NavigateToItemAsync(ReadOnlyProject item) =>
        Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = item.Id
        });

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();
        
        _subscriptions.Add(DataManager.ProjectCreated.Add(GroupName.Project(null), 
                                                          async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(null), 
                                                          async _ => await InvokeAsync(StateHasChanged)));
    }
}