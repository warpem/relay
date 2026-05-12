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
    /// Event callback for context menu in diagram mode (where FluentMenu can't be used
    /// inside CSS-transformed containers). Carries the job, mouse args, header, and actions.
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

            if (Selection.SelectedItems.Any() && !Selection.IsSelected(SelectionKey.ForJob(Job.Id)))
                foreach (var selectedJob in Selection.IdsOfType(ItemType.Job).Select(id => DataManager.FindJob(Job.Space.Project.Id, Job.Space.Id, id)))
                {
                    if (selectedJob.GetParents().Contains(Job))
                        _relationParent.Add($"J{selectedJob.Id}");

                    if (selectedJob.GetChildren().Contains(Job))
                        _relationChild.Add($"J{selectedJob.Id}");
                }

            bool anythingChanged = false;
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
            if (Selection.IsSelected(SelectionKey.ForJob(_job.Id)))
            {
                // Card is already selected — build actions for entire selection
                var selectedJobs = Selection.IdsOfType(ItemType.Job)
                    .Select(id => DataManager.FindJob(_job.Space.Project.Id, _job.Space.Id, id))
                    .Where(j => j != null)
                    .ToList();
                _contextMenuActions = MenuActions.GetJobActions(selectedJobs);
                _contextMenuHeader = $"{selectedJobs.Count} jobs selected";
            }
            else
            {
                // Card is not selected — make it the sole selection
                await Selection.Replace([SelectionKey.ForJob(_job.Id)]);
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
            _contextMenuActions = null;
    }
    
    private async Task HandleRightClick(MouseEventArgs args)
    {
        if (!DiagramMode) return; // FluentMenu handles it in list mode

        // Build context menu actions (same logic as HandleContextMenu)
        string header;
        List<MenuAction> actions;

        if (Selection.IsSelected(SelectionKey.ForJob(_job.Id)))
        {
            var selectedJobs = Selection.IdsOfType(ItemType.Job)
                .Select(id => DataManager.FindJob(_job.Space.Project.Id, _job.Space.Id, id))
                .Where(j => j != null)
                .ToList();
            actions = MenuActions.GetJobActions(selectedJobs);
            header = $"{selectedJobs.Count} jobs selected";
        }
        else
        {
            await Selection.Replace([SelectionKey.ForJob(_job.Id)]);
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
        if (DiagramMode)
            return;

        if (Selection.IsSelected(SelectionKey.ForJob(_job.Id)) && Selection.SelectedItems.Count > 1)
        {
            var items = Selection.IdsOfType(ItemType.Job)
                .Select(id => DataManager.FindJob(_job.Space.Project.Id, _job.Space.Id, id))
                .Where(j => j != null)
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
        if (DiagramMode)
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