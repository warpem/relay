using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Panels.Right;

public partial class FactoryDefinitionProperties : ComponentBase, IDisposable
{
    [Parameter]
    public IEnumerable<ReadOnlyFactoryDefinition> Definitions { get; set; }

    [Parameter]
    public bool IsLocked { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    [Inject]
    private IToastService ToastService { get; set; }

    private List<GroupEventSubscription> _subscriptions = new();
    private IEnumerable<ReadOnlyFactoryDefinition> _defs;

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(Definitions, _defs))
        {
            _defs = Definitions;
            _subscriptions.UnsubscribeAndClear();

            if (_defs != null && Session.Project != null && Session.Space != null)
            {
                foreach (var def in _defs)
                {
                    _subscriptions.Add(DataManager.FactoryDefinitionUpdated.Add(
                        GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, def.Id),
                        async _ => await InvokeAsync(StateHasChanged)));
                }
            }
        }
    }

    private async Task HandleAliasChanged(string alias)
    {
        if (Definitions?.Count() != 1) return;
        var definition = Definitions.First();
        if (alias == definition.Alias) return;
        try
        {
            await DataManager.RenameFactoryDefinition(Session.User, Session.Space, definition, alias);
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't update name: {exc.Message}");
        }
    }

    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }
}
