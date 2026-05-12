using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Panels.Right;

/// <summary>
/// The main container component for the right panel of the application.
/// </summary>
/// <remarks>
/// This component serves as the host for all right panel content. It determines which
/// content to display based on the current selection context and job editor state.
/// 
/// The component dynamically switches between different property panels based on what
/// type of object is selected (project, space, view, or job), or shows the job editor
/// when a job is being configured.
/// 
/// It handles the visual structure of the panel, including title bar, resize functionality,
/// and content transitions.
/// </remarks>
public partial class RightDrawer : ComponentBase, IDisposable
{
    /// <summary>
    /// Gets or sets the session service for the current user and selection context.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }
    
    /// <summary>
    /// Gets or sets the data manager service to manage data operations.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }
    
    /// <summary>
    /// Gets or sets the card selection service for tracking selected objects.
    /// </summary>
    [Inject]
    private CardSelectionService Selection { get; set; }
    
    /// <summary>
    /// Gets or sets the job editor service for job parameter editing.
    /// </summary>
    [Inject]
    private JobEditorService JobEditor { get; set; }

    /// <summary>
    /// Gets or sets the factory editor service for factory instance parameter editing.
    /// </summary>
    [Inject]
    private FactoryEditorService FactoryEditor { get; set; }

    /// <summary>
    /// Sets up event subscriptions when the component initializes.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        // Subscribe to events that should trigger a UI refresh
        Session.OnStateChanged += HandleStateChanged;
        Session.OnFolderChanged += HandleStateChanged;
        Selection.OnSelectionChanged += HandleSelectionChanged;
        JobEditor.OnJobChanged += HandleEditorJobChanged;
        FactoryEditor.OnInstanceChanged += HandleEditorInstanceChanged;
    }

    /// <summary>
    /// Handles session state change events.
    /// </summary>
    /// <remarks>
    /// Session state includes the current project, space, view, and user.
    /// Changes to these values affect what is displayed in the panel.
    /// </remarks>
    private async Task HandleStateChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles selection change events.
    /// </summary>
    /// <remarks>
    /// This is triggered when the user selects different jobs or other objects,
    /// which affects what properties are displayed.
    /// </remarks>
    private async Task HandleSelectionChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles job editor state change events.
    /// </summary>
    /// <param name="job">The job being edited, or null if editing is complete</param>
    /// <remarks>
    /// When a job is being edited, the panel switches to the job editor view.
    /// When editing completes, it returns to the previously displayed properties.
    /// </remarks>
    private async Task HandleEditorJobChanged(ReadOnlyJob job)
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleEditorInstanceChanged(ReadOnlyFactoryInstance instance)
    {
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Cleans up event subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        Session.OnStateChanged -= HandleStateChanged;
        Session.OnFolderChanged -= HandleStateChanged;
        Selection.OnSelectionChanged -= HandleSelectionChanged;
        JobEditor.OnJobChanged -= HandleEditorJobChanged;
        FactoryEditor.OnInstanceChanged -= HandleEditorInstanceChanged;
    }
}