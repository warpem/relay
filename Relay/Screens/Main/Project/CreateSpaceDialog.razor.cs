using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using EmojiInfo = Relay.Emoji.EmojiInfo;

namespace Relay.Screens.Main.Project;

/// <summary>
/// Dialog component for creating a new space within a project or reconnecting an existing space.
/// Used by ProjectScreen to allow users to create spaces with customized properties or
/// reconnect previously disconnected spaces.
/// </summary>
public partial class CreateSpaceDialog : IDialogContentComponent<CreateSpaceDialogParameters>
{
    /// <summary>
    /// The current Relay session containing user, project, and navigation state.
    /// </summary>
    [Inject]
    public RelaySession Session { get; set; }
    
    /// <summary>
    /// Data manager service for creating spaces and views.
    /// </summary>
    [Inject]
    public DataManager DataManager { get; set; }
    
    /// <summary>
    /// Reference to the FluentUI dialog that hosts this component.
    /// </summary>
    [CascadingParameter] 
    public FluentDialog Dialog { get; set; }
    
    /// <summary>
    /// Parameters passed to the dialog when it's shown.
    /// </summary>
    [Parameter]
    public CreateSpaceDialogParameters Content { get; set; }
    
    /// <summary>
    /// Model containing the form data for creating a new space or reconnecting an existing one.
    /// </summary>
    private CreateSpaceModel Model { get; set; } = new();
    private EditContext FormContext;
    
    /// <summary>
    /// Error message to display when space creation or reconnection fails.
    /// </summary>
    private string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// Track the current reconnect mode to detect changes
    /// </summary>
    private bool _previousReconnectMode = false;
    
    /// <summary>
    /// Track the current already connected state to detect changes
    /// </summary>
    private bool _previousAlreadyConnected = false;

    protected override void OnInitialized()
    {
        FormContext = new EditContext(Model);
        base.OnInitialized();
    }

    /// <summary>
    /// Monitor for directory changes to update UI accordingly
    /// </summary>
    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        
        if (_previousReconnectMode != Model.IsReconnectMode || 
            _previousAlreadyConnected != Model.IsAlreadyConnected)
        {
            _previousReconnectMode = Model.IsReconnectMode;
            _previousAlreadyConnected = Model.IsAlreadyConnected;
            
            if (Dialog != null)
            {
                // if (Model.IsReconnectMode)
                // {
                //     Dialog.Parameters.Title = "Reconnect existing space";
                // }
                // else if (Model.IsAlreadyConnected)
                // {
                //     Dialog.Parameters.Title = "Space already connected";
                // }
                // else
                // {
                //     Dialog.Parameters.Title = "Create new space";
                // }
                
                StateHasChanged();
            }
        }
    }

    private async Task OnFormSubmit()
    {
        if (Model.IsReconnectMode)
            await OnReconnectClick();
        else
            await OnCreateClick();
    }
    
    /// <summary>
    /// Handles the Create button click by creating a new space with the specified properties,
    /// then automatically creates an initial view within that space, and finally closes the dialog.
    /// </summary>
    private async Task OnCreateClick()
    {
        try
        {
            var space = await DataManager.CreateSpace(Session.User, Session.Project, new Refund.DataModel.Space
            {
                Alias = Model.Name,
                RootDirectory = Model.Directory,
                HeroImage = Model.HeroImage,
                Notes = Model.Notes
            });

            var firstView = await DataManager.CreateView(Session.User, space, new Refund.DataModel.View
            {
                Alias = "View 1",
                HeroImage = "🪟"
            });
            
            await Dialog.CloseAsync(new CreateSpaceDialogResult 
            { 
                SpaceId = space.Id,
                Success = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create space: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Handles the Reconnect button click by reconnecting an existing space to the current project.
    /// </summary>
    private async Task OnReconnectClick()
    {
        try
        {
            // This will be implemented in DataManager
            var space = await ReconnectSpace();
            
            await Dialog.CloseAsync(new CreateSpaceDialogResult 
            { 
                SpaceId = space.Id,
                Success = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to reconnect space: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Reconnects an existing space from disk to the current project.
    /// </summary>
    private async Task<Refund.DataModel.ReadOnly.ReadOnlySpace> ReconnectSpace()
    {
        // Call DataManager to reconnect the space
        return await DataManager.ReconnectSpace(
            Session.User, 
            Session.Project, 
            Model.ExistingSpacePath);
    }
    
    /// <summary>
    /// Handles the Cancel button click by closing the dialog without creating or reconnecting a space.
    /// </summary>
    private async Task OnCancelClick()
    {
        await Dialog.CancelAsync();
    }
    
    /// <summary>
    /// Shows the Create/Reconnect Space dialog and sets up the callback for when it's closed.
    /// 
    /// Used by ProjectScreen to display this dialog when the user clicks the "Create new space" button.
    /// </summary>
    /// <param name="dialogService">The FluentUI dialog service to show the dialog</param>
    /// <param name="callbackReceiver">The component that will receive the dialog result callback</param>
    /// <param name="callbackHandler">The method to call when the dialog is closed</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public static async Task Show(IDialogService dialogService,
                                  object callbackReceiver,
                                  Func<DialogResult, Task> callbackHandler)
    {
        var parameters = new CreateSpaceDialogParameters();
        
        await dialogService.ShowDialogAsync<CreateSpaceDialog>(parameters,
                                                               new DialogParameters
                                                               {
                                                                   Title = "Create new space",
                                                                   Modal = true,
                                                                   Width = "1050px",
                                                                   PreventScroll = true,
                                                                   ShowDismiss = true,
                                                                   OnDialogResult = dialogService.CreateDialogCallback(callbackReceiver, callbackHandler)
                                                               });
    }
}

/// <summary>
/// Parameters class for the CreateSpaceDialog component.
/// Currently empty but allows for future extensibility.
/// </summary>
public class CreateSpaceDialogParameters
{
}

/// <summary>
/// Result object returned when the Create Space dialog is closed.
/// Used by ProjectScreen to navigate to the newly created space upon successful creation.
/// </summary>
public class CreateSpaceDialogResult
{
    /// <summary>
    /// Indicates whether the space was successfully created.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// The ID of the newly created space. Used for navigation after creation.
    /// </summary>
    public int SpaceId { get; set; }
}