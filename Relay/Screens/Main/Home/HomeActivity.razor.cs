using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.Home;

/// <summary>
/// Horizontal strip of the user's recent jobs on the Home screen, shown as full job cards.
/// </summary>
/// <remarks>
/// The order is a snapshot taken on first render, refresh, or scope change, so cards never move while jobs
/// run or the user interacts with them. Submissions and new jobs only light up the refresh button.
/// Cards are mounted in batches as the strip scrolls towards its end, since each card is expensive.
/// </remarks>
public partial class HomeActivity : ComponentBase, IAsyncDisposable
{
    /// <summary>
    /// Number of cards mounted initially and added each time the strip nears its end.
    /// </summary>
    private const int BatchSize = 12;

    [Inject] private DataManager DataManager { get; set; }
    [Inject] private RelaySession Session { get; set; }
    [Inject] private CardSelectionService Selection { get; set; }
    [Inject] private IJSRuntime JSRuntime { get; set; }

    private readonly List<GroupEventSubscription> _subscriptions = new();

    private bool _disposed;

    private ActivityScope _scope = ActivityScope.Mine;

    /// <summary>
    /// Snapshot of activity in display order. Only refreshes and scope changes reorder it.
    /// </summary>
    private List<JobActivity> _items = new();

    /// <summary>
    /// Number of leading snapshot items that are mounted as cards.
    /// </summary>
    private int _renderCount = BatchSize;

    /// <summary>
    /// Whether newer activity than the snapshot exists in the current scope.
    /// </summary>
    private bool _hasNewer;

    private bool _canScrollBack;
    private bool _canScrollForward;

    /// <summary>
    /// Anchor for Shift range selection, as in listing screens.
    /// </summary>
    private SelectionKey? _lastSelectedKey;

    /// <summary>
    /// Card to scroll into view after the next render, set when a refresh keeps a selected job.
    /// </summary>
    private SelectionKey? _revealKey;

    private ElementReference _row;
    private IJSObjectReference _module;
    private IJSObjectReference _rowHandle;
    private DotNetObjectReference<HomeActivity> _dotNetRef;

    protected override void OnInitialized()
    {
        base.OnInitialized();

        TakeSnapshot();
        SubscribeToEvents();
    }

