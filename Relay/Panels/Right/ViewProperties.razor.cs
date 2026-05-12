using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components.FileBrowser;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Panels.Right;

/// <summary>
/// A component that displays properties for the currently selected view(s) in the right panel.
/// </summary>
/// <remarks>
/// This component shows information about one or more selected views, including:
/// - Basic metadata (name, creation date, author)
/// - Notes and description
/// - Custom emoji icon
/// 
/// It allows editing of view properties and provides validation for user input.
/// Views represent different arrangements or subsets of jobs within a space.
/// </remarks>
public partial class ViewProperties : ComponentBase, IDisposable
{
    /// <summary>
    /// Gets or sets the collection of views to display properties for.
    /// </summary>
    /// <remarks>
    /// The component can show properties for multiple selected views, though editing
    /// is only available when a single view is selected.
    /// </remarks>
    [Parameter]
    public IEnumerable<ReadOnlyView> Views { get; set; }

    /// <summary>
    /// Gets or sets the data manager service for updating views.
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

    /// <summary>
    /// Subscriptions to view update events, used to refresh the display when views change.
    /// </summary>
    private List<GroupEventSubscription> _subscriptions = new();
    
    /// <summary>
    /// Validation error message for the view alias field.
    /// </summary>
    private string _aliasValidationError;

    /// <summary>
    /// Sets up subscriptions for view updates when parameters change.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        
        // Clean up old subscriptions
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();
        
        // Set up new subscriptions for view updates
        if (Views != null)
            foreach (var view in Views)
                _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(view.Space.Project.Id, view.Space.Id, view.Id),
                                                               async (_) => await InvokeAsync(StateHasChanged)));
    }

    /// <summary>
    /// Validates a view alias (name) for format and uniqueness within its space.
    /// </summary>
    /// <param name="view">The view being edited</param>
    /// <param name="newAlias">The proposed new alias</param>
    /// <returns>An error message, or empty string if validation passes</returns>
    private string ValidateViewAlias(ReadOnlyView view, string newAlias)
    {
        if (string.IsNullOrWhiteSpace(newAlias))
            return "View name is required";
            
        if (newAlias.Length < 3)
            return "View name must be at least 3 characters long";
            
        if (newAlias.Length > 150)
            return "View name cannot be longer than 150 characters";

        // Check for duplicates within the same space, excluding the current view
        if (view.Space.Views.Any(v => v.Id != view.Id && 
                                    v.Alias.Equals(newAlias, StringComparison.OrdinalIgnoreCase)))
            return "A view with this name already exists in this space";

        return string.Empty;
    }

    /// <summary>
    /// Updates a view's alias when changed in the UI, with validation.
    /// </summary>
    /// <param name="value">The new alias</param>
    private async Task HandleViewAliasChanged(string value)
    {
        var view = Views.First();
        _aliasValidationError = ValidateViewAlias(view, value);
        
        if (string.IsNullOrEmpty(_aliasValidationError))
        {
            await DataManager.UpdateView(Session.User, view, originalView =>
            {
                originalView.Alias = value;
            });
        }
        else
            await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Updates a view's emoji icon when changed in the UI.
    /// </summary>
    /// <param name="glyph">The new emoji glyph</param>
    private async Task HandleViewEmojiChanged(string glyph)
    {
        await DataManager.UpdateView(Session.User, Views.First(), originalView =>
        {
            originalView.HeroImage = glyph;
        });
    }

    /// <summary>
    /// Updates a view's notes when changed in the UI.
    /// </summary>
    /// <param name="value">The new notes</param>
    private async Task HandleViewNotesChanged(string value)
    {
        await DataManager.UpdateView(Session.User, Views.First(), originalView =>
        {
            originalView.Notes = value;
        });
    }

    /// <summary>
    /// Copies the space's root directory path to the clipboard.
    /// </summary>
    private async Task HandlePathCopyClicked()
    {
        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", Views.First().Space.RootDirectory);
        ToastService.ShowSuccess("Path copied to clipboard", timeout: 1000);
    }

    private async Task HandleBrowseFolderClicked()
    {
        await FileBrowserDialog.Show(
            DialogService,
            this,
            _ => Task.CompletedTask,
            "Browse Files",
            currentFolder: Views.First().Space.RootDirectory,
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