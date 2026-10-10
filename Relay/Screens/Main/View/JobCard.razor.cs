using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;

namespace Relay.Screens.Main.View;

/// <summary>
/// Represents a job card in the view screen, visualizing a specific processing job within the workflow.
/// Handles job rendering, selection state tracking, and port interaction events.
/// </summary>
public partial class JobCard : ComponentBase, IDisposable
{
    [Inject] private ILogger<JobCard> Logger { get; set; } = default!;
    /// <summary>
    /// The job entity to be visualized by this card.
    /// </summary>
    [Parameter]
    public required ReadOnlyJob Job { get; set; }
    private ReadOnlyJob _job;
    
    /// <summary>
    /// Event callback that fires when the job card is clicked.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }
    
    /// <summary>
    /// Event callback that fires when the job card is double-clicked.
    /// Typically used to navigate to job details.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnDoubleClick { get; set; }
    
    /// <summary>
    /// Event callback that fires when a port on the job card is clicked.
    /// Used in ViewScreen to trigger job connection workflows.
    /// </summary>
    [Parameter]
    public EventCallback<PortClickArgs> OnPortClick { get; set; }

    /// <summary>
    /// Event callback that fires when the job card is middle-clicked.
    /// Used to open the job in a new tab.
    /// </summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnMiddleClick { get; set; }

    /// <summary>
    /// Event callback for context menus customized by the diagram host.
    /// Carries the job, mouse args, header, and actions.
    /// </summary>
    [Parameter]
    public EventCallback<CardContextMenuArgs> OnDiagramContextMenu { get; set; }

    /// <summary>
    /// When true, renders the card in diagram mode with layout-driven dimensions,
    /// input ports on the left, content scaling, and disabled dragging.
    /// </summary>
    [Parameter] public bool DiagramMode { get; set; }

    /// <summary>
    /// The width of the card in diagram mode, in pixels. Set by DiagramLayoutComputer.
    /// </summary>
    [Parameter] public double DiagramWidth { get; set; }

    /// <summary>
    /// The height of the card in diagram mode, in pixels. Set by DiagramLayoutComputer.
    /// </summary>
    [Parameter] public double DiagramHeight { get; set; }

    /// <summary>
    /// When true, the card is shown outside its view (e.g. Home activity): it looks the same, but dragging
    /// and port connection are disabled and the job editor's port-compatibility dimming doesn't apply.
    /// </summary>
    [Parameter] public bool OverviewMode { get; set; }

    /// <summary>
    /// Selection key identifying this card. Defaults to the unscoped key used inside a view;
    /// screens that mix spaces pass a scoped key from <see cref="Refund.DataModel.SelectionKey.ForJob(ReadOnlyJob)"/>.
    /// </summary>
    [Parameter] public SelectionKey? SelectionKey { get; set; }

    /// <summary>
    /// The effective selection key for this card.
    /// </summary>
    private SelectionKey CardKey => SelectionKey ?? Refund.DataModel.SelectionKey.ForJob(Job.Id);

    /// <summary>
    /// DOM ID of the card root, also the context menu anchor. Scoped keys produce IDs that stay
    /// unique when cards from several spaces share a page.
    /// </summary>
    [Parameter] public string DomIdSuffix { get; set; } = "";

    private string CardDomId => (CardKey.IsScoped
        ? $"card-p{CardKey.ProjectId}-s{CardKey.SpaceId}-j{_job.Id}"
        : $"card-{_job.Id}") + DomIdSuffix;

    /// <summary>
    /// Prefix for port element IDs, unique per card in the same way as <see cref="CardDomId"/>.
    /// </summary>
    private string PortDomIdPrefix => (CardKey.IsScoped
        ? $"p{CardKey.ProjectId}-s{CardKey.SpaceId}-j{Job.Id}"
        : $"j{Job.Id}") + DomIdSuffix;

    /// <summary>
    /// Subscriptions to data manager events for this job.
    /// </summary>
    private readonly List<GroupEventSubscription> _subscriptions = new();
    
    /// <summary>
    /// Parameters passed to the job content component.
    /// </summary>
    private readonly Dictionary<string, object> _componentParams = new();
    
    /// <summary>
    /// Tracks whether the mouse is currently over the job card.
    /// </summary>
    private bool _isMouseOver = false;
    
    /// <summary>
    /// Controls whether tooltips should be shown for this job card.
    /// </summary>
    private bool _showTooltips = false;
    
    /// <summary>
    /// Tracks which port is currently being interacted with.
    /// </summary>
    private ReadOnlyPort _openPort = null;
    
    /// <summary>
    /// List of related parent job IDs for visualization purposes.
    /// </summary>
    private List<string> _relationParent = new();
    
    /// <summary>
    /// List of related child job IDs for visualization purposes.
    /// </summary>
    private List<string> _relationChild = new();
    
    /// <summary>
    /// Last rendered (selected, any job selected) state in overview mode.
    /// </summary>
    private (bool Selected, bool JobSelected)? _overviewSelectionState;

    /// <summary>
    /// List of context menu actions available for this job.
    /// </summary>
    private List<MenuAction> _contextMenuActions;

