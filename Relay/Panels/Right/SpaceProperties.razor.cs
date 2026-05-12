using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.FileBrowser;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Panels.Right;

/// <summary>
/// A component that displays properties for the currently selected space(s) in the right panel.
/// </summary>
/// <remarks>
/// This component shows information about one or more selected spaces, including:
/// - Basic metadata (name, creation date, author)
/// - File system location
/// - Notes and description
/// - Custom emoji icon
/// 
/// It allows editing of space properties and provides validation for user input.
/// </remarks>
public partial class SpaceProperties : ComponentBase, IDisposable
{
    /// <summary>
    /// Gets or sets the collection of spaces to display properties for.
    /// </summary>
    /// <remarks>
    /// The component can show properties for multiple selected spaces, though editing
    /// is only available when a single space is selected.
    /// </remarks>
    [Parameter]
    public IEnumerable<ReadOnlySpace> Spaces { get; set; }

    /// <summary>
    /// Gets or sets the data manager service for updating spaces.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }
    
    /// <summary>
    /// Gets or sets the session service for the current user context.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }
    
    /// <summary>
    /// Gets or sets the JavaScript runtime for clipboard operations.
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; }
    
    /// <summary>
    /// Gets or sets the toast service for showing notifications.
    /// </summary>
    [Inject]
    private IToastService ToastService { get; set; }
    
    [Inject]
    private IDialogService DialogService { get; set; }

    [Inject]
    private MenuActionService MenuActions { get; set; }

    /// <summary>
    /// Subscriptions to space update events, used to refresh the display when spaces change.
    /// </summary>
    private List<GroupEventSubscription> _subscriptions = new();
    
    /// <summary>
    /// Validation error message for the space alias field.
    /// </summary>
    private string _aliasValidationError;

    /// <summary>
    /// Sets up subscriptions for space updates when parameters change.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        
        // Clean up old subscriptions
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
        
        // Set up new subscriptions for space updates
        if (Spaces != null)
            foreach (var space in Spaces)
                _subscriptions.Add(DataManager.SpaceUpdated.Add(GroupName.Space(space.Project.Id, space.Id),
                                                                async (_) => await InvokeAsync(StateHasChanged)));
    }
    
    /// <summary>
    /// Validates a space alias (name) for format and uniqueness.
    /// </summary>
    /// <param name="space">The space being edited</param>
    /// <param name="newAlias">The proposed new alias</param>
    /// <returns>An error message, or empty string if validation passes</returns>
    private string ValidateSpaceAlias(ReadOnlySpace space, string newAlias)
    {
        if (string.IsNullOrWhiteSpace(newAlias))
            return "Space name is required";
            
        if (newAlias.Length < 3)
            return "Space name must be at least 3 characters long";
            
        if (newAlias.Length > 150)
            return "Space name cannot be longer than 150 characters";

        // Check for duplicates, excluding the current space
        if (Session.Project.Spaces.Any(s => s.Id != space.Id && 
                                          s.Alias.Equals(newAlias, StringComparison.OrdinalIgnoreCase)))
            return "A space with this name already exists";

        return string.Empty;
    }

    /// <summary>
    /// Updates a space's alias when changed in the UI, with validation.
    /// </summary>
    /// <param name="value">The new alias</param>
    private async Task HandleSpaceAliasChanged(string value)
    {
        var space = Spaces.First();
        _aliasValidationError = ValidateSpaceAlias(space, value);
        
        if (string.IsNullOrEmpty(_aliasValidationError))
        {
            await DataManager.UpdateSpace(Session.User, space, originalSpace =>
            {
                originalSpace.Alias = value;
            });
        }
        else
            await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Updates a space's emoji icon when changed in the UI.
    /// </summary>
    /// <param name="glyph">The new emoji glyph</param>
    private async Task HandleSpaceEmojiChanged(string glyph)
    {
        await DataManager.UpdateSpace(Session.User, Spaces.First(), originalSpace =>
        {
            originalSpace.HeroImage = glyph;
        });
    }

    /// <summary>
    /// Updates a space's notes when changed in the UI.
    /// </summary>
    /// <param name="value">The new notes</param>
    private async Task HandleSpaceNotesChanged(string value)
    {
        await DataManager.UpdateSpace(Session.User, Spaces.First(), originalSpace =>
        {
            originalSpace.Notes = value;
        });
    }

    /// <summary>
    /// Copies the space's root directory path to the clipboard.
    /// </summary>
    private async Task HandlePathCopyClicked()
    {
        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", Spaces.First().RootDirectory);
        ToastService.ShowSuccess("Path copied to clipboard", timeout: 1000);
    }

    private async Task HandleBrowseFolderClicked()
    {
        await FileBrowserDialog.Show(
            DialogService,
            this,
            _ => Task.CompletedTask,
            "Browse Files",
            currentFolder: Spaces.First().RootDirectory,
            showSelectionButtons: false);
    }

    /// <summary>
    /// Cleans up subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
    }
}