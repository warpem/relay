using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;

namespace Relay.Screens.Main.View;

/// <summary>
/// A context menu component that displays job types or port connections based on the current menu context.
/// Used in the ViewScreen for creating new jobs and connecting ports between jobs.
/// </summary>
public partial class JobTypeMenu : ComponentBase
{
    /// <summary>
    /// Gets or sets whether the menu is open.
    /// </summary>
    [Parameter]
    public bool Open { get; set; }

    /// <summary>
    /// Event callback that is invoked when the Open property changes.
    /// </summary>
    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }
    
    /// <summary>
    /// Gets or sets the type of menu to display. 
    /// The menu behavior changes based on this property.
    /// </summary>
    [Parameter]
    public MenuType Type { get; set; }
    
    /// <summary>
    /// Gets or sets the port that was clicked to open this menu.
    /// Used when creating connections between ports or creating a job from a port.
    /// </summary>
    [Parameter]
    public ReadOnlyPortOut ClickedPort { get; set; }
    
    /// <summary>
    /// Filter function to determine which job types should be displayed in the menu.
    /// </summary>
    [Parameter]
    public Func<Type, bool> TypeFilter { get; set; }
    
    /// <summary>
    /// Filter function to determine which ports should be displayed in the menu.
    /// </summary>
    [Parameter]
    public Func<Type, bool> PortFilter { get; set; }

    /// <summary>
    /// Optional filter to exclude specific input ports from the ConnectToPort menu.
    /// Return true to include the port, false to exclude.
    /// </summary>
    [Parameter]
    public Func<ReadOnlyPortIn, bool> ConnectPortFilter { get; set; }

    /// <summary>
    /// HTML ID of the element to which the menu should be anchored.
    /// </summary>
    [Parameter]
    public string Anchor { get; set; }

    [Parameter] public double? X { get; set; }
    [Parameter] public double? Y { get; set; }
    [Parameter] public string Width { get; set; } = "300px";

    /// <summary>
    /// Event callback that is invoked when a job type is selected from the menu.
    /// </summary>
    [Parameter]
    public EventCallback<Type> OnTypeSelected { get; set; }

    /// <summary>
    /// Event callback that is invoked when a port is selected for connection from the menu.
    /// Returns the job type, output port, and input port to connect.
    /// </summary>
    [Parameter]
    public EventCallback<(Type jobType, ReadOnlyPortOut portOut, ReadOnlyPortIn portIn)> OnPortSelected { get; set; }
    
    /// <summary>
    /// Event callback that is invoked when a port connection is established from the menu.
    /// </summary>
    [Parameter]
    public EventCallback<(ReadOnlyPortOut portOut, ReadOnlyPortIn port)> OnPortConnected { get; set; }
    
    /// <summary>
    /// Event callback that is invoked when a folder creation is requested from the menu.
    /// </summary>
    [Parameter]
    public EventCallback OnFolderRequested { get; set; }

    /// <summary>
    /// Event callback that is invoked when a factory definition is selected for instantiation.
    /// </summary>
    [Parameter]
    public EventCallback<ReadOnlyFactoryDefinition> OnFactorySelected { get; set; }

    /// <summary>
    /// Factory definitions available for instantiation.
    /// </summary>
    [Parameter]
    public IEnumerable<ReadOnlyFactoryDefinition> Definitions { get; set; }

    /// <summary>
    /// The job editor service used to interact with the current job being edited.
    /// </summary>
    [Inject]
    public JobEditorService JobEditor { get; set; }
    

}

/// <summary>
/// Defines the different modes of operation for the JobTypeMenu component.
/// Used in ViewScreen to determine the appropriate menu behavior based on context.
/// </summary>
public enum MenuType
{
    /// <summary>
    /// Menu for creating a new job from a job type selection.
    /// </summary>
    CreateFromType,
    
    /// <summary>
    /// Menu for creating a new job that connects to a clicked output port.
    /// </summary>
    CreateFromPort,
    
    /// <summary>
    /// Menu for connecting an existing port to another job's port.
    /// Used during active job editing.
    /// </summary>
    ConnectToPort
}