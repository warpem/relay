using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.View;

namespace Relay.Panels.Top;

/// <summary>
/// Navigation breadcrumbs component that displays the current hierarchical position within the application
/// and allows quick navigation to different levels (Project, Space, View, Job).
/// Provides dropdown selectors for each level with lists of available items.
/// </summary>
public partial class Breadcrumbs : IDisposable
{
    /// <summary>
    /// Mapping of project IDs to their display names, for use in the project dropdown.
    /// </summary>
    private Dictionary<int, string> Projects { set; get; } = new();
    
    /// <summary>
    /// Mapping of space IDs to their display names, for use in the space dropdown.
    /// </summary>
    private Dictionary<int, string> Spaces { set; get; } = new();
    
    /// <summary>
    /// Mapping of view IDs to their display names, for use in the view dropdown.
    /// </summary>
    private Dictionary<int, string> Views { set; get; } = new();
    
    /// <summary>
    /// Mapping of job objects to their display names, for use in the job dropdown.
    /// Uses job objects as keys instead of IDs to maintain object references.
    /// </summary>
    private Dictionary<ReadOnlyJob, string> Jobs { set; get; } = new();

    /// <summary>
    /// Mapping of factory definition IDs to their display names, for use in the factory definition dropdown.
    /// </summary>
    private Dictionary<int, string> FactoryDefinitions { get; set; } = new();

    /// <summary>
    /// Mapping of factory instance IDs to their display names, for use in the factory instance dropdown.
    /// </summary>
    private Dictionary<int, string> FactoryInstances { get; set; } = new();

    /// <summary>
    /// Data manager service used to access project, space, view, and job information.
    /// </summary>
    [Inject]
    private DataManager DataManager { set; get; }
    
    /// <summary>
    /// Session service that maintains the current application state and navigation.
    /// </summary>
    [Inject]
    private RelaySession Session { set; get; }

    /// <summary>
    /// List of event subscriptions that need to be tracked for proper cleanup.
    /// </summary>
    private List<GroupEventSubscription> _subscriptions = new();

    /// <summary>
    /// CSS styles for the dropdown elements.
    /// </summary>
    private string GetStyles => "border-radius: 6px;border: 1px solid var(--Secondary-500, #2E77D0);background: var(--Color, #FFF);max-width: 175px;min-width: 175px;";

    /// <summary>
    /// Initializes the component, sets up event subscriptions, and loads initial data.
    /// </summary>
    /// <summary>
    /// The folder breadcrumb path from root to the current folder.
    /// </summary>
    private List<ReadOnlyFolder> FolderPath { get; set; } = new();

    protected override void OnInitialized()
    {
        Session.OnMainChanged += HandleMainChanged;
        Session.OnFolderChanged += HandleFolderChanged;
        Session.OnFactoryDefinitionChanged += HandleFactoryContextChanged;
        Session.OnFactoryInstanceChanged += HandleFactoryContextChanged;
        LoadData();
    }

    /// <summary>
    /// Handles changes to the main application state by updating event subscriptions.
    /// Sets up relevant event listeners based on the currently selected objects (Project, Space, View, Job).
    /// Uses a cascading approach where subscriptions are added only for the levels that are currently active.
    /// </summary>
    private async Task HandleMainChanged()
    {
        // Clear existing subscriptions
        foreach(var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();

        // Add subscriptions based on the current navigation context
        if (Session.Project != null)
        {
            _subscriptions.Add(DataManager.ProjectCreated.Add(GroupName.Project(null),
                                                              HandleProjectUpdated));
            _subscriptions.Add(DataManager.ProjectUpdated.Add(GroupName.Project(null),
                                                              HandleProjectUpdated));
            _subscriptions.Add(DataManager.ProjectDeleted.Add(GroupName.Project(null),
                                                              HandleProjectUpdated));

            if (Session.Space != null)
            {
                _subscriptions.Add(DataManager.SpaceCreated.Add(GroupName.Space(Session.Project.Id, null),
                                                                HandleSpaceUpdated));
                _subscriptions.Add(DataManager.SpaceUpdated.Add(GroupName.Space(Session.Project.Id, null),
                                                                HandleSpaceUpdated));
                _subscriptions.Add(DataManager.SpaceDeleted.Add(GroupName.Space(Session.Project.Id, null),
                                                                HandleSpaceUpdated));

                if (Session.View != null)
                {
                    _subscriptions.Add(DataManager.ViewCreated.Add(GroupName.View(Session.Project.Id, Session.Space.Id, null),
                                                                    HandleViewUpdated));
                    _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(Session.Project.Id, Session.Space.Id, null),
                                                                    HandleViewUpdated));
                    _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Session.Project.Id, Session.Space.Id, null),
                                                                    HandleViewUpdated));

