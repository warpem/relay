using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Panels.Right;

public partial class FolderProperties : ComponentBase, IDisposable
{
    [Parameter]
    public ReadOnlyFolder Folder { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    private List<GroupEventSubscription> _subscriptions = new();

    private string _aliasValidationError;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        _subscriptions.UnsubscribeAndClear();

        if (Folder != null && Session.View != null)
        {
            _subscriptions.Add(DataManager.ViewUpdated.Add(
                GroupName.View(Session.View.Space.Project.Id, Session.View.Space.Id, Session.View.Id),
                async (_) => await InvokeAsync(StateHasChanged)));
        }
    }

    private async Task HandleAliasChanged(string value)
    {
        _aliasValidationError = ValidateAlias(value);

        if (string.IsNullOrEmpty(_aliasValidationError))
        {
            await DataManager.UpdateFolder(Session.User, Session.View, Folder, f =>
            {
                f.Alias = value;
            });
        }
        else
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private string ValidateAlias(string newAlias)
    {
        if (string.IsNullOrWhiteSpace(newAlias))
            return "Folder name is required";

        if (newAlias.Length > 150)
            return "Folder name cannot be longer than 150 characters";

        return string.Empty;
    }

    private async Task HandleNotesChanged(string value)
    {
        await DataManager.UpdateFolder(Session.User, Session.View, Folder, f =>
        {
            f.Notes = value;
        });
    }

    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }
}
