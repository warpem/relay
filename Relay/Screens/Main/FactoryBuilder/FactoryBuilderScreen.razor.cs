using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.View;
using Warp.Tools;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Relay.Screens.Main.FactoryBuilder;

public partial class FactoryBuilderScreen : ComponentBase, IDisposable
{
    [Inject] private DataManager DataManager { get; set; }
    [Inject] private RelaySession Session { get; set; }
    [Inject] private JobEditorService JobEditor { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Inject] private CardSelectionService Selection { get; set; }
    [Inject] private DiagramViewService DiagramService { get; set; }

    private readonly List<GroupEventSubscription> _subscriptions = new();

    private bool _jobTypeMenuOpen;
    private ReadOnlyPortOut _clickedPort;
    private float2? _clickedPosition;

    // Card context menu state
    private bool _cardContextMenuOpen;
    private double _cardContextMenuX;
    private double _cardContextMenuY;
    private string _cardContextMenuHeader;
    private List<MenuAction> _cardContextMenuActions;

    private ReadOnlyFactoryDefinition Definition => Session.FactoryDefinition;

    /// <summary>True when instances of this definition exist — definition becomes read-only.</summary>
    private bool IsLocked =>
        Definition != null &&
        Session.Space?.FactoryInstances.Any(i => i.DefinitionId == Definition.Id) == true;

    private IEnumerable<IViewItem> GetItems()
    {
        return Definition?.SubJobs?.Cast<IViewItem>()
               ?? Enumerable.Empty<IViewItem>();
    }

    private DiagramLayout? GetDiagramLayout() => Definition?.DiagramLayout;

    protected override async Task OnInitializedAsync()
    {
        // Close any regular job editor when entering the builder
        if (JobEditor.IsActive)
            await JobEditor.SetJob(null);

        Session.OnFactoryDefinitionChanged += HandleDefinitionChanged;
        DiagramService.OnRelayoutRequested += HandleRelayoutRequested;
        SubscribeToEvents();

        // Compute initial layout if definition has sub-jobs but no layout
        if (Definition != null && Definition.SubJobs.Count > 0 && Definition.DiagramLayout == null)
            await RecomputeLayout();
    }

