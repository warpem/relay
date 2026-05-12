using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Panels.Right;

public partial class FactoryInstanceProperties : ComponentBase, IDisposable
{
    [Parameter]
    public ReadOnlyFactoryInstance FactoryInstance { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    [Inject]
    private IToastService ToastService { get; set; }

    private List<GroupEventSubscription> _subscriptions = new();
    private ReadOnlyFactoryInstance _fi;

    protected override void OnParametersSet()
    {
        if (FactoryInstance != _fi)
        {
            _fi = FactoryInstance;
            _subscriptions.UnsubscribeAndClear();

            if (_fi != null && Session.Project != null && Session.Space != null)
            {
                _subscriptions.Add(DataManager.FactoryInstanceUpdated.Add(
                    GroupName.FactoryInstance(Session.Project.Id, Session.Space.Id, _fi.Id),
                    async _ => await InvokeAsync(StateHasChanged)));

                // Sub-job status changes affect aggregate status
                _subscriptions.Add(DataManager.JobUpdated.Add(
                    GroupName.Job(Session.Project.Id, Session.Space.Id, null),
                    async _ => await InvokeAsync(StateHasChanged)));
            }
        }
    }

    private async Task HandleAliasChanged(string alias)
    {
        if (FactoryInstance == null || alias == FactoryInstance.Alias) return;
        try
        {
            await DataManager.UpdateFactoryInstance(Session.User, FactoryInstance,
                fi => fi.Alias = alias);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update name: {exc.Message}");
        }
    }

    private async Task HandleNotesChanged(string notes)
    {
        if (FactoryInstance == null || notes == FactoryInstance.Notes) return;
        try
        {
            await DataManager.UpdateFactoryInstance(Session.User, FactoryInstance,
                fi => fi.Notes = notes);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update notes: {exc.Message}");
        }
    }

    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }
}