    private void SubscribeToEvents()
    {
        _subscriptions.UnsubscribeAndClear();

        // New and resubmitted jobs only offer a refresh; the snapshot order stays put
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(null, null, null),
                                                      args => InvokeAsync(() => HandleNewActivity(args.Object))));
        _subscriptions.Add(DataManager.JobQueued.Add(GroupName.Job(null, null, null),
                                                     args => InvokeAsync(() => HandleNewActivity(args.Object))));

        // Deleted or no longer accessible jobs drop out without reordering the rest
        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(null, null, null),
                                                      _ => InvokeAsync(Prune)));
        _subscriptions.Add(DataManager.SpaceDeleted.Add(GroupName.Space(null, null),
                                                        _ => InvokeAsync(Prune)));
        _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(null),
                                                          _ => InvokeAsync(Prune)));
        _subscriptions.Add(DataManager.ProjectUpdated.Add(GroupName.Project(null),
                                                          _ => InvokeAsync(Prune)));
    }

    /// <summary>
    /// Rebuilds the snapshot for the current scope and mounts enough cards to keep selected jobs visible.
    /// </summary>
    private void TakeSnapshot()
    {
        _items = JobActivity.Snapshot(DataManager.GetUserProjects(Session.User), Session.User, _scope);
        _hasNewer = false;

        int lastSelected = _items.FindLastIndex(a => Selection.IsSelected(a.Key));
        _renderCount = Math.Max(BatchSize, lastSelected + 1);

        int firstSelected = _items.FindIndex(a => Selection.IsSelected(a.Key));
        _revealKey = firstSelected >= 0 ? _items[firstSelected].Key : null;
    }

    private void HandleNewActivity(ReadOnlyJob job)
    {
        if (_hasNewer || JobActivity.Describe(job) is not { } activity || !activity.IsInScope(_scope, Session.User))
            return;

        if (!DataManager.GetUserProjects(Session.User).Any(p => p.Id == activity.ProjectId))
            return;

        // Finalization also raises JobQueued but isn't a new submission
        int index = _items.FindIndex(a => a.Key == activity.Key);
        if (index >= 0 && _items[index].Timestamp == activity.Timestamp)
            return;

        _hasNewer = true;
        StateHasChanged();
    }

    private async Task Prune()
    {
        var accessible = DataManager.GetUserProjects(Session.User).Select(p => p.Id).ToHashSet();
        var removed = _items.Where(a => !accessible.Contains(a.ProjectId) ||
                                        DataManager.FindJob(a.ProjectId, a.SpaceId, a.Job.Id) != a.Job)
                            .Select(a => a.Key).ToHashSet();
        if (removed.Count > 0)
        {
            _items.RemoveAll(a => removed.Contains(a.Key));
            await Selection.RemoveRange(removed);
            if (_lastSelectedKey.HasValue && removed.Contains(_lastSelectedKey.Value))
                _lastSelectedKey = null;
            StateHasChanged();
        }
    }

    private async Task RefreshAsync()
    {
        TakeSnapshot();
        await ScrollToStartUnlessRevealing();
    }

    private async Task SetScopeAsync(ActivityScope scope)
    {
        if (scope == _scope)
            return;

        _scope = scope;
        _lastSelectedKey = null;
        TakeSnapshot();

        // Jobs from the other scope would stay selected without a visible card
        var visible = _items.Select(a => a.Key).ToHashSet();
        await Selection.RemoveRange(Selection.SelectedItems.Where(k => k.Type == ItemType.Job && !visible.Contains(k)));

        await ScrollToStartUnlessRevealing();
    }

    private async Task ScrollToStartUnlessRevealing()
    {
        if (_revealKey == null && _rowHandle != null)
            await _rowHandle.InvokeVoidAsync("scrollToStart");
    }

    private async Task ScrollAsync(int direction)
    {
        if (_rowHandle != null)
            await _rowHandle.InvokeVoidAsync("scrollPage", direction);
    }

    private async Task HandleCardClick(JobActivity item, MouseEventArgs args)
    {
        // JobCard reports context menus as clicks; it already selects the card itself
        if (args.Button != 0 || args.Type == "contextmenu")
            return;

        var key = item.Key;

        if (MouseUtils.ModifierSelectSingle(args, Session.ClientOs))
        {
            // Don't mix project and job selections; the right panel can show only one kind
            await Selection.RemoveRange(Selection.SelectedItems.Where(k => k.Type != ItemType.Job));

            if (Selection.IsSelected(key))
                await Selection.Remove(key);
            else
                await Selection.AddRange([key]);

            _lastSelectedKey = key;
        }
        else if (MouseUtils.ModifierSelectRange(args, Session.ClientOs))
        {
            int start = _lastSelectedKey is { } anchor ? _items.FindIndex(a => a.Key == anchor) : -1;
            int end = _items.FindIndex(a => a.Key == key);

            if (start >= 0 && end >= 0)
                await Selection.Replace(_items.Skip(Math.Min(start, end))
                                              .Take(Math.Abs(end - start) + 1)
                                              .Select(a => a.Key));
            else
            {
                await Selection.Replace([key]);
                _lastSelectedKey = key;
            }
        }
        else
        {
            await Selection.Replace([key]);
            _lastSelectedKey = key;
        }
    }

    private async Task HandleCardDoubleClick(JobActivity item, MouseEventArgs args)
    {
        if (MouseUtils.IsNewTabClick(args))
            await Session.OpenInNewTabAsync(GetJobNavigation(item.Job));
        else
            await Session.NavigateToAsync(GetJobNavigation(item.Job));
    }

    private async Task HandleCardMiddleClick(JobActivity item, MouseEventArgs args)
    {
        await Session.OpenInNewTabAsync(GetJobNavigation(item.Job));
    }

    /// <summary>
    /// Navigation to a job's page in the first view that contains it, inside its folder if it has one.
    /// </summary>
    private static NavigationRequest GetJobNavigation(ReadOnlyJob job) => JobNavigation.ForJob(job);

    /// <summary>
    /// Called by the strip's scroll tracker with the current scroll state.
    /// </summary>
    [JSInvokable]
    public Task HandleScrollState(bool canScrollBack, bool canScrollForward, bool nearEnd)
    {
        if (_disposed)
            return Task.CompletedTask;

        bool changed = canScrollBack != _canScrollBack || canScrollForward != _canScrollForward;
        _canScrollBack = canScrollBack;
        _canScrollForward = canScrollForward;

        if (nearEnd && _renderCount < _items.Count)
        {
            _renderCount = Math.Min(_items.Count, _renderCount + BatchSize);
            changed = true;
        }

        if (changed)
            StateHasChanged();

        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed)
            return;

        try
        {
            if (firstRender)
            {
                var module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Screens/Main/Home/HomeActivity.razor.js");
                if (_disposed)
                {
                    await module.DisposeAsync();
                    return;
                }
                _module = module;
                _dotNetRef = DotNetObjectReference.Create(this);
            }

            bool hasRow = _items.Count > 0;
            if (_module != null && hasRow && _rowHandle == null)
            {
                var handle = await _module.InvokeAsync<IJSObjectReference>("attach", _row, _dotNetRef);
                if (_disposed)
                {
                    await handle.InvokeVoidAsync("dispose");
                    await handle.DisposeAsync();
                    return;
                }
                _rowHandle = handle;
            }
            else if (!hasRow && _rowHandle != null)
                await DetachRowAsync();

            if (_disposed || _rowHandle == null)
                return;

            if (_revealKey is { } reveal)
            {
                _revealKey = null;
                await _rowHandle.InvokeVoidAsync("reveal", GetItemDomId(reveal));
            }

            if (!_disposed && _rowHandle != null)
                await _rowHandle.InvokeVoidAsync("update");
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (JSException) when (_disposed) { }
    }

    private static string GetItemDomId(SelectionKey key) => $"activity-p{key.ProjectId}-s{key.SpaceId}-j{key.Id}";

    private async Task DetachRowAsync()
    {
        var handle = _rowHandle;
        _rowHandle = null;
        await handle.InvokeVoidAsync("dispose");
        await handle.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _subscriptions.UnsubscribeAndClear();

        try
        {
            if (_rowHandle != null)
                await DetachRowAsync();

            if (_module != null)
                await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        _dotNetRef?.Dispose();
    }
}
