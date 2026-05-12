using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Relay.Screens.Main.Base;
using Warp.Tools;

namespace Relay.Screens.Main.View;

public partial class ViewScreen : ListingScreenLogic<IViewItem>
{
    [Inject]
    private JobEditorService JobEditor { get; set; }

    [Inject]
    private IToastService ToastService { get; set; }

    [Inject]
    private JobSortingService SortingService { get; set; }

    [Inject]
    private DiagramViewService DiagramService { get; set; }

    [Inject]
    private ViewDragDropService DragDrop { get; set; }

    [Inject]
    private FactoryEditorService FactoryEditor { get; set; }

    private bool _jobTypeMenuOpen = false;
    private ReadOnlyPortOut _clickedPort = null;
    private float2? _clickedPosition = null;

    // Card context menu state (used in diagram mode where FluentMenu can't render inside CSS transforms)
    private bool _cardContextMenuOpen;
    private double _cardContextMenuX;
    private double _cardContextMenuY;
    private string _cardContextMenuHeader;
    private List<MenuAction> _cardContextMenuActions;

    private static readonly Dictionary<JobStatus, int> StatusSortPriority = new()
    {
        { JobStatus.Running, 0 },
        { JobStatus.Staging, 1 },
        { JobStatus.Waiting, 2 },
        { JobStatus.Building, 3 },
        { JobStatus.Finalizing, 4 },
        { JobStatus.Aborting, 5 },
        { JobStatus.Failed, 6 },
        { JobStatus.Aborted, 7 },
        { JobStatus.Finished, 8 },
        { JobStatus.Clearing, 9 },
        { JobStatus.Deleted, 10 },
    };

