using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Screens.Overlay.Queues;

public partial class OverlayQueues : ComponentBase
{
    [Inject] public DataManager DataManager { get; set; }
    [Inject] public RelaySession Session { get; set; }
    [Inject] public MenuActionService MenuActions { get; set; }

    private readonly List<GroupEventSubscription> _subscriptions = new();

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // re-render when queue updates
        _subscriptions.Add(DataManager.QueueUpdated.Add(GroupName.Queue(null),
            async _ => await InvokeAsync(StateHasChanged)));
    }
    
    private async Task HandleJobCreated(ReadOnlyJob job) => await InvokeAsync(StateHasChanged);
    private async Task HandleJobUpdated(ReadOnlyJob job) => await InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        foreach(var subscription in _subscriptions)
        {
            subscription.Unsubscribe();
        }
    }
}