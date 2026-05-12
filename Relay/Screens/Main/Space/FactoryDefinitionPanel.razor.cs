using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.Space;

public partial class FactoryDefinitionPanel : ComponentBase, IDisposable
{
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private RelaySession Session { get; set; }
    [Inject] private IToastService ToastService { get; set; }

    private bool _isCollapsed;
    private readonly List<GroupEventSubscription> _subscriptions = new();

    private IEnumerable<ReadOnlyFactoryDefinition> Definitions =>
        Session.Space?.FactoryDefinitions ?? Enumerable.Empty<ReadOnlyFactoryDefinition>();

    protected override void OnInitialized()
    {
        Session.OnSpaceChanged += HandleSpaceChanged;
        SubscribeToEvents();
    }

    private void SubscribeToEvents()
    {
        _subscriptions.UnsubscribeAndClear();
        if (Session.Project != null && Session.Space != null)
        {
            _subscriptions.Add(DataManager.FactoryDefinitionCreated.Add(
                GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, null),
                async _ => await InvokeAsync(StateHasChanged)));
            _subscriptions.Add(DataManager.FactoryDefinitionUpdated.Add(
                GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, null),
                async _ => await InvokeAsync(StateHasChanged)));
            _subscriptions.Add(DataManager.FactoryDefinitionDeleted.Add(
                GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, null),
                async _ => await InvokeAsync(StateHasChanged)));
        }
    }

    private async Task HandleSpaceChanged()
    {
        SubscribeToEvents();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleDefinitionDoubleClick(ReadOnlyFactoryDefinition def)
    {
        await Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Session.Project.Id,
            SpaceId = Session.Space.Id,
            FactoryDefinitionId = def.Id
        });
    }

    private async Task HandleCreateFactory()
    {
        try
        {
            var def = await DataManager.CreateFactoryDefinition(Session.User, Session.Space);
            await Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = Session.Space.Id,
                FactoryDefinitionId = def.Id
            });
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create factory: " + exc.Message);
        }
    }

    public void Dispose()
    {
        Session.OnSpaceChanged -= HandleSpaceChanged;
        _subscriptions.UnsubscribeAndClear();
    }
}
