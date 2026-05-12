using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using EmojiInfo = Relay.Emoji.EmojiInfo;

namespace Relay.Screens.Main.Home;

/// <summary>
/// Dialog component for creating a new project in the application.
/// Used by HomeScreen to create projects that will be immediately navigated to.
/// </summary>
public partial class CreateProjectDialog : IDialogContentComponent<CreateProjectDialogParameters>
{
    /// <summary>
    /// The current relay session providing user context and navigation capabilities.
    /// </summary>
    [Inject]
    public RelaySession Session { get; set; }
    
    /// <summary>
    /// Data manager for creating new project entities.
    /// </summary>
    [Inject]
    public DataManager DataManager { get; set; }
    
    /// <summary>
    /// Reference to the parent dialog instance.
    /// </summary>
    [CascadingParameter] 
    public FluentDialog Dialog { get; set; }
    
    /// <summary>
    /// Dialog parameters (currently empty but maintained for pattern consistency).
    /// </summary>
    [Parameter]
    public CreateProjectDialogParameters Content { get; set; }
    
    /// <summary>
    /// The model containing project creation fields with validation.
    /// </summary>
    private CreateProjectModel Model { get; set; } = new();
    
    /// <summary>
    /// Error message displayed when project creation fails.
    /// </summary>
    private string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// Handles the create button click event.
    /// Creates a new project and closes the dialog with success result.
    /// </summary>
    private async Task OnCreateClick()
    {
        try
        {
            var project = await DataManager.CreateProject(Session.User, new Refund.DataModel.Project
            {
                Alias = Model.Name,
                HeroImage = Model.HeroImage,
                Notes = Model.Notes
            });
            
            await Dialog.CloseAsync(new CreateProjectDialogResult 
            { 
                ProjectId = project.Id,
                Success = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create project: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Handles the cancel button click event.
    /// Closes the dialog without creating a project.
    /// </summary>
    private async Task OnCancelClick()
    {
        await Dialog.CancelAsync();
    }
    
    /// <summary>
    /// Shows the create project dialog.
    /// Primarily called from HomeScreen.ShowCreateDialogAsync().
    /// </summary>
    /// <param name="dialogService">The dialog service to show the dialog</param>
    /// <param name="callbackReceiver">The component that will receive the dialog result callback</param>
    /// <param name="callbackHandler">The callback method to process the dialog result, typically OnCreateDialogClosedAsync</param>
    /// <returns>A task representing the asynchronous dialog operation</returns>
    public static async Task Show(IDialogService dialogService,
                                  object callbackReceiver,
                                  Func<DialogResult, Task> callbackHandler)
    {
        var parameters = new CreateProjectDialogParameters();

        await dialogService.ShowDialogAsync<CreateProjectDialog>(parameters,
                                                                 new DialogParameters
                                                                 {
                                                                     Title = "Create new project",
                                                                     Modal = true,
                                                                     PreventScroll = true,
                                                                     ShowDismiss = true,
                                                                     OnDialogResult = dialogService.CreateDialogCallback(callbackReceiver, callbackHandler)
                                                                 });
    }
}

/// <summary>
/// Parameters for the CreateProjectDialog.
/// Currently empty but maintained for future extensibility.
/// </summary>
public class CreateProjectDialogParameters
{
}

/// <summary>
/// Result returned by the CreateProjectDialog when closed.
/// Used in HomeScreen.OnCreateDialogClosedAsync to handle successful project creation.
/// </summary>
public class CreateProjectDialogResult
{
    /// <summary>
    /// Indicates whether the project was successfully created.
    /// HomeScreen checks this property to determine whether to navigate to the new project.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// The ID of the newly created project.
    /// Used in HomeScreen to navigate to the project when Success is true.
    /// </summary>
    public int ProjectId { get; set; }
}