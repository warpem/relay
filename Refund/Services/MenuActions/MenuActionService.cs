using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Services;

/// <summary>
/// Provides contextual menu actions for different objects in the application.
/// </summary>
/// <remarks>
/// This service builds lists of available actions for various types of objects (projects, spaces, views, jobs).
/// Each action includes metadata about its appearance, enabled/disabled state, confirmation requirements,
/// and the actual operation to perform when the action is triggered.
///
/// The service handles validating when actions should be disabled (with explanatory messages)
/// and provides notifications when actions succeed or fail.
/// </remarks>
public partial class MenuActionService
{
    private readonly DataManager _dataManager;
    private readonly RelaySession _session;
    private readonly JobEditorService _jobEditor;
    private readonly FactoryEditorService _factoryEditor;
    private readonly CardSelectionService _selectionService;
    private readonly IToastService _toastService;
    private readonly FileService _fileService;
    private readonly IJSRuntime _jsRuntime;

    /// <summary>
    /// Initializes a new instance of the <see cref="MenuActionService"/> class.
    /// </summary>
    /// <param name="dataManager">The data manager for performing operations on model objects</param>
    /// <param name="session">The current session with user and context information</param>
    /// <param name="jobEditorService">The job editor service for editing jobs</param>
    /// <param name="selectionService">The card selection service for managing selected cards</param>
    /// <param name="toastService">The toast service for displaying notifications</param>
    /// <param name="fileService">The file service for generating secure download URLs</param>
    /// <param name="jsRuntime">The JavaScript runtime for triggering browser downloads</param>
    public MenuActionService(DataManager dataManager,
                             RelaySession session,
                             JobEditorService jobEditorService,
                             FactoryEditorService factoryEditorService,
                             CardSelectionService selectionService,
                             IToastService toastService,
                             FileService fileService,
                             IJSRuntime jsRuntime)
    {
        _dataManager = dataManager;
        _session = session;
        _jobEditor = jobEditorService;
        _factoryEditor = factoryEditorService;
        _selectionService = selectionService;
        _toastService = toastService;
        _fileService = fileService;
        _jsRuntime = jsRuntime;
    }

    /// <summary>
    /// Recursively builds folder sub-actions for a parent menu action,
    /// optionally skipping a specific folder (and its descendants).
    /// </summary>
    private void BuildFolderSubActions(MenuAction parentAction, IEnumerable<ReadOnlyFolder> folders, Func<ReadOnlyFolder, Task> action, int? excludeFolderId = null)
    {
        foreach (var folder in folders.OrderBy(f => f.Alias, StringComparer.OrdinalIgnoreCase))
        {
            if (excludeFolderId.HasValue && folder.Id == excludeFolderId.Value)
                continue;

            var folderAction = new MenuAction() { Name = folder.Alias };

            var subFolders = folder.Items.OfType<ReadOnlyFolder>().ToList();
            if (subFolders.Any())
            {
                folderAction.SubActions.Add(new MenuAction()
                {
                    Name = "(this folder)",
                    Action = async () => await action(folder)
                });
                BuildFolderSubActions(folderAction, subFolders, action, excludeFolderId);
            }
            else
            {
                folderAction.Action = async () => await action(folder);
            }

            parentAction.SubActions.Add(folderAction);
        }
    }

    /// <summary>
    /// Adds a view as a destination in a menu action, with folder hierarchy if the view has folders.
    /// </summary>
    private void AddViewDestination(MenuAction parentAction, ReadOnlyView view, Func<ReadOnlyFolder, Task> action)
    {
        var viewAction = new MenuAction() { Name = view.Alias };

        if (view.Folders.Count > 0)
        {
            viewAction.SubActions.Add(new MenuAction()
            {
                Name = "(root level)",
                Action = async () => await action(null)
            });
            BuildFolderSubActions(viewAction, view.Folders.Where(f => f.Parent == null), action);
        }
        else
        {
            viewAction.Action = async () => await action(null);
        }

        parentAction.SubActions.Add(viewAction);
    }

    /// <summary>
    /// Recursively deletes a folder and all its subfolders, bottom-up.
    /// This avoids orphaned subfolders that would result from ungrouping a parent first.
    /// </summary>
    private async Task DeleteFolderRecursive(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFolder folder)
    {
        foreach (var subfolder in folder.Items.OfType<ReadOnlyFolder>().ToList())
            await DeleteFolderRecursive(user, view, subfolder);

        await _dataManager.DeleteFolder(user, view, folder);
    }

    /// <summary>
    /// Recursively replicates a folder's structure into a target view.
    /// Creates the folder with same alias/color, adds child jobs (skipping duplicates), recurses for subfolders.
    /// </summary>
    /// <returns>Tuple of (jobs added, jobs skipped because already in target view)</returns>
    private async Task<(int added, int skipped)> ReplicateFolderToView(
        ReadOnlyFolder sourceFolder,
        ReadOnlyView targetView,
        ReadOnlyFolder targetParent)
    {
        var user = _session.User;
        int added = 0, skipped = 0;

        // Create replica folder
        var replica = await _dataManager.CreateFolder(user, targetView, sourceFolder.Alias, targetParent);
        if (!string.IsNullOrEmpty(sourceFolder.ColorTag))
            await _dataManager.UpdateFolder(user, targetView, replica, f => f.ColorTag = sourceFolder.ColorTag);

        foreach (var item in sourceFolder.Items)
        {
            if (item is ReadOnlyJob job)
            {
                if (targetView.Jobs.Any(j => j.Id == job.Id))
                {
                    skipped++;
                    continue;
                }

                await _dataManager.AddJobToView(user, targetView, job);
                // Re-fetch replica since view was updated
                replica = targetView.FindFolder(replica.Id);
                await _dataManager.MoveJobToFolder(user, targetView, job, replica);
                added++;
            }
            else if (item is ReadOnlyFolder subfolder)
            {
                // Re-fetch replica since view may have been updated
                replica = targetView.FindFolder(replica.Id);
                var (subAdded, subSkipped) = await ReplicateFolderToView(subfolder, targetView, replica);
                added += subAdded;
                skipped += subSkipped;
            }
        }

        return (added, skipped);
    }
}
