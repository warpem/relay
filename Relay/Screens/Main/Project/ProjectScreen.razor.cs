using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Project;

public partial class ProjectScreen : ListingScreenLogic<ReadOnlySpace>
{
    protected override SelectionKey GetSelectionKey(ReadOnlySpace item) => SelectionKey.ForSpace(item.Id);

    /// <summary>
    /// Project the current subscriptions are bound to.
    /// </summary>
    private int? _subscribedProjectId;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Session.OnProjectChanged += HandleProjectChanged;
    }

    private async Task HandleProjectChanged()
    {
        await InvokeAsync(() =>
        {
            // The same screen instance is reused when navigating between projects
            if (_subscribedProjectId != Session.Project?.Id)
            {
                SubscribeToEvents();
                _lastSelectedKey = null;
                _lastContextMenuTime = null;
            }

            StateHasChanged();
        });
    }

    public override void Dispose()
    {
        base.Dispose();
        Session.OnProjectChanged -= HandleProjectChanged;
    }

    protected override string GetTitle() => "Spaces";
    protected override string GetCreateButtonText() => "Create or reconnect space";

    protected override IEnumerable<ReadOnlySpace> GetItems() =>
        Session.Project?.Spaces.OldestFirst() ?? Enumerable.Empty<ReadOnlySpace>();

    protected override Task ShowCreateDialogAsync() => CreateSpaceDialog.Show(DialogService, this, OnCreateDialogClosedAsync);

    protected override async Task OnCreateDialogClosedAsync(DialogResult result)
    {
        if (result.Data is CreateSpaceDialogResult { Success: true } createResult)
        {
            await Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = createResult.SpaceId
            });
        }
    }

    protected override Task NavigateToItemAsync(ReadOnlySpace space) =>
        Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Session.Project.Id,
            SpaceId = space.Id
        });

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();

        _subscribedProjectId = Session.Project?.Id;

        if (Session.Project == null)
            return;

        // Project updates/deletion
        _subscriptions.Add(DataManager.ProjectUpdated.Add(GroupName.Project(Session.Project.Id),
                                                          async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(Session.Project.Id),
                                                          async _ => await Session.NavigateToAsync(new())));
            
        // Space events
        _subscriptions.Add(DataManager.SpaceCreated.Add(GroupName.Space(Session.Project.Id, null),
                                                        async _ => await InvokeAsync(StateHasChanged)));
            
        _subscriptions.Add(DataManager.SpaceDeleted.Add(GroupName.Space(Session.Project.Id, null),
                                                        async _ => await InvokeAsync(StateHasChanged)));
    }
}