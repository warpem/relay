using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Relay.Panels.Right.JobEditor;

/// <summary>
/// A component that displays job input ports with their connections and validation errors.
/// </summary>
/// <remarks>
/// This component handles the display of:
/// - Port circles colored by resource type
/// - Port aliases with connection count vs. requirements
/// - Connected edges with source job information
/// - Delete buttons for edges (hidden in disabled mode)
/// - Validation errors for ports (hidden in disabled mode)
/// - Visual styling based on port active state
/// 
/// Can be used in both editable (JobEditor) and read-only (JobProperties) contexts
/// by setting the IsDisabled property.
/// </remarks>
public partial class JobPortDisplay : ComponentBase
{
    /// <summary>
    /// Gets or sets the job whose ports should be displayed.
    /// </summary>
    [Parameter]
    public required ReadOnlyJob Job { get; set; }

    /// <summary>
    /// Gets or sets whether the ports should be displayed in read-only mode.
    /// </summary>
    /// <remarks>
    /// When true, delete buttons are hidden and port errors are not displayed.
    /// </remarks>
    [Parameter]
    public bool IsDisabled { get; set; } = false;

    /// <summary>
    /// Gets or sets a function to get validation error messages for ports.
    /// </summary>
    /// <remarks>
    /// This function is not called when IsDisabled is true.
    /// </remarks>
    [Parameter]
    public required Func<string, List<string>> GetPortErrors { get; set; }

    /// <summary>
    /// Gets or sets the callback that is invoked when an edge should be removed.
    /// </summary>
    [Parameter]
    public EventCallback<ReadOnlyEdge> OnEdgeRemoved { get; set; }

    /// <summary>
    /// Function that returns internal connection descriptions for a port (builder mode).
    /// Each entry is (sourceSubJobId, sourcePortName) from factory internal edges.
    /// When null, only real Edge connections are shown.
    /// </summary>
    [Parameter]
    public Func<string, List<(int sourceJobId, string sourcePortName)>> GetInternalConnections { get; set; }

    /// <summary>
    /// Whether to show exposure toggle buttons on input ports (builder mode).
    /// </summary>
    [Parameter]
    public bool ShowExposureToggle { get; set; } = false;

    /// <summary>
    /// Function to check if an input port is currently exposed.
    /// </summary>
    [Parameter]
    public Func<ReadOnlyPortIn, bool> IsPortExposed { get; set; }

    /// <summary>
    /// Callback invoked when an input port's exposure is toggled.
    /// </summary>
    [Parameter]
    public EventCallback<(ReadOnlyPortIn port, bool exposed)> OnExposureToggled { get; set; }

    /// <summary>
    /// Function to get the custom name for an exposed input port.
    /// </summary>
    [Parameter]
    public Func<ReadOnlyPortIn, string> GetExposedPortName { get; set; }

    /// <summary>
    /// Callback invoked when an exposed input port's custom name changes.
    /// </summary>
    [Parameter]
    public EventCallback<(ReadOnlyPortIn port, string name)> OnExposedPortNameChanged { get; set; }

    /// <summary>
    /// Callback invoked when an internal connection should be removed (builder mode).
    /// Parameters: (portName, sourceJobId, sourcePortName).
    /// </summary>
    [Parameter]
    public EventCallback<(string portName, int sourceJobId, string sourcePortName)> OnInternalEdgeRemoved { get; set; }

    /// <summary>
    /// Function that returns external connection descriptions for a port (builder mode).
    /// Each entry is (externalJobId, externalPortName) from factory external edges.
    /// When null, external connections are not shown.
    /// </summary>
    [Parameter]
    public Func<string, List<(int externalJobId, string externalPort)>> GetExternalConnections { get; set; }

    /// <summary>
    /// Callback invoked when an external connection should be removed (builder mode).
    /// Parameters: (portName, externalJobId, externalPort).
    /// </summary>
    [Parameter]
    public EventCallback<(string portName, int externalJobId, string externalPort)> OnExternalEdgeRemoved { get; set; }

    /// <summary>
    /// Icon for the delete edge button.
    /// </summary>
    private Icon iconDeleteEdge = new Icons.Filled.Size16.Delete();
    private Icon iconExposedFilled = new Icons.Filled.Size16.ArrowCircleUpRight();
    private Icon iconExposedRegular = new Icons.Regular.Size16.ArrowCircleUpRight();

    /// <summary>
    /// Gets a user-friendly text representation of a port's connection requirements.
    /// </summary>
    /// <param name="port">The input port</param>
    /// <returns>A string describing how many connections the port requires</returns>
    private string GetPortRequirementText(ReadOnlyPortIn port)
    {
        if (port.MaxItems == int.MaxValue)
            return $"{port.MinItems}+";
        
        if (port.MinItems == port.MaxItems)
            return port.MinItems.ToString();
        
        return $"{port.MinItems}–{port.MaxItems}";
    }

    /// <summary>
    /// Handles edge removal requests and propagates them to the parent component.
    /// </summary>
    /// <param name="edge">The edge to remove</param>
    private async Task HandleEdgeRemoved(ReadOnlyEdge edge)
    {
        await OnEdgeRemoved.InvokeAsync(edge);
    }
}