    private void SubscribeToEvents()
    {
        _subscriptions.UnsubscribeAndClear();
        if (Definition != null)
        {
            _subscriptions.Add(DataManager.FactoryDefinitionUpdated.Add(
                GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, Definition.Id),
                async _ =>
                {
                    // Recompute layout if connectivity changed (e.g. edge added/removed from editor).
                    // The layout computers return the same object if the hash matches, so check
                    // for identity to avoid an infinite update loop.
                    if (Definition != null)
                    {
                        var newLayout = DiagramLayoutComputer.ComputeLayoutForDefinition(Definition);
                        if (newLayout != Definition.DiagramLayout)
                        {
                            var newCardLayout = FolderLayoutComputer.ComputeCardLayoutForDefinition(Definition, Definition.CardLayout);
                            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, Definition, def =>
                            {
                                def.DiagramLayout = newLayout;
                                def.CardLayout = newCardLayout;
                            });
                            return; // The update above will fire another event that triggers StateHasChanged
                        }
                    }
                    await InvokeAsync(StateHasChanged);
                }));
        }
    }

    private async Task HandleDefinitionChanged()
    {
        SubscribeToEvents();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleTypeSelected(Type type)
    {
        try
        {
            Job template = Activator.CreateInstance(type) as Job;
            template.Status = JobStatus.Building;

            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, Definition, def =>
            {
                template.Id = def.SubJobs.Count > 0 ? def.SubJobs.Max(j => j.Id) + 1 : 1;
                template.DirectoryName = "";
                def.SubJobs.Add(template);

                // If a port was clicked, create an internal edge to the new sub-job's first compatible port
                if (_clickedPort != null)
                {
                    var sourceJob = _clickedPort.Job;
                    var resourceType = _clickedPort.ResourceType;

                    // Find first compatible input port on the new sub-job
                    var targetPort = template.PortsIn.Values
                        .FirstOrDefault(p => p.ResourceType == resourceType);

                    if (targetPort != null)
                    {
                        def.InternalEdges.Add(new FactoryEdge(
                            $"{sourceJob.Id}.{_clickedPort.Name}",
                            $"{template.Id}.{targetPort.Name}"));
                    }
                }
            });

            await RecomputeLayout();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't add sub-job: " + exc.Message);
        }
    }

    private async Task HandlePortSelected((Type jobType, ReadOnlyPortOut portOut, ReadOnlyPortIn portIn) args)
    {
        try
        {
            Job template = Activator.CreateInstance(args.jobType) as Job;
            template.Status = JobStatus.Building;

            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, Definition, def =>
            {
                template.Id = def.SubJobs.Count > 0 ? def.SubJobs.Max(j => j.Id) + 1 : 1;
                template.DirectoryName = "";
                def.SubJobs.Add(template);

                if (args.portOut != null)
                {
                    def.InternalEdges.Add(new FactoryEdge(
                        $"{args.portOut.Job.Id}.{args.portOut.Name}",
                        $"{template.Id}.{args.portIn.Name}"));
                }
            });

            await RecomputeLayout();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't add sub-job: " + exc.Message);
        }
    }

    private bool IsPortExposed(int subJobId, string portName)
    {
        return Definition?.ExposedPortsIn.Any(p => p.SubJobId == subJobId && p.PortName == portName) == true;
    }

    private async Task HandlePortConnected((ReadOnlyPortOut portOut, ReadOnlyPortIn portIn) args)
    {
        try
        {
            if (args.portOut == null || args.portIn == null)
                throw new Exception("Both ports must be specified");

            if (args.portOut.ResourceType != args.portIn.ResourceType)
                throw new Exception("Can't connect ports of different resource types");

            if (IsPortExposed(args.portIn.Job.Id, args.portIn.Name))
                throw new Exception("Can't connect to an exposed port — unexpose it first");

            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, Definition, def =>
            {
                def.InternalEdges.Add(new FactoryEdge(
                    $"{args.portOut.Job.Id}.{args.portOut.Name}",
                    $"{args.portIn.Job.Id}.{args.portIn.Name}"));
            });

            await RecomputeLayout();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't connect ports: " + exc.Message);
        }
    }

    private async Task HandleItemClick((IViewItem Item, MouseEventArgs Args) e)
    {
        if (IsLocked) return;

        var key = SelectionKey.ForJob(e.Item.Id);

        if (e.Args.CtrlKey || e.Args.MetaKey)
        {
            if (!Selection.IsSelected(key))
                await Selection.AddRange([key]);
            else
                await Selection.RemoveRange([key]);
        }
        else
        {
            await Selection.Replace([key]);
        }
    }

    private async Task HandleItemDoubleClick((IViewItem Item, MouseEventArgs Args) e)
    {
        if (IsLocked) return;

        if (e.Item is ReadOnlyJob job)
        {
            await JobEditor.SetJob(job);
        }
    }

    private async Task HandlePortClicked(PortClickArgs args)
    {
        if (IsLocked)
        {
            ToastService.ShowWarning("This definition can no longer be edited because instances exist. Clone to modify.");
            return;
        }

        _clickedPosition = new float2((float)args.MouseEventArgs.ClientX - RelaySession.LeftPanelWidth,
                                      (float)args.MouseEventArgs.ClientY - RelaySession.TopPanelHeight);
        _clickedPort = args.Port;
        _jobTypeMenuOpen = true;
    }

    private async Task OnContextMenu(MouseEventArgs args)
    {
        if (IsLocked)
        {
            ToastService.ShowWarning("This definition can no longer be edited because instances exist. Clone to modify.");
            return;
        }

        _clickedPosition = new float2((float)args.ClientX - RelaySession.LeftPanelWidth,
                                      (float)args.ClientY - RelaySession.TopPanelHeight);
        _clickedPort = null;
        _jobTypeMenuOpen = true;
    }

    private async Task HandleJobTypeMenuOpenChanged(bool isOpen)
    {
        _jobTypeMenuOpen = isOpen;
        if (!isOpen)
        {
            _clickedPort = null;
            _clickedPosition = null;
        }
    }

    private Func<Type, bool> GetJobTypeFilter()
    {
        if (_clickedPort != null)
        {
            var resourceType = _clickedPort.ResourceType;
            return t => Job.AllTypesPortsIn[t].Any(p => p.Value.ResourceType == resourceType);
        }

        return t => true;
    }

    private Func<Type, bool> GetPortFilter()
    {
        if (_clickedPort != null)
        {
            var resourceType = _clickedPort.ResourceType;
            return p => p == resourceType;
        }

        return null;
    }

    private Func<ReadOnlyPortIn, bool> GetConnectPortFilter()
    {
        if (Definition == null) return null;
        // Exclude ports that are already exposed
        return port => !Definition.ExposedPortsIn.Any(
            p => p.SubJobId == port.Job.Id && p.PortName == port.Name);
    }

    private MenuType GetMenuType()
    {
        if (JobEditor.IsActive && _clickedPort != null)
            return MenuType.ConnectToPort;

        if (_clickedPort != null)
            return MenuType.CreateFromPort;

        return MenuType.CreateFromType;
    }

    private void HandleCardContextMenu(CardContextMenuArgs args)
    {
        if (IsLocked)
        {
            ToastService.ShowWarning("This definition can no longer be edited because instances exist. Clone to modify.");
            return;
        }

        // Override the actions from JobCard with builder-specific actions (Delete only)
        var selectedJobIds = Selection.IdsOfType(ItemType.Job).ToList();
        if (selectedJobIds.Count == 0)
            return;

        var actions = GetSubJobActions(selectedJobIds);

        _cardContextMenuX = args.MouseEventArgs.ClientX;
        _cardContextMenuY = args.MouseEventArgs.ClientY;
        _cardContextMenuHeader = selectedJobIds.Count > 1
            ? $"{selectedJobIds.Count} sub-jobs selected"
            : args.Header;
        _cardContextMenuActions = actions;
        _cardContextMenuOpen = true;
    }

    private List<MenuAction> GetSubJobActions(List<int> subJobIds)
    {
        var actions = new List<MenuAction>();

        if (subJobIds.Count == 1)
        {
            var subJob = Definition?.SubJobs.FirstOrDefault(j => j.Id == subJobIds[0]);
            if (subJob != null)
            {
                actions.Add(new MenuAction
                {
                    Name = "Edit parameters",
                    IconSmall = new Icons.Regular.Size16.Edit(),
                    IconLarge = new Icons.Regular.Size20.Edit(),
                    Action = async () => await JobEditor.SetJob(subJob)
                });
            }
        }

        var actionDelete = new MenuAction
        {
            Name = subJobIds.Count > 1 ? $"Delete {subJobIds.Count} sub-jobs" : "Delete sub-job",
            NeedsConfirmation = true,
            TextColor = "var(--error)",
            IconSmall = new Icons.Regular.Size16.Delete().WithColor("var(--error)"),
            IconLarge = new Icons.Regular.Size20.Delete().WithColor("var(--error)"),
            Action = async () =>
            {
                foreach (var subJobId in subJobIds)
                    await DeleteSubJob(subJobId);
            }
        };

        actions.Add(actionDelete);
        return actions;
    }

    private async Task DeleteSubJob(int subJobId)
    {
        try
        {
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, Definition, def =>
            {
                // Remove the sub-job itself
                def.SubJobs.RemoveAll(j => j.Id == subJobId);

                // Remove internal edges referencing this sub-job
                var prefix = $"{subJobId}.";
                def.InternalEdges.RemoveAll(e =>
                    e.Source.StartsWith(prefix) || e.Target.StartsWith(prefix));

                // Remove external edges referencing this sub-job
                def.ExternalEdges.RemoveAll(e => e.SubJobId == subJobId);

                // Remove exposed ports referencing this sub-job
                def.ExposedPortsIn.RemoveAll(p => p.SubJobId == subJobId);
                def.ExposedPortsOut.RemoveAll(p => p.SubJobId == subJobId);

                // Remove exposed properties referencing this sub-job
                def.ExposedProperties.RemoveAll(p => p.SubJobId == subJobId);

                // Remove queue assignments for this sub-job
                def.QueueAssignments.Remove(subJobId);
            });

            await RecomputeLayout();
            Selection.Clear();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't delete sub-job: " + exc.Message);
        }
    }

    private void CloseCardContextMenu()
    {
        _cardContextMenuOpen = false;
        _cardContextMenuActions = null;
    }

    private async void HandleRelayoutRequested()
    {
        await RecomputeLayout();
    }

    /// <summary>
    /// Recomputes the diagram layout for the current definition and saves it.
    /// </summary>
    private async Task RecomputeLayout()
    {
        if (Definition == null) return;

        try
        {
            var layout = DiagramLayoutComputer.ComputeLayoutForDefinition(Definition);
            var cardLayout = FolderLayoutComputer.ComputeCardLayoutForDefinition(Definition, Definition.CardLayout);
            await DataManager.UpdateFactoryDefinition(Session.User, Session.Space, Definition, def =>
            {
                def.DiagramLayout = layout;
                def.CardLayout = cardLayout;
            });
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't compute layout: " + exc.Message);
        }
    }

    public void Dispose()
    {
        // Close sub-job editor when leaving the builder
        if (JobEditor.IsActive)
            _ = JobEditor.SetJob(null);

        Session.OnFactoryDefinitionChanged -= HandleDefinitionChanged;
        DiagramService.OnRelayoutRequested -= HandleRelayoutRequested;
        _subscriptions.UnsubscribeAndClear();
    }
}
