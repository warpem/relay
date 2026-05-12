using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Screens.Main.Space;

/// <summary>
/// Dialog component for creating new views within a space.
/// 
/// Used in the SpaceScreen to initiate view creation. The dialog collects
/// essential view information including name, emoji, and notes, validates 
/// the inputs, and creates a new view upon confirmation.
/// </summary>
public partial class CreateViewDialog : IDialogContentComponent<CreateViewDialogParameters>
{
    /// <summary>
    /// Current session information including the user and current space context.
    /// </summary>
    [Inject]
    public RelaySession Session { get; set; }
    
    /// <summary>
    /// Data manager for performing CRUD operations on views.
    /// </summary>
    [Inject]
    public DataManager DataManager { get; set; }
    
    /// <summary>
    /// Reference to the dialog component that hosts this content.
    /// </summary>
    [CascadingParameter] 
    public FluentDialog Dialog { get; set; }
    
    /// <summary>
    /// Input parameters for the dialog. Currently not used but preserved
    /// for future extensibility.
    /// </summary>
    [Parameter]
    public CreateViewDialogParameters Content { get; set; }
    
    /// <summary>
    /// View model containing the form data with validation attributes.
    /// </summary>
    private CreateViewModel Model { get; set; } = new();
    
    /// <summary>
    /// Error message displayed to the user if view creation fails.
    /// </summary>
    private string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// Handles the creation of a new view when the user confirms the dialog.
    /// Creates a view in the current space using the DataManager and returns
    /// the result to the calling component.
    /// </summary>
    private async Task OnCreateClick()
    {
        try
        {
            var view = await DataManager.CreateView(Session.User, Session.Space, new Refund.DataModel.View
            {
                Alias = Model.Name,
                HeroImage = Model.HeroImage,
                Notes = Model.Notes
            });
            
            await Dialog.CloseAsync(new CreateViewDialogResult 
            { 
                ViewId = view.Id,
                Success = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create view: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Handles dialog cancellation, dismissing the dialog without creating a view.
    /// </summary>
    private async Task OnCancelClick()
    {
        await Dialog.CancelAsync();
    }
    
    /// <summary>
    /// Shows the create view dialog and sets up the callback handling.
    /// 
    /// This static method is called from SpaceScreen when the user clicks 
    /// the "Create new view" button, providing a consistent way to display
    /// the dialog and handle its result.
    /// </summary>
    /// <param name="dialogService">Service for displaying dialogs</param>
    /// <param name="callbackReceiver">Component that will receive the callback</param>
    /// <param name="callbackHandler">Method to handle the dialog result</param>
    public static async Task Show(
        IDialogService dialogService,
        object callbackReceiver,
        Func<DialogResult, Task> callbackHandler)
    {
        var parameters = new CreateViewDialogParameters();
        
        await dialogService.ShowDialogAsync<CreateViewDialog>(
            parameters,
            new DialogParameters
            {
                Title = "Create new view",
                Modal = true,
                PreventScroll = true,
                ShowDismiss = true,
                OnDialogResult = dialogService.CreateDialogCallback(callbackReceiver, callbackHandler)
            });
    }
}

/// <summary>
/// Parameters for the CreateViewDialog.
/// Currently empty but maintained for future extensibility.
/// </summary>
public class CreateViewDialogParameters
{
}

/// <summary>
/// Result returned when the view creation dialog is closed.
/// Contains the ID of the newly created view and a success flag.
/// 
/// Used in SpaceScreen.OnCreateDialogClosedAsync to navigate to 
/// the newly created view when Success is true.
/// </summary>
public class CreateViewDialogResult
{
    /// <summary>
    /// Indicates whether the view was successfully created.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// ID of the newly created view, used for navigation after creation.
    /// </summary>
    public int ViewId { get; set; }
}