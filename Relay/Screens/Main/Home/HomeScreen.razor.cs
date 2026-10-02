using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Home;

public partial class HomeScreen : ListingScreenLogic<ReadOnlyProject>
{
    protected override SelectionKey GetSelectionKey(ReadOnlyProject item) => SelectionKey.ForProject(item.Id);

    protected override string GetTitle() => "Projects";
    protected override string GetCreateButtonText() => "Create new project";
    
    protected override IEnumerable<ReadOnlyProject> GetItems() =>
        DataManager.GetUserProjects(Session.User).NewestFirst();

    protected override async Task HandleItemClicked(ReadOnlyProject item, MouseEventArgs args)
    {
        // Activity job cards share this screen's selection; don't mix projects into a job selection
        if (args.Button == 0 && args.Type != "contextmenu" &&
            (MouseUtils.ModifierSelectSingle(args, Session.ClientOs) || MouseUtils.ModifierSelectRange(args, Session.ClientOs)))
            await Selection.RemoveRange(Selection.SelectedItems.Where(k => k.Type != ItemType.Project));

        await base.HandleItemClicked(item, args);
    }

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

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // Activity job cards change the selection without going through this screen's handlers
        Selection.OnSelectionChanged += HandleSelectionChanged;
    }

    private Task HandleSelectionChanged() => InvokeAsync(StateHasChanged);

    public override void Dispose()
    {
        Selection.OnSelectionChanged -= HandleSelectionChanged;
        base.Dispose();
    }

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();
        
        _subscriptions.Add(DataManager.ProjectCreated.Add(GroupName.Project(null), 
                                                          async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(null), 
                                                          async _ => await InvokeAsync(StateHasChanged)));
    }
}