    /// <summary>
    /// Header text for the context menu, reflecting single or multi-job selection.
    /// </summary>
    private string _contextMenuHeader;

    /// <summary>
    /// Initializes the component and sets up event handlers.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        
        JobEditor.OnJobChanged += HandleJobChanged;
        //JobEditor.OnJobUpdated += HandleJobUpdated;
        Selection.OnSelectionChanged += HandleSelectionChanged;
    }
    
    /// <summary>
    /// Handles job changed events from the job editor.
    /// </summary>
    /// <param name="job">The job that changed</param>
    private async Task HandleJobChanged(ReadOnlyJob job) => await InvokeAsync(StateHasChanged);

    /// <summary>
    /// Handles job updated events from the job editor.
    /// </summary>
    /// <param name="job">The job that was updated</param>
    // private async Task HandleJobUpdated(ReadOnlyJob job)
    // {
    //     if (job == Job)
    //         await InvokeAsync(StateHasChanged);
    // }

    /// <summary>
    /// Updates the relationship visualization when job selection changes.
    /// Identifies parent and child relationships between selected jobs and the current job.
    /// </summary>
    private async Task HandleSelectionChanged()
    {
        try
        {
            var oldRelationParent = _relationParent.ToList();
            var oldRelationChild = _relationChild.ToList();
            
            _relationParent.Clear();
            _relationChild.Clear();

            if (Selection.SelectedItems.Any() && !Selection.IsSelected(CardKey))
                foreach (var selectedJob in Selection.ResolveSelectedJobs(Job.Space).Where(j => j.Space == Job.Space))
                {
                    if (selectedJob.GetParents().Contains(Job))
                        _relationParent.Add($"J{selectedJob.Id}");

                    if (selectedJob.GetChildren().Contains(Job))
                        _relationChild.Add($"J{selectedJob.Id}");
                }

            bool anythingChanged = false;

            // In a view the screen re-renders its cards on selection changes; overview hosts don't,
            // so overview cards refresh their own outline and dimming
            if (OverviewMode)
            {
                var state = (Selection.IsSelected(CardKey), Selection.SelectedItems.Any(k => k.Type == ItemType.Job));
                if (state != _overviewSelectionState)
                {
                    _overviewSelectionState = state;
                    anythingChanged = true;
                }
            }

            if (oldRelationParent.Count != _relationParent.Count ||
                oldRelationChild.Count != _relationChild.Count ||
                oldRelationParent.Except(_relationParent).Any() ||
                oldRelationChild.Except(_relationChild).Any())
                anythingChanged = true;
            
            if (anythingChanged)
                await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling selection change for job {JobId}", Job.Id);
        }
    }

    /// <summary>
    /// Sets up event subscriptions when the job parameter changes.
    /// Ensures the component stays updated when job data changes.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (Job != _job)
        {
            _job = Job;
            
            _subscriptions.UnsubscribeAndClear();

            _componentParams["Job"] = Job;
            
            if (_job != null && _job.Space != null)
            {
                _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(_job.Space.Project.Id, _job.Space.Id, _job.Id),
                                                              async (_) => await InvokeAsync(StateHasChanged)));
                _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(_job.Space.Project.Id, _job.Space.Id, _job.Id),
                                                              async (_) => Dispose()));
            }
        }
    }

    /// <summary>
    /// When the context menu last closed, used to recognize clicks on its items.
    /// </summary>
    private DateTime _contextMenuClosedAt = DateTime.MinValue;

    /// <summary>
    /// Forwards card clicks. In overview mode, clicks on the context menu (rendered inside the card)
    /// are dropped so choosing an action doesn't change the selection; listing screens filter these
    /// themselves.
    /// </summary>
    private async Task HandleClick(MouseEventArgs args)
    {
        if (OverviewMode &&
            (_contextMenuActions != null || (DateTime.UtcNow - _contextMenuClosedAt).TotalMilliseconds < 300))
            return;

        await OnClick.InvokeAsync(args);
    }

    /// <summary>
    /// Handles mouse-up events to detect middle-click for open-in-new-tab.
    /// </summary>
    private async Task HandleMouseUp(MouseEventArgs args)
    {
        if (args.Button == 1)
            await OnMiddleClick.InvokeAsync(args);
    }

    /// <summary>
    /// Handles mouse-over events on the job card.
    /// Shows tooltips when the mouse hovers over the card.
    /// </summary>
    private async Task OnMouseOver()
    {
        _isMouseOver = true;
        _showTooltips = true;
    }

    /// <summary>
    /// Handles mouse-leave events on the job card.
    /// Hides tooltips when the mouse leaves the card.
    /// </summary>
    private async Task OnMouseLeave()
    {
        _isMouseOver = false;
        _showTooltips = false;
    }
    
    /// <summary>
    /// Handles clicks on job port elements.
    /// Creates and raises a PortClickArgs event that contains position and port data.
    /// </summary>
    /// <param name="eventArgs">Mouse event arguments</param>
    /// <param name="job">The job containing the clicked port</param>
    /// <param name="port">The port that was clicked</param>
    private async Task HandlePortClick(MouseEventArgs eventArgs, ReadOnlyJob job, ReadOnlyPortOut port)
    {
        if (OverviewMode)
            return;

        _showTooltips = false;

        await OnPortClick.InvokeAsync(new PortClickArgs
        {
            Job = job,
            Port = port,
            MouseEventArgs = eventArgs
        });
    }

    /// <summary>
    /// Handles context menu state changes.
    /// Prepares context menu actions when the menu is opened.
    /// </summary>
    /// <param name="value">Whether the context menu is being opened (true) or closed (false)</param>
    private async Task HandleContextMenu(bool value)
    {
        if (value)
        {
            if (Selection.IsSelected(CardKey))
            {
                // Card is already selected — build actions for entire selection
                var selectedJobs = Selection.ResolveSelectedJobs(_job.Space);
                _contextMenuActions = MenuActions.GetJobActions(selectedJobs);
                _contextMenuHeader = selectedJobs.Count > 1 ? $"{selectedJobs.Count} jobs selected" : _job.QualifiedName;
            }
            else
            {
                // Card is not selected — make it the sole selection
                await Selection.Replace([CardKey]);
                _contextMenuActions = MenuActions.GetJobActions([_job]);
                _contextMenuHeader = _job.QualifiedName;
            }

            // Notify ListingScreen about context menu to prevent spurious click processing
            if (OnClick.HasDelegate)
            {
                await OnClick.InvokeAsync(new MouseEventArgs
                {
                    Button = 2,
                    Type = "contextmenu",
                });
            }
        }
        else
        {
            _contextMenuActions = null;
            _contextMenuClosedAt = DateTime.UtcNow;
        }
    }
    
    private async Task HandleRightClick(MouseEventArgs args)
    {
        if (!DiagramMode) return; // RelayMenu handles it in list mode

        // Build context menu actions (same logic as HandleContextMenu)
        string header;
        List<MenuAction> actions;

        if (Selection.IsSelected(CardKey))
        {
            var selectedJobs = Selection.ResolveSelectedJobs(_job.Space);
            actions = MenuActions.GetJobActions(selectedJobs);
            header = selectedJobs.Count > 1 ? $"{selectedJobs.Count} jobs selected" : _job.QualifiedName;
        }
        else
        {
            await Selection.Replace([CardKey]);
            actions = MenuActions.GetJobActions([_job]);
            header = _job.QualifiedName;
        }

        await OnDiagramContextMenu.InvokeAsync(new CardContextMenuArgs
        {
            MouseEventArgs = args,
            Header = header,
            Actions = actions
        });
    }

    [Inject]
    private ViewDragDropService DragDrop { get; set; }

    private bool _isDragging;
    private int _dragCount;

    private void HandleDragStart(DragEventArgs args)
    {
        if (DiagramMode || OverviewMode)
            return;

        if (Selection.IsSelected(CardKey) && Selection.SelectedItems.Count > 1)
        {
            var items = Selection.ResolveSelectedJobs(_job.Space)
                .Cast<IViewItem>()
                .ToList();
            DragDrop.StartDrag(items);
            _dragCount = items.Count;
        }
        else
        {
            DragDrop.StartDrag([_job]);
            _dragCount = 1;
        }
        _isDragging = true;
    }

    private void HandleDragEnd(DragEventArgs args)
    {
        if (DiagramMode || OverviewMode)
            return;

        _isDragging = false;
        DragDrop.EndDrag();
    }

    /// <summary>
    /// Performs cleanup by unsubscribing from events and clearing subscriptions.
    /// </summary>
    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();

        JobEditor.OnJobChanged -= HandleJobChanged;
        //JobEditor.OnJobUpdated -= HandleJobUpdated;
        Selection.OnSelectionChanged -= HandleSelectionChanged;
    }
}

