using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Relay.Screens.Main.View;

public partial class FactoryCard : ComponentBase, IDisposable
{
    public enum FactoryCardMode { Definition, Instance }

    [Parameter] public ReadOnlyFactoryDefinition Definition { get; set; }
    [Parameter] public ReadOnlyFactoryInstance Instance { get; set; }
    [Parameter] public FactoryCardMode Mode { get; set; }
    [Parameter] public bool DiagramMode { get; set; }
    [Parameter] public double DiagramWidth { get; set; }
    [Parameter] public double DiagramHeight { get; set; }
    [Parameter] public EventCallback<MouseEventArgs> OnClick { get; set; }
    [Parameter] public EventCallback<MouseEventArgs> OnDoubleClick { get; set; }
    [Parameter] public EventCallback<PortClickArgs> OnPortClick { get; set; }
    [Parameter] public EventCallback<CardContextMenuArgs> OnDiagramContextMenu { get; set; }

    [Inject] private DataManager DataManager { get; set; }
    [Inject] private RelaySession Session { get; set; }
    [Inject] private CardSelectionService Selection { get; set; }
    [Inject] private MenuActionService MenuActions { get; set; }

    private readonly List<GroupEventSubscription> _subscriptions = new();
    private ReadOnlyFactoryInstance _instance;
    private ReadOnlyFactoryDefinition _definition;
    private List<MenuAction> _contextMenuActions;
    private string _contextMenuHeader;
    private bool _showTooltips;

    private SelectionKey SelectionKey => Mode switch
    {
        FactoryCardMode.Instance when Instance != null => SelectionKey.ForFactoryInstance(Instance.Id),
        FactoryCardMode.Definition when Definition != null => SelectionKey.ForFactoryDefinition(Definition.Id),
        _ => default
    };

    private ReadOnlyFactoryDefinition ResolvedDefinition => Mode == FactoryCardMode.Instance
        ? Instance?.Definition
        : Definition;

    private string DisplayName
    {
        get
        {
            if (Mode == FactoryCardMode.Instance && Instance != null)
            {
                // Definition name is always the primary identifier (like job type name)
                if (Instance.Definition != null)
                    return $"FI{Instance.Id} {Instance.Definition.Alias}";
                return $"FI{Instance.Id}";
            }
            return Definition?.QualifiedName ?? "";
        }
    }

    /// <summary>Shows the alias below the header when the instance has a custom alias.</summary>
    private bool ShowTypeLabel => Mode == FactoryCardMode.Instance
        && !string.IsNullOrWhiteSpace(Instance?.Alias);

    private JobStatus AggregateStatus => Mode == FactoryCardMode.Instance
        ? Instance?.AggregateStatus ?? JobStatus.Building
        : JobStatus.Finished;

    private int HeightSquares
    {
        get
        {
            var def = ResolvedDefinition;
            if (def == null) return 1;
            int maxPorts = Math.Max(def.ExposedPortsIn.Count, def.ExposedPortsOut.Count);
            return maxPorts <= 6 ? 1 : (int)Math.Ceiling(maxPorts / 6.0);
        }
    }

