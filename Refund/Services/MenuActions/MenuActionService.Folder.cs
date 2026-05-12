using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel.ReadOnly;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Services;

public partial class MenuActionService
{
    /// <summary>
    /// Gets the list of available actions for a folder.
    /// </summary>
    /// <param name="folder">The folder to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    public List<MenuAction> GetFolderActions(ReadOnlyFolder folder)
    {
        List<MenuAction> result = new();

        #region Color

        {
            var actionColor = new MenuAction()
            {
                Name = "Color",
                IconSmall = new Icons.Regular.Size16.Color(),
                IconLarge = new Icons.Regular.Size20.Color()
            };

            var palette = new (string Hex, string Label)[]
            {
                ("#F5A8A8", "Red"),
                ("#F5CDA8", "Orange"),
                ("#F5E6A8", "Yellow"),
                ("#A8F5B8", "Green"),
                ("#A8E6F5", "Teal"),
                ("#A8B8F5", "Blue"),
                ("#CDA8F5", "Purple"),
                ("#F5A8E6", "Pink")
            };

            foreach (var (hex, label) in palette)
            {
                var color = hex;
                actionColor.SubActions.Add(new MenuAction()
                {
                    Name = label,
                    BackgroundColor = $"color-mix(in srgb, {color} 50%, transparent)",
                    Action = async () =>
                    {
                        try
                        {
                            await _dataManager.UpdateFolder(_session.User, _session.View, folder, f => f.ColorTag = color);
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't set color on folder: {exc.Message}");
                        }
                    }
                });
            }

            actionColor.SubActions.Add(new MenuAction()
            {
                Name = "Remove color",
                Action = async () =>
                {
                    try
                    {
                        await _dataManager.UpdateFolder(_session.User, _session.View, folder, f => f.ColorTag = null);
                    }
                    catch (Exception exc)
                    {
                        _toastService.ShowError($"Couldn't remove color from folder: {exc.Message}");
                    }
                }
            });

            result.Add(actionColor);
        }

        #endregion

        #region Move folder to folder (intra-view)

        {
            var intraCurrentView = _session.View;
            var hasOtherFolders = intraCurrentView.Folders.Any(f => f.Id != folder.Id);
            var hasParent = folder.Parent != null;

            if (hasOtherFolders || hasParent)
            {
                var actionMoveToFolder = new MenuAction()
                {
                    Name = "Move to folder",
                    NeedsConfirmation = false,
                    Appearance = null,
                    IconSmall = new Icons.Regular.Size16.ArrowForward(),
                    IconLarge = new Icons.Regular.Size20.ArrowForward()
                };

                Func<ReadOnlyFolder, Task> moveToFolderAction = async (targetFolder) =>
                {
                    try
                    {
                        await _dataManager.MoveFolderToFolder(_session.User, intraCurrentView, folder, targetFolder);
                        var destName = targetFolder != null ? $"folder \"{targetFolder.Alias}\"" : "root level";
                        _toastService.ShowSuccess($"Folder \"{folder.Alias}\" moved to {destName}");
                    }
                    catch (Exception exc)
                    {
                        _toastService.ShowError($"Couldn't move folder: {exc.Message}");
                    }
                };

                // Show "(root level)" only if folder is not already at root
                if (hasParent)
                {
                    actionMoveToFolder.SubActions.Add(new MenuAction()
                    {
                        Name = "(root level)",
                        Action = async () => await moveToFolderAction(null)
                    });
                }

                BuildFolderSubActions(actionMoveToFolder, intraCurrentView.Folders.Where(f => f.Parent == null), moveToFolderAction, folder.Id);

                result.Add(actionMoveToFolder);
            }
        }

        #endregion

        #region Add folder to another view

        {
            var actionAddFolder = new MenuAction()
            {
                Name = "Add folder to another view",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.LinkAdd(),
                IconLarge = new Icons.Regular.Size20.LinkAdd()
            };

            var folderJobs = folder.GetAllJobsRecursive().ToList();
            var currentView = _session.View;

            var availableViews = currentView.Space.Views.Reverse()
                .Where(v => v != currentView && !folderJobs.Any(j => v.Jobs.Any(vj => vj.Id == j.Id)));

            if (!availableViews.Any())
            {
                actionAddFolder.DisabledBecause = "No other views available where none of this folder's jobs exist";
                actionAddFolder.IsDisabled = true;
            }
            else
            {
                foreach (var view in availableViews)
                {
                    Func<ReadOnlyFolder, Task> addAction = async (targetParent) =>
                    {
                        try
                        {
                            var (added, skipped) = await ReplicateFolderToView(folder, view, targetParent);
                            var msg = $"Folder \"{folder.Alias}\" added to {view.QualifiedName} ({added} job{(added != 1 ? "s" : "")} added)";
                            if (skipped > 0)
                                msg += $", {skipped} skipped";
                            _toastService.ShowSuccess(msg);
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't add folder to {view.QualifiedName}: {exc.Message}");
                        }
                    };

                    AddViewDestination(actionAddFolder, view, addAction);
                }
            }

            result.Add(actionAddFolder);
        }

        #endregion

        #region Move folder to another view

        {
            var actionMoveFolder = new MenuAction()
            {
                Name = "Move folder to another view",
                NeedsConfirmation = true,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.ArrowForward(),
                IconLarge = new Icons.Regular.Size20.ArrowForward()
            };

            // Reuse folderJobs/currentView from Add block — redeclare since we're in a new scope
            var moveFolderJobs = folder.GetAllJobsRecursive().ToList();
            var moveCurrentView = _session.View;

            var moveAvailableViews = moveCurrentView.Space.Views.Reverse()
                .Where(v => v != moveCurrentView && !moveFolderJobs.Any(j => v.Jobs.Any(vj => vj.Id == j.Id)));

            if (!moveAvailableViews.Any())
            {
                actionMoveFolder.DisabledBecause = "No other views available where none of this folder's jobs exist";
                actionMoveFolder.IsDisabled = true;
            }
            else
            {
                foreach (var view in moveAvailableViews)
                {
                    Func<ReadOnlyFolder, Task> moveAction = async (targetParent) =>
                    {
                        try
                        {
                            var (added, skipped) = await ReplicateFolderToView(folder, view, targetParent);

                            // Remove jobs from current view (only if they exist in at least one other view)
                            foreach (var job in moveFolderJobs)
                            {
                                if (job.Space.Views.Any(v => v != moveCurrentView && v.Jobs.Any(vj => vj.Id == job.Id)))
                                {
                                    try
                                    {
                                        await _dataManager.RemoveJobFromView(_session.User, moveCurrentView, job);
                                    }
                                    catch (Exception exc)
                                    {
                                        _toastService.ShowError($"Couldn't remove {job.QualifiedName} from view: {exc.Message}");
                                    }
                                }
                            }

                            // Delete subfolders bottom-up, then the source folder
                            // (DeleteFolder ungroups contents, so we must delete children first)
                            await DeleteFolderRecursive(_session.User, moveCurrentView, folder);

                            var msg = $"Folder \"{folder.Alias}\" moved to {view.QualifiedName} ({added} job{(added != 1 ? "s" : "")} moved)";
                            if (skipped > 0)
                                msg += $", {skipped} skipped";
                            _toastService.ShowSuccess(msg);
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't move folder to {view.QualifiedName}: {exc.Message}");
                        }
                    };

                    AddViewDestination(actionMoveFolder, view, moveAction);
                }
            }

            result.Add(actionMoveFolder);
        }

        #endregion

        #region Delete (ungroup)

        {
            var actionDelete = new MenuAction()
            {
                Name = "Ungroup",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.DismissCircle().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.DismissCircle().WithColor("var(--error)")
            };

            actionDelete.Action = async () =>
            {
                try
                {
                    await _dataManager.DeleteFolder(_session.User, _session.View, folder);
                    _toastService.ShowSuccess($"Folder \"{folder.Alias}\" ungrouped");
                }
                catch (Exception exc)
                {
                    _toastService.ShowError($"Couldn't ungroup folder: {exc.Message}");
                }
            };

            result.Add(actionDelete);
        }

        #endregion

        return result;
    }
}