    private bool IsBrowseMode => Session.FactoryInstance != null;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Session.OnViewChanged += HandleViewChanged;
        Session.OnFolderChanged += HandleFolderChanged;
        Session.OnFactoryInstanceChanged += HandleFactoryInstanceChanged;
        SortingService.OnSortChanged += HandleSortChanged;
        DiagramService.OnViewModeChanged += HandleViewModeChanged;
        DiagramService.OnRelayoutRequested += HandleRelayoutRequested;
        Selection.OnSelectionChanged += HandleSelectionChanged;
    }

    private async Task HandleViewChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleFolderChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleSortChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    private void HandleViewModeChanged()
    {
        CloseCardContextMenu();
        InvokeAsync(StateHasChanged);
    }

    private async Task HandleSelectionChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleFactoryInstanceChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    public override void Dispose()
    {
        base.Dispose();
        Session.OnViewChanged -= HandleViewChanged;
        Session.OnFolderChanged -= HandleFolderChanged;
        Session.OnFactoryInstanceChanged -= HandleFactoryInstanceChanged;
        SortingService.OnSortChanged -= HandleSortChanged;
        DiagramService.OnViewModeChanged -= HandleViewModeChanged;
        DiagramService.OnRelayoutRequested -= HandleRelayoutRequested;
        Selection.OnSelectionChanged -= HandleSelectionChanged;
    }

    protected override string GetTitle() => "";
    protected override string GetCreateButtonText() => "";

    protected override SelectionKey GetSelectionKey(IViewItem item) => item.ItemType switch
    {
        ItemType.Folder => SelectionKey.ForFolder(item.Id),
        ItemType.FactoryInstance => SelectionKey.ForFactoryInstance(item.Id),
        _ => SelectionKey.ForJob(item.Id)
    };

    private static string GetColorTag(IViewItem item) => item switch
    {
        ReadOnlyJob j => j.ColorTag,
        ReadOnlyFolder f => f.ColorTag,
        ReadOnlyFactoryInstance fi => fi.ColorTag,
        _ => null
    };

    private static int GetStatusPriority(IViewItem item) => item switch
    {
        ReadOnlyJob j => StatusSortPriority.GetValueOrDefault(j.Status, 99),
        ReadOnlyFactoryInstance fi => StatusSortPriority.GetValueOrDefault(fi.AggregateStatus, 99),
        _ => 5
    };

    private static string GetTypeCategory(IViewItem item) => item switch
    {
        ReadOnlyJob j => j.TypeCategory,
        ReadOnlyFactoryInstance _ => "Factory",
        _ => "Folder"
    };

    protected override IEnumerable<IViewItem> GetItems()
    {
        // In browse mode, show factory instance's sub-jobs
        if (IsBrowseMode)
        {
            var fi = Session.FactoryInstance;
            return fi.SubJobs.Cast<IViewItem>();
        }

        // Get items from current folder or root level
        IReadOnlyList<IViewItem> items;
        if (Session.Folder != null)
            items = Session.Folder.Items;
        else
            items = Session.View?.RootItems;

        if (items == null)
            return Enumerable.Empty<IViewItem>();

        // Custom sort: return items in stored order (already intermixed in data model)
        if (SortingService.Criterion == JobSortCriterion.Custom)
            return items;

        // Note: flex-wrap: wrap-reverse means array index 0 = bottom-left of screen,
        // last index = top-right. Nulls/empty values sort to array start (screen bottom)
        // so they stay out of the way regardless of sort direction.
        IOrderedEnumerable<IViewItem> sorted = SortingService.Criterion switch
        {
            JobSortCriterion.Id => SortingService.IsAscending
                ? items.OrderBy(i => i.Id)
                : items.OrderByDescending(i => i.Id),
            JobSortCriterion.LastModified => SortingService.IsAscending
                ? items.OrderBy(i => i.UpdateDate)
                : items.OrderByDescending(i => i.UpdateDate),
            JobSortCriterion.Status => SortingService.IsAscending
                ? items.OrderBy(GetStatusPriority)
                : items.OrderByDescending(GetStatusPriority),
            JobSortCriterion.Type => SortingService.IsAscending
                ? items.OrderBy(GetTypeCategory, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(GetTypeCategory, StringComparer.OrdinalIgnoreCase),
            JobSortCriterion.Name => SortingService.IsAscending
                ? items.OrderBy(i => string.IsNullOrWhiteSpace(i.Alias) ? 0 : 1)
                       .ThenBy(i => i.Alias, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(i => i.Id)
                : items.OrderBy(i => string.IsNullOrWhiteSpace(i.Alias) ? 0 : 1)
                       .ThenByDescending(i => i.Alias, StringComparer.OrdinalIgnoreCase)
                       .ThenByDescending(i => i.Id),
            JobSortCriterion.Color => SortingService.IsAscending
                ? items.OrderBy(i => GetColorTag(i) == null ? 0 : 1)
                       .ThenBy(GetColorTag, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(i => i.Id)
                : items.OrderBy(i => GetColorTag(i) == null ? 0 : 1)
                       .ThenByDescending(GetColorTag, StringComparer.OrdinalIgnoreCase)
                       .ThenByDescending(i => i.Id),
            _ => items.OrderBy(i => i.Id)
        };

        return sorted;
    }

    #region Item reordering

    /// <summary>
    /// Returns the current item list for reordering: folder contents if inside a folder, root items otherwise.
    /// </summary>
    private IReadOnlyList<IViewItem> GetCurrentLevelItems() =>
        Session.Folder != null ? Session.Folder.Items : Session.View?.RootItems;

    private int IndexOfSelectedItem(IReadOnlyList<IViewItem> items, SelectionKey key)
    {
        for (int i = 0; i < items.Count; i++)
            if (GetSelectionKey(items[i]).Equals(key))
                return i;
        return -1;
    }

    private bool CanMoveLeft
    {
        get
        {
            if (SortingService.Criterion != JobSortCriterion.Custom)
                return false;
            if (Selection.SelectedItems.Count != 1)
                return false;
            var items = GetCurrentLevelItems();
            if (items == null)
                return false;
            return IndexOfSelectedItem(items, Selection.SelectedItems[0]) > 0;
        }
    }

    private bool CanMoveRight
    {
        get
        {
            if (SortingService.Criterion != JobSortCriterion.Custom)
                return false;
            if (Selection.SelectedItems.Count != 1)
                return false;
            var items = GetCurrentLevelItems();
            if (items == null)
                return false;
            int index = IndexOfSelectedItem(items, Selection.SelectedItems[0]);
            return index >= 0 && index < items.Count - 1;
        }
    }

    private async Task HandleMoveLeft()
    {
        var items = GetCurrentLevelItems();
        if (items == null) return;
        int index = IndexOfSelectedItem(items, Selection.SelectedItems[0]);
        if (index <= 0) return;
        var item = items[index];

        if (Session.Folder != null)
            await DataManager.ReorderItemInFolder(Session.User, Session.View, Session.Folder, item, index - 1);
        else
            await DataManager.ReorderItemInView(Session.User, Session.View, item, index - 1);
    }

    private async Task HandleMoveRight()
    {
        var items = GetCurrentLevelItems();
        if (items == null) return;
        int index = IndexOfSelectedItem(items, Selection.SelectedItems[0]);
        if (index < 0 || index >= items.Count - 1) return;
        var item = items[index];

        if (Session.Folder != null)
            await DataManager.ReorderItemInFolder(Session.User, Session.View, Session.Folder, item, index + 1);
        else
            await DataManager.ReorderItemInView(Session.User, Session.View, item, index + 1);
    }

    #endregion

    protected override async Task ShowCreateDialogAsync()
    {

    }

    protected override async Task OnCreateDialogClosedAsync(DialogResult result)
    {
    }

    protected override Task NavigateToItemAsync(IViewItem item)
    {
        if (item is ReadOnlyFolder folder)
        {
            return Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = Session.Space.Id,
                ViewId = Session.View.Id,
                FolderId = folder.Id
            });
        }

        if (item is ReadOnlyFactoryInstance fi)
        {
            return Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = Session.Space.Id,
                ViewId = Session.View.Id,
                FolderId = Session.FolderId,
                FactoryInstanceId = fi.Id
            });
        }

        if (item is ReadOnlyJob job)
        {
            return Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = Session.Space.Id,
                ViewId = Session.View.Id,
                FolderId = Session.FolderId,
                FactoryInstanceId = Session.FactoryInstanceId,
                JobId = job.Id
            });
        }

        return Task.CompletedTask;
    }

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();

        if (Session.Project == null || Session.Space == null || Session.View == null)
            return;

        _subscriptions.Add(DataManager.ViewUpdated.Add(GroupName.View(Session.Project.Id, Session.Space.Id, Session.View.Id),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.ViewDeleted.Add(GroupName.View(Session.Project.Id, Session.Space.Id, Session.View.Id),
                                                       async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(Session.Project.Id, Session.Space.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(Session.Project.Id, Session.Space.Id, null),
                                                      async _ => await InvokeAsync(StateHasChanged)));

        _subscriptions.Add(DataManager.FactoryInstanceCreated.Add(
            GroupName.FactoryInstance(Session.Project.Id, Session.Space.Id, null),
            async _ => await InvokeAsync(StateHasChanged)));
        _subscriptions.Add(DataManager.FactoryInstanceDeleted.Add(
            GroupName.FactoryInstance(Session.Project.Id, Session.Space.Id, null),
            async _ => await InvokeAsync(StateHasChanged)));
    }

    private async Task ItemDoubleClickedAsync(ReadOnlyJob job, MouseEventArgs args)
    {
        if (MouseUtils.IsNewTabClick(args))
        {
            await Session.OpenInNewTabAsync(new NavigationRequest
            {
                ProjectId = Session.Project.Id,
                SpaceId = Session.Space.Id,
                ViewId = Session.View.Id,
                FolderId = Session.FolderId,
                FactoryInstanceId = Session.FactoryInstanceId,
                JobId = job.Id
            });
        }
        else
        {
            await NavigateToItemAsync(job);
        }
    }

    private async Task HandleFolderDoubleClick(ReadOnlyFolder folder, MouseEventArgs args)
    {
        await Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Session.Project.Id,
            SpaceId = Session.Space.Id,
            ViewId = Session.View.Id,
            FolderId = folder.Id
        });
    }

    private async Task HandleFactoryInstanceDoubleClick(ReadOnlyFactoryInstance fi, MouseEventArgs args)
    {
        await Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Session.Project.Id,
            SpaceId = Session.Space.Id,
            ViewId = Session.View.Id,
            FactoryInstanceId = fi.Id
        });
    }

    private async Task HandleMiddleClick(ReadOnlyJob job, MouseEventArgs args)
    {
        await Session.OpenInNewTabAsync(new NavigationRequest
        {
            ProjectId = Session.Project.Id,
            SpaceId = Session.Space.Id,
            ViewId = Session.View.Id,
            FolderId = Session.FolderId,
            FactoryInstanceId = Session.FactoryInstanceId,
            JobId = job.Id
        });
    }

    #region Diagram view

    private DiagramLayout? GetDiagramLayout()
    {
        if (IsBrowseMode)
            return Session.FactoryInstance?.DiagramLayout;
        if (Session.Folder != null)
            return Session.Folder.DiagramLayout;
        return Session.View?.DiagramLayout;
    }

    private async Task HandleDiagramItemClick((IViewItem Item, MouseEventArgs Args) e)
    {
        await HandleItemClicked(e.Item, e.Args);
    }

    private async Task HandleDiagramDoubleClick((IViewItem Item, MouseEventArgs Args) e)
    {
        if (e.Item is ReadOnlyJob job)
            await ItemDoubleClickedAsync(job, e.Args);
        else if (e.Item is ReadOnlyFolder folder)
            await HandleFolderDoubleClick(folder, e.Args);
        else if (e.Item is ReadOnlyFactoryInstance fi)
            await HandleFactoryInstanceDoubleClick(fi, e.Args);
    }

    private void HandleCardContextMenu(CardContextMenuArgs args)
    {
        _cardContextMenuX = args.MouseEventArgs.ClientX;
        _cardContextMenuY = args.MouseEventArgs.ClientY;
        _cardContextMenuHeader = args.Header;
        _cardContextMenuActions = args.Actions;
        _cardContextMenuOpen = true;
    }

    private void CloseCardContextMenu()
    {
        _cardContextMenuOpen = false;
        _cardContextMenuActions = null;
    }

    private async void HandleRelayoutRequested()
    {
        if (Session.View == null) return;
        try
        {
            if (IsBrowseMode && Session.FactoryInstance != null)
                await DataManager.ResetFactoryInstanceDiagramLayout(Session.FactoryInstance);
            else
                await DataManager.ResetDiagramLayout(Session.View, Session.Folder);
            DiagramService.RequestZoomToFit();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't redo layout: " + exc.Message);
        }
    }

    #endregion

    private IEnumerable<ReadOnlyFactoryDefinition> FactoryDefinitions =>
        Session.Space?.FactoryDefinitions
        ?? Enumerable.Empty<ReadOnlyFactoryDefinition>();

    private async Task HandleFactorySelected(ReadOnlyFactoryDefinition def)
    {
        try
        {
            var fi = await DataManager.CreateFactoryInstance(Session.User, Session.View, def.Id, Session.Folder);
            await FactoryEditor.SetInstance(fi);
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create factory instance: " + exc.Message);
        }
    }

    #region Job menu

    private async Task OnContextMenu(MouseEventArgs args)
    {
        if (IsBrowseMode) return;

        _clickedPosition = new float2((float)args.ClientX - RelaySession.LeftPanelWidth,
                                      (float)args.ClientY - RelaySession.TopPanelHeight);
        _clickedPort = null;
        _jobTypeMenuOpen = true;

        _lastContextMenuTime = DateTime.Now;
    }

    private async Task HandlePortClicked(PortClickArgs args)
    {
        if (IsBrowseMode) return;

        _clickedPosition = new float2((float)args.MouseEventArgs.ClientX - RelaySession.LeftPanelWidth,
                                      (float)args.MouseEventArgs.ClientY - RelaySession.TopPanelHeight);
        _clickedPort = args.Port;
        _jobTypeMenuOpen = true;

        _lastContextMenuTime = DateTime.Now;
    }

    private async Task HandleJobTypeMenuOpenChanged(bool isOpen)
    {
        _jobTypeMenuOpen = isOpen;

        if (!isOpen)
        {
            _clickedPort = null;
            _clickedPosition = null;
        }
    }

    private Func<Type, bool> GetJobTypeFilter()
    {
        if (_clickedPort != null)
        {
            var resourceType = _clickedPort.ResourceType;
            return t => Job.AllTypesPortsIn[t].Any(p => p.Value.ResourceType == resourceType);
        }

        return t => true;
    }

    private Func<Type, bool> GetPortFilter()
    {
        if (_clickedPort != null)
        {
            var resourceType = _clickedPort.ResourceType;
            return p => p == resourceType;
        }

        return null;
    }

    private MenuType GetMenuType()
    {
        if (JobEditor.IsActive)
            return MenuType.ConnectToPort;

        if (_clickedPort != null)
            return MenuType.CreateFromPort;

        return MenuType.CreateFromType;
    }

    #endregion

    #region Job creation

    private async Task HandleMenuTypeSelected(Type type)
    {
        try
        {
            Job template = Activator.CreateInstance(type) as Job;
            template.Status = JobStatus.Building;

            var newJob = await DataManager.CreateJob(Session.User, Session.View, template.TypeGuid, template, Session.Folder);
            await JobEditor.SetJob(newJob);

            ToastService.ShowSuccess($"{newJob.QualifiedName} created!");
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create job:\n" + exc.Message);
        }
    }

    private async Task HandlePortSelected((Type jobType, ReadOnlyPortOut portOut, ReadOnlyPortIn portIn) args)
    {
        try
        {
            Job template = Activator.CreateInstance(args.jobType) as Job;
            template.Status = JobStatus.Building;

            var newJob = await DataManager.CreateJob(Session.User, Session.View, template.TypeGuid, template, Session.Folder);
            var newPort = newJob.PortsIn[args.portIn.Name];

            if (args.portOut != null)
            {
                if (args.portOut.ResourceType != args.portIn.ResourceType)
                    throw new Exception("Can't connect ports of different types");

                await DataManager.CreateEdge(Session.Space, args.portOut, newPort);
            }
            else
                throw new Exception("Can't find port to connect");

            await JobEditor.SetJob(newJob);

            ToastService.ShowSuccess($"{newJob.QualifiedName} created!");
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create job:\n" + exc.Message);
        }
    }

    private async Task HandlePortConnected((ReadOnlyPortOut portOut, ReadOnlyPortIn portIn) args)
    {
        try
        {
            if (args.portOut != null)
            {
                if (args.portOut.ResourceType != args.portIn.ResourceType)
                    throw new Exception("Can't connect ports of different types");

                await DataManager.CreateEdge(Session.Space, args.portOut, args.portIn);
            }
            else
                throw new Exception("Can't find port to connect");
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't connect job:\n" + exc.Message);
        }
    }

    private async Task HandleFolderCreation()
    {
        try
        {
            await DataManager.CreateFolder(Session.User, Session.View, "New Folder", Session.Folder);
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create folder:\n" + exc.Message);
        }
    }

    #endregion

    #region Drag and drop

    private async Task HandleBackgroundDrop(DragEventArgs args)
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
                {
                    // Skip if job is already at this level
                    var currentParent = Session.View?.FindFolderContainingJob(job.Id);
                    if (currentParent?.Id == Session.Folder?.Id)
                        continue;
                    await DataManager.MoveJobToFolder(Session.User, Session.View, job, Session.Folder);
                }
                else if (item is ReadOnlyFolder folder)
                {
                    // Skip if folder is already at this level
                    if (folder.Parent?.Id == Session.Folder?.Id)
                        continue;
                    await DataManager.MoveFolderToFolder(Session.User, Session.View, folder, Session.Folder);
                }
            }
        }
        catch (Exception exc)
        {
            ToastService.ShowError($"Couldn't move item: {exc.Message}");
        }
    }

    #endregion
}