                    if (Session.Job != null)
                    {
                        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Session.Project.Id, Session.Space.Id, null),
                                                                        HandleJobUpdated));
                        _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(Session.Project.Id, Session.Space.Id, null),
                                                                        HandleJobUpdated));
                        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Session.Project.Id, Session.Space.Id, null),
                                                                        HandleJobUpdated));
                    }
                }

                if (Session.FactoryDefinition != null)
                {
                    _subscriptions.Add(DataManager.FactoryDefinitionCreated.Add(
                        GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, null),
                        async _ => { LoadData(); await InvokeAsync(StateHasChanged); }));
                    _subscriptions.Add(DataManager.FactoryDefinitionUpdated.Add(
                        GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, null),
                        async _ => { LoadData(); await InvokeAsync(StateHasChanged); }));
                    _subscriptions.Add(DataManager.FactoryDefinitionDeleted.Add(
                        GroupName.FactoryDefinition(Session.Project.Id, Session.Space.Id, null),
                        async _ => { LoadData(); await InvokeAsync(StateHasChanged); }));
                }

                if (Session.FactoryInstance != null)
                {
                    _subscriptions.Add(DataManager.FactoryInstanceCreated.Add(
                        GroupName.FactoryInstance(Session.Project.Id, Session.Space.Id, null),
                        async _ => { LoadData(); await InvokeAsync(StateHasChanged); }));
                    _subscriptions.Add(DataManager.FactoryInstanceUpdated.Add(
                        GroupName.FactoryInstance(Session.Project.Id, Session.Space.Id, null),
                        async _ => { LoadData(); await InvokeAsync(StateHasChanged); }));
                    _subscriptions.Add(DataManager.FactoryInstanceDeleted.Add(
                        GroupName.FactoryInstance(Session.Project.Id, Session.Space.Id, null),
                        async _ => { LoadData(); await InvokeAsync(StateHasChanged); }));
                }
            }
        }

        // Refresh data and UI
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Event handler for project updates (creation, modification, deletion).
    /// Refreshes the breadcrumb data and UI.
    /// </summary>
    private async Task HandleProjectUpdated(GroupEventArgs<ReadOnlyProject> args)
    {
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Event handler for space updates (creation, modification, deletion).
    /// Refreshes the breadcrumb data and UI.
    /// </summary>
    private async Task HandleSpaceUpdated(GroupEventArgs<ReadOnlySpace> args)
    {
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Event handler for view updates (creation, modification, deletion).
    /// Refreshes the breadcrumb data and UI.
    /// </summary>
    private async Task HandleViewUpdated(GroupEventArgs<ReadOnlyView> args)
    {
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Event handler for job updates (creation, modification, deletion).
    /// Refreshes the breadcrumb data and UI.
    /// </summary>
    private async Task HandleJobUpdated(GroupEventArgs<ReadOnlyJob> args)
    {
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Loads the data for all breadcrumb levels based on the current session state.
    /// Populates the dropdown dictionaries with the appropriate items.
    /// Items are displayed in reverse order (newest first) to prioritize recent items.
    /// </summary>
    private void LoadData()
    {
        Projects = DataManager.GetUserProjects(Session.User).Reverse().ToDictionary(p => p.Id, p => p.QualifiedName);
        Spaces = Session.Project?.Spaces.Reverse().ToDictionary(s => s.Id, s => s.QualifiedName);
        Views = Session.Space?.Views.Reverse().ToDictionary(v => v.Id, v => v.QualifiedName);
        Jobs = Session.View?.Jobs.Reverse().ToDictionary(j => j, j => j.QualifiedName);
        FactoryDefinitions = Session.Space?.FactoryDefinitions
            .ToDictionary(d => d.Id, d => d.QualifiedName) ?? new();
        FactoryInstances = Session.View?.FactoryInstances
            .ToDictionary(fi => fi.Id, fi => $"FI{fi.Id}: {fi.Definition?.Alias ?? ""}") ?? new();
        BuildFolderPath();
    }

    /// <summary>
    /// Builds the breadcrumb path from root to the current folder.
    /// </summary>
    private void BuildFolderPath()
    {
        FolderPath = new List<ReadOnlyFolder>();
        var folder = Session.Folder;
        while (folder != null)
        {
            FolderPath.Insert(0, folder);
            folder = folder.Parent;
        }
    }

    /// <summary>
    /// Gets sibling folders at the same level as the given folder (for the dropdown).
    /// </summary>
    private IEnumerable<ReadOnlyFolder> GetSiblingFolders(ReadOnlyFolder folder)
    {
        if (folder.Parent != null)
            return folder.Parent.Items.OfType<ReadOnlyFolder>();
        return Session.View?.RootItems.OfType<ReadOnlyFolder>() ?? Enumerable.Empty<ReadOnlyFolder>();
    }

    private async Task HandleFolderChanged()
    {
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles changes to the active factory definition or factory instance context.
    /// Refreshes the breadcrumb data and UI.
    /// </summary>
    private async Task HandleFactoryContextChanged()
    {
        LoadData();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Navigates to the home screen when the home button is clicked.
    /// Bails on modifier/middle clicks so the wrapping &lt;a&gt; tag handles them natively.
    /// </summary>
    private async Task OnHomeButtonClick(MouseEventArgs args)
    {
        if (MouseUtils.IsNewTabClick(args))
            return;
        await Session.NavigateToAsync(new());
    }

    /// <summary>
    /// Handles click on breadcrumb button for SPA navigation.
    /// Bails on modifier/middle clicks so the wrapping &lt;a&gt; tag handles them natively.
    /// </summary>
    private async Task HandleBreadcrumbClick(MouseEventArgs args, NavigationRequest request)
    {
        if (MouseUtils.IsNewTabClick(args))
            return;
        await Session.NavigateToAsync(request);
    }

    /// <summary>
    /// Handles selection of a project from the project dropdown.
    /// Navigates to the selected project.
    /// </summary>
    private async Task OnProjectValueChange(string id)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = int.Parse(id)
        });
    }

    /// <summary>
    /// Handles selection of a space from the space dropdown.
    /// Navigates to the selected space within the current project.
    /// </summary>
    private async Task OnSpaceValueChange(string id)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = int.Parse(id)
        });
    }

    /// <summary>
    /// Handles selection of a view from the view dropdown.
    /// Navigates to the selected view within the current project and space.
    /// </summary>
    private async Task OnViewValueChange(string id)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = int.Parse(id)
        });
    }

    /// <summary>
    /// Handles selection of a job from the job dropdown.
    /// Navigates to the selected job within the current project, space, and view.
    /// </summary>
    private async Task OnFolderValueChange(int folderId)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = Session.ViewId,
            FolderId = folderId
        });
    }

    private async Task OnJobValueChange(ReadOnlyJob job)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = Session.ViewId,
            FolderId = Session.FolderId,
            JobId = job?.Id,
        });
    }

    /// <summary>
    /// Handles selection of a factory definition from the factory definition dropdown.
    /// Navigates to the selected factory definition within the current project and space.
    /// </summary>
    private async Task OnFactoryDefinitionValueChange(string id)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            FactoryDefinitionId = int.Parse(id)
        });
    }

    /// <summary>
    /// Handles selection of a factory instance from the factory instance dropdown.
    /// Navigates to the selected factory instance within the current project, space, and view.
    /// </summary>
    private async Task OnFactoryInstanceValueChange(string id)
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = Session.ViewId,
            FolderId = Session.FolderId,
            FactoryInstanceId = int.Parse(id)
        });
    }

    private async Task HandleBreadcrumbDrop(ReadOnlyFolder targetFolder)
    {
        if (!DragDrop.IsDragging)
            return;

        var draggedItems = DragDrop.DraggedItems.ToList();
        DragDrop.EndDrag();

        try
        {
            foreach (var item in draggedItems)
            {
                if (item is ReadOnlyJob job)
                    await DataManager.MoveJobToFolder(Session.User, Session.View, job, targetFolder);
                else if (item is ReadOnlyFolder folder && folder.Id != targetFolder?.Id)
                    await DataManager.MoveFolderToFolder(Session.User, Session.View, folder, targetFolder);
            }
        }
        catch (Exception exc)
        {
            // Silently handle - the toast is shown by the service
        }
    }

    /// <summary>
    /// Performs cleanup by unsubscribing from session events.
    /// </summary>
    public void Dispose()
    {
        Session.OnMainChanged -= HandleMainChanged;
        Session.OnFolderChanged -= HandleFolderChanged;
        Session.OnFactoryDefinitionChanged -= HandleFactoryContextChanged;
        Session.OnFactoryInstanceChanged -= HandleFactoryContextChanged;
    }
}