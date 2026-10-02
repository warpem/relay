using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;

namespace Relay.Screens.Main.Home;

/// <summary>Keeps an activity card's location current without rerendering its expensive job preview.</summary>
public partial class ActivityBreadcrumb : IDisposable
{
    [Parameter, EditorRequired] public ReadOnlyJob Job { get; set; }
    [Inject] private DataManager DataManager { get; set; }

    private ReadOnlyJob _job;
    private bool _disposed;
    private readonly List<GroupEventSubscription> _subscriptions = new();

    protected override void OnParametersSet()
    {
        if (_job == Job)
            return;

        _subscriptions.UnsubscribeAndClear();
        _job = Job;
        var space = Job.Space;
        var projectGroup = GroupName.Project(space.Project.Id);
        var spaceGroup = GroupName.Space(space.Project.Id, space.Id);
        var viewsGroup = GroupName.View(space.Project.Id, space.Id, null);
        _subscriptions.Add(DataManager.ProjectUpdated.Add(projectGroup, _ => Refresh()));
        _subscriptions.Add(DataManager.SpaceUpdated.Add(spaceGroup, _ => Refresh()));
        _subscriptions.Add(DataManager.ViewCreated.Add(viewsGroup, _ => Refresh()));
        _subscriptions.Add(DataManager.ViewUpdated.Add(viewsGroup, _ => Refresh()));
        _subscriptions.Add(DataManager.ViewDeleted.Add(viewsGroup, _ => Refresh()));
    }

    private Task Refresh() => InvokeAsync(() =>
    {
        if (!_disposed)
            StateHasChanged();
    });

    public void Dispose()
    {
        _disposed = true;
        _subscriptions.UnsubscribeAndClear();
    }
}
