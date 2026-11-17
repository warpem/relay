using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Overlay.Queues;

public partial class QueueJobCard : ComponentBase, IAsyncDisposable
{
    [Parameter, EditorRequired]
    public ReadOnlyJob Job { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    [Inject]
    private MenuActionService MenuActions { get; set; }

    [Inject]
    private IJSRuntime JSRuntime { get; set; }

    [Inject]
    private INotificationService ToastService { get; set; }

    private string _id;
    private ReadOnlyJob _job;
    private bool _isSelected = false;
    private readonly List<GroupEventSubscription> _subscriptions = new();
    private List<MenuAction> _contextMenuActions;
    private DotNetObjectReference<QueueJobCard>? _objectReference;
    private IJSObjectReference _module;
    private bool _isResizingPool;

    private bool CanResizePool => !_isResizingPool &&
                                  Job?.Status == JobStatus.Running &&
                                  Job.PoolDesiredSize.HasValue;

    protected override void OnInitialized()
    {
        _id = Guid.NewGuid().ToString();
        base.OnInitialized();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if(Job != _job)
        {
            // Clear existing subscriptions
            foreach(var subscription in _subscriptions)
                subscription.Unsubscribe();
            _subscriptions.Clear();

            _job = Job;

            if(_job != null)
            {
                var jobIdentifier = GroupName.Job(_job.Space.Project.Id, _job.Space.Id, _job.Id);

                // Subscribe to job updates
                _subscriptions.Add(
                    DataManager.JobUpdated.Add(
                        jobIdentifier,
                        async (_) => await InvokeAsync(StateHasChanged)
                    )
                );

                // Subscribe to job deletions
                _subscriptions.Add(
                    DataManager.JobDeleted.Add(
                        jobIdentifier,
                        async (_) => await InvokeAsync(Dispose)
                    )
                );
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if(firstRender)
        {
            if(_module == null)
                _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Screens/Overlay/Queues/QueueJobCard.razor.js");

            _objectReference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("addClickOutsideEvent", $"job-card-{_id}", _objectReference);
        }
    }

    public void Dispose()
    {
        foreach(var subscription in _subscriptions)
        {
            subscription.Unsubscribe();
        }

        _subscriptions.Clear();
    }

    private string GetCreatedAtDisplay()
    {
        // Get current culture's date pattern with two digit year
        var culture = CultureInfo.CurrentCulture;
        var shortDatePattern = culture.DateTimeFormat.ShortDatePattern.Replace("yyyy", "yy");

        // Combine with time format
        var fullPattern = $"{shortDatePattern} HH:mm";

        return Job.GetMostRecentEvent(EventType.Created).Timestamp.ToString(fullPattern, culture);
    }

    /// <summary>
    /// Where the job lives, or null if its space or every view of it is gone (the job can outlive both while it
    /// still sits in a queue).
    /// </summary>
    private NavigationRequest? TryGetNavRequest()
    {
        var space = Job?.Space;
        var view = space?.Views.FirstOrDefault();
        if (space?.Project == null || view == null)
            return null;

        return new NavigationRequest
        {
            ProjectId = space.Project.Id,
            SpaceId = space.Id,
            ViewId = view.Id,
            JobId = Job.Id,
            Overlay = OverlayScreenType.None
        };
    }

    private async Task OpenJobAsync(bool newTab)
    {
        var request = TryGetNavRequest();
        if (request == null)
        {
            _ = ToastService.ShowErrorToastAsync($"Can't open {Job?.QualifiedName}: its space or view no longer exists.");
            return;
        }

        if (newTab)
            await Session.OpenInNewTabAsync(request);
        else
            await Session.NavigateToAsync(request);
    }

    private async Task HandleContextMenu(bool value)
    {
        if(value)
            _contextMenuActions = MenuActions.GetQueueJobActions([Job]);
        else
            _contextMenuActions = null;
    }

    private async Task ChangePoolSize(int delta)
    {
        if (!CanResizePool || Job.PoolDesiredSize is not { } desiredSize ||
            (delta < 0 && desiredSize <= 1) || (delta > 0 && desiredSize == int.MaxValue))
            return;

        _isResizingPool = true;
        try
        {
            await DataManager.ResizeJobPool(Session.User, Job, desiredSize + delta);
        }
        catch (Exception exception)
        {
            _ = ToastService.ShowErrorToastAsync($"Couldn't resize worker pool: {exception.Message}");
        }
        finally
        {
            _isResizingPool = false;
        }
    }

    private async Task HandleMouseUp(MouseEventArgs args)
    {
        if (args.Button == 1)
            await OpenJobAsync(newTab: true);
    }

    private async Task HandleClick(MouseEventArgs args)
    {
        if (MouseUtils.IsNewTabClick(args))
        {
            await OpenJobAsync(newTab: true);
            return;
        }

        switch(_isSelected)
        {
            case false:
                _isSelected = true;
                await InvokeAsync(StateHasChanged);

                break;
            case true:
                await OpenJobAsync(newTab: false);

                break;
        }
    }

    [JSInvokable]
    public async Task HandleClickOutside()
    {
        if(_isSelected)
        {
            _isSelected = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private Task HandleDoubleClick(MouseEventArgs args) => OpenJobAsync(newTab: MouseUtils.IsNewTabClick(args));

    public async ValueTask DisposeAsync()
    {
        if(_module is not null)
            await _module.DisposeAsync();

        if(_objectReference is not null)
            _objectReference.Dispose();
    }
}