/// <summary>
/// Contains data about a port click event within the job card.
/// Used to transfer port click information from JobCard to ViewScreen for port connection operations.
/// </summary>
/// <remarks>
/// When a port is clicked in the view, ViewScreen uses this data to:
/// 1. Position the job type menu at the clicked location using MouseEventArgs coordinates
/// 2. Track which port was clicked for connection operations
/// 3. Determine which job the port belongs to
/// 
/// This facilitates job creation and connection workflows in the ViewScreen component.
/// </remarks>
public struct CardContextMenuArgs
{
    public MouseEventArgs MouseEventArgs { get; set; }
    public string Header { get; set; }
    public List<MenuAction> Actions { get; set; }
}

public struct PortClickArgs
{
    /// <summary>
    /// Mouse event data for the port click, containing position coordinates.
    /// Used in ViewScreen to position the job type menu at the click location.
    /// </summary>
    public MouseEventArgs MouseEventArgs { get; set; }
    
    /// <summary>
    /// The job containing the port that was clicked.
    /// </summary>
    public ReadOnlyJob Job { get; set; }
    
    /// <summary>
    /// The specific output port that was clicked.
    /// This port can be used as a source for creating connections to other jobs.
    /// </summary>
    public ReadOnlyPortOut Port { get; set; }
}