    protected override void OnParametersSet()
    {
        if (Mode == FactoryCardMode.Instance)
        {
            if (Instance != _instance)
            {
                _instance = Instance;
                _subscriptions.UnsubscribeAndClear();

                if (_instance?.Space != null)
                {
                    var projectId = _instance.Space.Project.Id;
                    var spaceId = _instance.Space.Id;

                    _subscriptions.Add(DataManager.FactoryInstanceUpdated.Add(
                        GroupName.FactoryInstance(projectId, spaceId, _instance.Id),
                        async _ => await InvokeAsync(StateHasChanged)));

                    // Sub-job status changes affect aggregate status — filter to this instance's sub-jobs
                    _subscriptions.Add(DataManager.JobUpdated.Add(
                        GroupName.Job(projectId, spaceId, null),
                        async args =>
                        {
                            if (_instance != null && _instance.SubJobIds.Contains(args.Object.Id))
                                await InvokeAsync(StateHasChanged);
                        }));
                }
            }
        }
        else // Definition mode
        {
            if (Definition != _definition)
            {
                _definition = Definition;
                _subscriptions.UnsubscribeAndClear();

                // Definition mode doesn't need event subscriptions for now
                // (FactoryDefinitionPanel handles re-render)
            }
        }
    }

    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }

    private async Task HandleClick(MouseEventArgs args)
    {
        await OnClick.InvokeAsync(args);
    }

    private async Task HandleDoubleClick(MouseEventArgs args)
    {
        await OnDoubleClick.InvokeAsync(args);
    }

    private async Task HandleRightClick(MouseEventArgs args)
    {
        if (!DiagramMode) return;

        string header;
        List<MenuAction> actions;

        if (Mode == FactoryCardMode.Instance)
        {
            if (Selection.IsSelected(SelectionKey))
            {
                var selectedInstances = Selection.IdsOfType(ItemType.FactoryInstance)
                    .Select(id => Instance.Space.FindFactoryInstance(id))
                    .Where(fi => fi != null)
                    .ToList();
                actions = MenuActions.GetFactoryInstanceActions(selectedInstances);
                header = $"{selectedInstances.Count} instances selected";
            }
            else
            {
                await Selection.Replace([SelectionKey]);
                actions = MenuActions.GetFactoryInstanceActions([Instance]);
                header = DisplayName;
            }
        }
        else
        {
            if (Selection.IsSelected(SelectionKey))
            {
                var selectedDefs = Selection.IdsOfType(ItemType.FactoryDefinition)
                    .Select(id => Session.Space?.FactoryDefinitions.FirstOrDefault(d => d.Id == id))
                    .Where(d => d != null)
                    .ToList();
                actions = MenuActions.GetFactoryDefinitionActions(selectedDefs);
                header = selectedDefs.Count > 1 ? $"{selectedDefs.Count} definitions selected" : DisplayName;
            }
            else
            {
                await Selection.Replace([SelectionKey]);
                actions = MenuActions.GetFactoryDefinitionActions([Definition]);
                header = DisplayName;
            }
        }

        await OnDiagramContextMenu.InvokeAsync(new CardContextMenuArgs
        {
            MouseEventArgs = args,
            Header = header,
            Actions = actions
        });
    }

    private async Task HandleContextMenu(bool value)
    {
        if (value)
        {
            if (Mode == FactoryCardMode.Instance)
            {
                if (Selection.IsSelected(SelectionKey))
                {
                    var selectedInstances = Selection.IdsOfType(ItemType.FactoryInstance)
                        .Select(id => Instance.Space.FindFactoryInstance(id))
                        .Where(fi => fi != null)
                        .ToList();
                    _contextMenuActions = MenuActions.GetFactoryInstanceActions(selectedInstances);
                    _contextMenuHeader = selectedInstances.Count > 1
                        ? $"{selectedInstances.Count} instances selected"
                        : DisplayName;
                }
                else
                {
                    await Selection.Replace([SelectionKey]);
                    _contextMenuActions = MenuActions.GetFactoryInstanceActions([Instance]);
                    _contextMenuHeader = DisplayName;
                }
            }
            else
            {
                if (Selection.IsSelected(SelectionKey))
                {
                    var selectedDefs = Selection.IdsOfType(ItemType.FactoryDefinition)
                        .Select(id => Session.Space?.FactoryDefinitions.FirstOrDefault(d => d.Id == id))
                        .Where(d => d != null)
                        .ToList();
                    _contextMenuActions = MenuActions.GetFactoryDefinitionActions(selectedDefs);
                    _contextMenuHeader = selectedDefs.Count > 1
                        ? $"{selectedDefs.Count} definitions selected"
                        : DisplayName;
                }
                else
                {
                    await Selection.Replace([SelectionKey]);
                    _contextMenuActions = MenuActions.GetFactoryDefinitionActions([Definition]);
                    _contextMenuHeader = DisplayName;
                }
            }

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
        }
    }

    private async Task HandleExposedPortClick(MouseEventArgs mouseArgs, ExposedPort exposedPort, bool isInput)
    {
        if (!OnPortClick.HasDelegate || Instance == null)
            return;

        // Map exposed port's blueprint-local SubJobId to the real job ID
        int blueprintIndex = exposedPort.SubJobId - 1;
        if (blueprintIndex < 0 || blueprintIndex >= Instance.SubJobIds.Count)
            return;

        int realJobId = Instance.SubJobIds[blueprintIndex];
        var subJob = Instance.Space?.FindJob(realJobId);
        if (subJob == null)
            return;

        if (!isInput)
        {
            // Output port click initiates edge creation (same pattern as JobCard)
            var portOut = subJob.PortsOut.Values.FirstOrDefault(p => p.Name == exposedPort.PortName);
            if (portOut != null)
            {
                await OnPortClick.InvokeAsync(new PortClickArgs
                {
                    Job = subJob,
                    Port = portOut,
                    MouseEventArgs = mouseArgs
                });
            }
        }
    }

    // Minimap helpers (using compact FolderLayout)
    private static readonly double MinimapMaxWidth = VisualProvider.GetWidth(1) - 23;
    private static readonly double MinimapMaxHeight = VisualProvider.GetHeight(1) - 20 - 60;

    private (double Width, double Height) GetCardMinimapSize(FolderLayout layout)
    {
        double scale = Math.Min(MinimapMaxWidth / layout.GraphWidth, MinimapMaxHeight / layout.GraphHeight);
        if (scale > 1) scale = 1;
        return (layout.GraphWidth * scale, layout.GraphHeight * scale);
    }

    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private string GetCardNodeColor(FolderLayoutNode node)
    {
        if (Mode == FactoryCardMode.Instance && Instance != null)
        {
            // node.ItemId is a blueprint sub-job ID; map to real job ID via definition order
            var def = Instance.Definition;
            if (def != null)
            {
                var blueprintSubJobs = def.SubJobs;
                for (int i = 0; i < blueprintSubJobs.Count && i < Instance.SubJobIds.Count; i++)
                {
                    if (blueprintSubJobs[i].Id == node.ItemId)
                    {
                        int realJobId = Instance.SubJobIds[i];
                        var realJob = Instance.Space?.FindJob(realJobId);
                        if (realJob != null)
                            return Refund.Utils.JobStatusExtensions.GetStatusHexColor(realJob.Status);
                        break;
                    }
                }
            }
        }
        else if (Mode == FactoryCardMode.Definition && Definition != null)
        {
            return Refund.Utils.JobStatusExtensions.GetStatusHexColor(JobStatus.Building);
        }
        return "#888";
    }

    private static string GetCardEdgePath(FolderLayoutEdge edge)
    {
        var pts = new List<(double X, double Y)> { (edge.SourceX, edge.SourceY) };
        if (edge.BendPoints != null)
            foreach (var bp in edge.BendPoints)
                pts.Add(bp);
        pts.Add((edge.TargetX, edge.TargetY));

        var sb = new StringBuilder();
        sb.Append($"M {F(pts[0].X)},{F(pts[0].Y)}");

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p1 = pts[i];
            var p2 = pts[i + 1];
            double tangent = (p2.X - p1.X) * 0.75;

            sb.Append($" C {F(p1.X + tangent)},{F(p1.Y)}" +
                      $" {F(p2.X - tangent)},{F(p2.Y)}" +
                      $" {F(p2.X)},{F(p2.Y)}");
        }

        return sb.ToString();
    }

    private static string GetPortColor(ExposedPort port) => PortColors.Get(port.ResourceType);
}
