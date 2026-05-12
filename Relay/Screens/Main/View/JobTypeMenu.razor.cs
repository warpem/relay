using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel.ReadOnly;
using Refund.Services;

namespace Relay.Screens.Main.View;

/// <summary>
/// A context menu component that displays job types or port connections based on the current menu context.
/// Used in the ViewScreen for creating new jobs and connecting ports between jobs.
/// </summary>
public partial class JobTypeMenu : ComponentBase, IAsyncDisposable
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
    private MenuType _type;
    
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

    /// <summary>
    /// The horizontal position of the menu relative to its anchor.
    /// </summary>
    [Parameter]
    public HorizontalPosition HorizontalPosition { get; set; } = HorizontalPosition.Right;

    /// <summary>
    /// The vertical position of the menu relative to its anchor.
    /// </summary>
    [Parameter]
    public VerticalPosition VerticalPosition { get; set; } = VerticalPosition.Bottom;

    /// <summary>
    /// Gets or sets the width of the menu.
    /// </summary>
    [Parameter]
    public string Width { get; set; } = "300px";

    /// <summary>
    /// Additional CSS styles to apply to the menu.
    /// </summary>
    [Parameter]
    public string Style { get; set; } = string.Empty;

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
    /// Event callback that is invoked when the mouse leaves the menu area.
    /// </summary>
    [Parameter]
    public EventCallback OnMouseLeave { get; set; }
    
    /// <summary>
    /// The job editor service used to interact with the current job being edited.
    /// </summary>
    [Inject]
    public JobEditorService JobEditor { get; set; }
    
    /// <summary>
    /// JavaScript runtime for interacting with browser APIs.
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; }

    private IJSObjectReference _module;
    private IJSObjectReference _clickOutsideHandler;
    private DotNetObjectReference<JobTypeMenu> _dotNetRef;

    private FluentMenu _menu;

    /// <summary>
    /// Called when component parameters are set. Updates the local state if the menu type has changed.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (Type != _type)
        {
            _type = Type;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Called after the component has been rendered. Initializes JavaScript interop for click-outside detection.
    /// </summary>
    /// <param name="firstRender">True if this is the first time the component has been rendered.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Load the JS module
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Screens/Main/View/JobTypeMenu.razor.js");
            _dotNetRef = DotNetObjectReference.Create(this);
                
            // Initialize the click outside handler
            _clickOutsideHandler = await _module.InvokeAsync<IJSObjectReference>("initialize", 
                                                                                 _dotNetRef,
                                                                                 "fluent-menu, fluent-menu-item");
        }
    }

    /// <summary>
    /// Handles clicks outside the menu component, closing the menu if it's open.
    /// This method is called from JavaScript.
    /// </summary>
    [JSInvokable]
    public async Task HandleClickOutside()
    {
        if (Open)
        {
            await _menu.CloseAsync();
        }
    }

    /// <summary>
    /// Disposes of JavaScript interop resources when the component is removed from the UI.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_clickOutsideHandler != null)
            {
                await _clickOutsideHandler.InvokeVoidAsync("dispose");
                await _clickOutsideHandler.DisposeAsync();
            }

            if (_module != null)
            {
                await _module.DisposeAsync();
            }

            _dotNetRef?.Dispose();
        }
        catch { }
    }
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