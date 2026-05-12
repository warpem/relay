using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.Base;

namespace Relay.Screens.Main.Space;

public partial class SpaceScreen : ListingScreenLogic<ReadOnlyView>
{
    [Inject] private IToastService ToastService { get; set; }

    protected override SelectionKey GetSelectionKey(ReadOnlyView item) => SelectionKey.ForView(item.Id);

    private IEnumerable<ReadOnlyFactoryDefinition> FactoryDefinitions =>
        Session.Space?.FactoryDefinitions ?? Enumerable.Empty<ReadOnlyFactoryDefinition>();

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Session.OnSpaceChanged += HandleSpaceChanged;
    }

    private async Task HandleSpaceChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    public override void Dispose()
    {
        base.Dispose();
        Session.OnSpaceChanged -= HandleSpaceChanged;
    }

    protected override string GetTitle() => "Views";
    protected override string GetCreateButtonText() => "Create new view";
    protected override IEnumerable<ReadOnlyView> GetItems() => Session.Space?.Views ?? Enumerable.Empty<ReadOnlyView>();

    protected override Task ShowCreateDialogAsync()
    {
        return CreateViewDialog.Show(DialogService, this, OnCreateDialogClosedAsync);
    }

    protected override async Task OnCreateDialogClosedAsync(DialogResult result)
    {
        if (result.Data is CreateViewDialogResult { Success: true } createResult)
        {
            await Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = Session.Space.Id,
                ViewId = createResult.ViewId
            });
        }
    }

    protected override Task NavigateToItemAsync(ReadOnlyView item)
    {
        return Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Session.Project.Id,
            SpaceId = Session.Space.Id,
            ViewId = item.Id
        });
    }

    private SelectionKey? _lastFdSelectedKey;
    private DateTime? _lastFdContextMenuTime;

    private async Task HandleFdCardClicked(ReadOnlyFactoryDefinition def, MouseEventArgs args)
    {
        if (args.Button == 2 || args.Type == "contextmenu")
        {
            _lastFdContextMenuTime = DateTime.Now;
            return;
        }
        if (args.Button != 0)
            return;

        // Skip processing if this click happens after a context menu was recently shown.
        // This prevents delayed clicks from context menu interactions being processed.
        if (_lastFdContextMenuTime != null &&
            (DateTime.Now - _lastFdContextMenuTime.Value).TotalMilliseconds > CONTEXT_MENU_CLICK_THRESHOLD_MS)
        {
            _lastFdContextMenuTime = null;
            return;
        }

        var key = SelectionKey.ForFactoryDefinition(def.Id);

        if (MouseUtils.ModifierSelectSingle(args, Session.ClientOs))
        {
            if (!Selection.IsSelected(key))
                await Selection.AddRange([key]);
            else
                await Selection.RemoveRange([key]);
            _lastFdSelectedKey = key;
        }
        else if (MouseUtils.ModifierSelectRange(args, Session.ClientOs))
        {
            if (_lastFdSelectedKey.HasValue)
            {
                var allDefs = FactoryDefinitions.ToList();
                int startIndex = allDefs.FindIndex(d => SelectionKey.ForFactoryDefinition(d.Id).Equals(_lastFdSelectedKey.Value));
                int endIndex = allDefs.FindIndex(d => SelectionKey.ForFactoryDefinition(d.Id).Equals(key));

                if (startIndex >= 0 && endIndex >= 0)
                {
                    int min = Math.Min(startIndex, endIndex);
                    int max = Math.Max(startIndex, endIndex);
                    await Selection.Replace(allDefs.Skip(min).Take(max - min + 1).Select(d => SelectionKey.ForFactoryDefinition(d.Id)));
                }
            }
        }
        else
        {
            await Selection.Replace([key]);
            _lastFdSelectedKey = key;
        }
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

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();

        if (Session.Project == null || Session.Space == null)
            return;

        _subscriptions.Add(DataManager.SpaceUpdated.Add(GroupName.Space(Session.Project.Id, Session.Space.Id),
                                                        async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.SpaceDeleted.Add(GroupName.Space(Session.Project.Id, Session.Space.Id),
                                                        async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewCreated.Add(GroupName.View(Session.Project.Id, Session.Space.Id, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Session.Project.Id, Session.Space.Id, null),
                                                       async _ => await InvokeAsync(StateHasChanged)));

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