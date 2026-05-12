using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay.Emoji;

namespace Relay.Panels.Right;

/// <summary>
/// A component that displays properties for the currently selected project(s) in the right panel.
/// </summary>
/// <remarks>
/// This component shows information about one or more selected projects, including:
/// - Basic metadata (name, creation date, owner)
/// - Team members with permission management
/// - Notes and description
/// - Custom emoji icon
/// 
/// It allows editing of project properties, adding and removing project members,
/// and provides validation for user input.
/// </remarks>
public partial class ProjectProperties : ComponentBase, IDisposable
{
    /// <summary>
    /// Gets or sets the collection of projects to display properties for.
    /// </summary>
    /// <remarks>
    /// The component can show properties for multiple selected projects, though editing
    /// is only available when a single project is selected.
    /// </remarks>
    [Parameter]
    public IEnumerable<ReadOnlyProject> Projects { get; set; }

    /// <summary>
    /// Gets or sets the data manager service for updating projects.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }
    
    /// <summary>
    /// Gets or sets the session service for the current user context.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }
    
    /// <summary>
    /// Gets or sets the toast service for showing notifications.
    /// </summary>
    [Inject]
    private IToastService ToastService { get; set; }

    /// <summary>
    /// Subscriptions to project update events, used to refresh the display when projects change.
    /// </summary>
    private List<GroupEventSubscription> _subscriptions = new();
    
    /// <summary>
    /// Validation error message for the project alias field.
    /// </summary>
    private string _aliasValidationError;
    
    /// <summary>
    /// Whether the user menu for adding project members is currently open.
    /// </summary>
    private bool _isUserMenuOpen;

    /// <summary>
    /// Sets up subscriptions for project updates when the component initializes.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();

        // Subscribe to project updates and deletions to refresh the UI
        _subscriptions.Add(DataManager.ProjectUpdated.Add(GroupName.Project(null),
                                                          async (_) => await InvokeAsync(StateHasChanged)));
        _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(null),
                                                          async (_) => await InvokeAsync(StateHasChanged)));
    }
    
    /// <summary>
    /// Validates a project alias (name) for format and uniqueness.
    /// </summary>
    /// <param name="project">The project being edited</param>
    /// <param name="newAlias">The proposed new alias</param>
    /// <returns>An error message, or empty string if validation passes</returns>
    private string ValidateProjectAlias(ReadOnlyProject project, string newAlias)
    {
        if (string.IsNullOrWhiteSpace(newAlias))
            return "Project name is required";
            
        if (newAlias.Length < 3)
            return "Project name must be at least 3 characters long";
            
        if (newAlias.Length > 150)
            return "Project name cannot be longer than 150 characters";

        // Check for duplicates, excluding the current project
        if (DataManager.Projects.Any(p => p.Id != project.Id && 
                                          p.Alias.Equals(newAlias, StringComparison.OrdinalIgnoreCase)))
            return "A project with this name already exists";

        return string.Empty;
    }

    /// <summary>
    /// Updates a project's alias when changed in the UI, with validation.
    /// </summary>
    /// <param name="value">The new alias</param>
    private async Task HandleProjectAliasChanged(string value)
    {
        var project = Projects.First();
        _aliasValidationError = ValidateProjectAlias(project, value);
        
        if (string.IsNullOrEmpty(_aliasValidationError))
        {
            await DataManager.UpdateProject(Session.User, project, originalProject =>
            {
                originalProject.Alias = value;
            });
        }
        else
            await InvokeAsync(StateHasChanged);
    }
    
    /// <summary>
    /// Updates a project's emoji icon when changed in the UI.
    /// </summary>
    /// <param name="glyph">The new emoji glyph</param>
    private async Task HandleProjectEmojiChanged(string glyph)
    {
        await DataManager.UpdateProject(Session.User, Projects.First(), originalProject =>
        {
            originalProject.HeroImage = glyph;
        });
    }

    /// <summary>
    /// Updates a project's notes when changed in the UI.
    /// </summary>
    /// <param name="value">The new notes</param>
    private async Task HandleProjectNotesChanged(string value)
    {
        await DataManager.UpdateProject(Session.User, Projects.First(), originalProject =>
        {
            originalProject.Notes = value;
        });
    }

    /// <summary>
    /// Removes a user from the project's team.
    /// </summary>
    /// <param name="member">The user to remove</param>
    private async Task HandleMemberDismissed(ReadOnlyUser member)
    {
        await DataManager.RemoveProjectMember(Session.User, Projects.First(), member);
        ToastService.ShowSuccess($"{member.Name} removed from project");
    }

    /// <summary>
    /// Adds a user to the project's team by their ID.
    /// </summary>
    /// <param name="id">The ID of the user to add</param>
    private async Task HandleMemberAdded(int id)
    {
        _isUserMenuOpen = false;
        
        try
        {
            ReadOnlyUser member = DataManager.FindUser(id);
            if (member == null)
                throw new Exception($"Couldn't find user with ID {id}");

            await DataManager.AddProjectMember(Session.User, Projects.First(), member);
            
            ToastService.ShowSuccess($"{member.Name} added to project");
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't add project member: {exc.Message}");
        }
    }

    /// <summary>
    /// Opens the user selection menu for adding project members.
    /// </summary>
    private async Task ShowUserMenu()
    {
        _isUserMenuOpen = true;
        await InvokeAsync(StateHasChanged);
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