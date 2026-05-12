using Microsoft.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs._2D.Class2D;
using Refund.Jobs._3D.Refine3D;
using Refund.Jobs.Preprocessing.Motion2D;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Screens.Overlay.Queues;

public partial class QueueCard : ComponentBase, IDisposable
{
    [Parameter]
    public required ReadOnlyJobQueue Queue { get; set; }

    [Inject]
    private DataManager DataManager { get; set; }

    [Inject]
    private RelaySession Session { get; set; }

    private string QueueTypeName => Queue.QueueType.ToString();

    private IEnumerable<ReadOnlyJob> FilteredJobs => Queue.QueuedJobs
                                                          .Where(j => Session.User.Role == UserRole.Admin ||
                                                                      j.Space.Project.Owner.Id == Session.User.Id ||
                                                                      j.Space.Project.Members.Any(m => m.Id == Session.User.Id))
                                                          .OrderByDescending(j => j.Status == JobStatus.Running)
                                                          .ThenBy(j => j.GetMostRecentEvent(EventType.WaitingStarted).Timestamp);

    private readonly List<GroupEventSubscription> _subscriptions = new();

    private List<ReadOnlyJob> _sampleJobs;
    private System.Timers.Timer _statusUpdateTimer;
    private Random _random = new Random();

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // re-render when the queue is updated
        _subscriptions.Add(DataManager.QueueUpdated.Add(GroupName.Queue(null),
                                                        async (_) => await InvokeAsync(StateHasChanged)));
    }

    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }
}