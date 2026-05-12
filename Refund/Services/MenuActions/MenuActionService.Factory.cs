using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.Session;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Services;

public partial class MenuActionService
{
    /// <summary>
    /// Gets the list of available actions for one or more factory instances.
    /// </summary>
    /// <param name="instances">The factory instances to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    public List<MenuAction> GetFactoryInstanceActions(IEnumerable<ReadOnlyFactoryInstance> instances)
    {
        List<MenuAction> result = new();

        #region Run

        if (instances.All(i => i.SubJobs.Any(j => j.Status == JobStatus.Building)))
        {
            bool allBuilding = instances.All(i => i.SubJobs.All(j => j.Status == JobStatus.Building));

            var actionRun = new MenuAction()
            {
                Name = allBuilding
                    ? (instances.Count() > 1 ? $"Run {instances.Count()} instances" : "Run instance")
                    : (instances.Count() > 1 ? $"Run remaining in {instances.Count()} instances" : "Run remaining"),
                NeedsConfirmation = false,
                Appearance = Appearance.Accent,
                TextColor = null,
                BorderColor = null,
                IconSmall = new Icons.Regular.Size16.Play(),
                IconLarge = new Icons.Regular.Size20.Play()
            };

            actionRun.Action = async () =>
            {
                foreach (var instance in instances)
                    Task.Run(async () =>
                    {
                        try
                        {
                            var queueAssignments = instance.SubJobIds.ToDictionary(id => id, _ => -1);
                            await _dataManager.RunFactoryInstance(_session.User, instance, queueAssignments);
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't run {instance.QualifiedName}: {exc.Message}");
                        }
                    });
            };

            result.Add(actionRun);
        }

        #endregion

        #region Edit

        {
            if (instances.Count() == 1 && instances.First().SubJobs.Any(j => j.Status == JobStatus.Building))
            {
                var instance = instances.First();

                var actionEdit = new MenuAction()
                {
                    Name = "Edit instance",
                    NeedsConfirmation = false,
                    IconSmall = new Icons.Regular.Size16.Edit(),
                    IconLarge = new Icons.Regular.Size20.Edit()
                };

                actionEdit.Action = async () => await _factoryEditor.SetInstance(instance);

                result.Add(actionEdit);
            }
        }

        #endregion

        #region Abort

        {
            if (instances.Any(i => i.SubJobs.Any(j => j.Status.IsUnsettled() || j.Status == JobStatus.Waiting)))
            {
                var actionAbort = new MenuAction()
                {
                    Name = instances.Count() > 1 ?
                               $"Abort {instances.Count()} instances" :
                               "Abort instance",
                    NeedsConfirmation = true,
                    Appearance = null,
                    TextColor = "var(--error)",
                    BorderColor = "var(--error)",
                    IconSmall = new Icons.Regular.Size16.RecordStop().WithColor("var(--error)"),
                    IconLarge = new Icons.Regular.Size20.RecordStop().WithColor("var(--error)")
                };

                actionAbort.Action = async () =>
                {
                    foreach (var instance in instances)
                        Task.Run(async () =>
                        {
                            try
                            {
                                await _dataManager.AbortFactoryInstance(_session.User, instance);
                                _toastService.ShowSuccess($"{instance.QualifiedName} aborted");
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't abort {instance.QualifiedName}: {exc.Message}");
                            }
                        });
                };

                result.Add(actionAbort);
            }
        }

        #endregion

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
                        foreach (var instance in instances)
                        {
                            try
                            {
                                await _dataManager.UpdateFactoryInstance(_session.User, instance, fi => fi.ColorTag = color);
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't set color on {instance.QualifiedName}: {exc.Message}");
                            }
                        }
                    }
                });
            }

            actionColor.SubActions.Add(new MenuAction()
            {
                Name = "Remove color",
                Action = async () =>
                {
                    foreach (var instance in instances)
                    {
                        try
                        {
                            await _dataManager.UpdateFactoryInstance(_session.User, instance, fi => fi.ColorTag = null);
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't remove color from {instance.QualifiedName}: {exc.Message}");
                        }
                    }
                }
            });

            result.Add(actionColor);
        }

        #endregion

        #region Clone

        {
            if (instances.Count() == 1)
            {
                var instance = instances.First();

                var actionClone = new MenuAction()
                {
                    Name = "Clone instance",
                    NeedsConfirmation = false,
                    Appearance = null,
                    TextColor = null,
                    BorderColor = null,
                    IconSmall = new Icons.Regular.Size16.Copy(),
                    IconLarge = new Icons.Regular.Size20.Copy()
                };

                actionClone.Action = async () =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            await _dataManager.CloneFactoryInstance(_session.User, _session.View, instance);
                            _toastService.ShowSuccess($"{instance.QualifiedName} cloned");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't clone {instance.QualifiedName}: {exc.Message}");
                        }
                    });
                };

                result.Add(actionClone);
            }
        }

        #endregion

        #region Add to view

        {
            var actionAddToView = new MenuAction()
            {
                Name = instances.Count() > 1 ?
                           $"Add {instances.Count()} instances to another view" :
                           "Add instance to another view",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.LinkAdd(),
                IconLarge = new Icons.Regular.Size20.LinkAdd()
            };

            var availableViews = instances.First()
                                     .Space.Views.Reverse().Where(v => v != _session.View &&
                                                             instances.All(i => !v.FactoryInstances.Any(fi => fi.Id == i.Id)));

            if (!availableViews.Any())
            {
                actionAddToView.DisabledBecause = "No other views available that none of the selected instances are part of";
                actionAddToView.IsDisabled = true;
            }
            else
            {
                foreach (var view in availableViews)
                {
                    Func<ReadOnlyFolder, Task> addAction = async (targetFolder) =>
                    {
                        foreach (var instance in instances)
                            try
                            {
                                await _dataManager.AddFactoryInstanceToView(_session.User, view, instance);
                                if (targetFolder != null)
                                    await _dataManager.MoveFactoryInstanceToFolder(_session.User, view, instance, targetFolder);
                                _toastService.ShowSuccess($"{instance.QualifiedName} added to {view.QualifiedName}");
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't add {instance.QualifiedName} to {view.QualifiedName}: {exc.Message}");
                            }
                    };

                    AddViewDestination(actionAddToView, view, addAction);
                }
            }

            result.Add(actionAddToView);
        }

        #endregion

        #region Move to view

        {
            var actionMoveToView = new MenuAction()
            {
                Name = instances.Count() > 1 ?
                           $"Move {instances.Count()} instances" :
                           "Move instance",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.ArrowForward(),
                IconLarge = new Icons.Regular.Size20.ArrowForward()
            };

            {
                var currentView = _session.View;
                bool hasCurrentViewEntry = false;

                // Current view entry: intra-view folder movement
                if (currentView.Folders.Count > 0)
                {
                    bool allAtRoot = instances.All(i => currentView.RootItems.OfType<ReadOnlyFactoryInstance>().Any(fi => fi.Id == i.Id));

                    Func<ReadOnlyFolder, Task> intraViewMoveAction = async (targetFolder) =>
                    {
                        foreach (var instance in instances)
                            try
                            {
                                await _dataManager.MoveFactoryInstanceToFolder(_session.User, currentView, instance, targetFolder);
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't move {instance.QualifiedName}: {exc.Message}");
                            }
                    };

                    var currentViewAction = new MenuAction() { Name = $"{currentView.Alias} (current)" };

                    // Show "(root level)" only if not all instances are already at root
                    if (!allAtRoot)
                    {
                        currentViewAction.SubActions.Add(new MenuAction()
                        {
                            Name = "(root level)",
                            Action = async () => await intraViewMoveAction(null)
                        });
                    }

                    BuildFolderSubActions(currentViewAction, currentView.Folders.Where(f => f.Parent == null), intraViewMoveAction);

                    // Only add current view entry if there are actual destinations
                    if (currentViewAction.SubActions.Count > 0)
                    {
                        actionMoveToView.SubActions.Add(currentViewAction);
                        hasCurrentViewEntry = true;
                    }
                }

                // Other views: cross-view move
                var moveAvailableViews = instances.First()
                                             .Space.Views.Reverse().Where(v => v != currentView &&
                                                                     instances.All(i => !v.FactoryInstances.Any(fi => fi.Id == i.Id)));

                foreach (var view in moveAvailableViews)
                {
                    Func<ReadOnlyFolder, Task> moveAction = async (targetFolder) =>
                    {
                        var instanceList = instances.ToList();

                        Task.Run(async () =>
                        {
                            await _selectionService.Clear();

                            foreach (var instance in instanceList)
                                try
                                {
                                    await _dataManager.AddFactoryInstanceToView(_session.User, view, instance);
                                    if (targetFolder != null)
                                        await _dataManager.MoveFactoryInstanceToFolder(_session.User, view, instance, targetFolder);
                                    await _dataManager.RemoveFactoryInstanceFromView(_session.User, currentView, instance);
                                    _toastService.ShowSuccess($"{instance.QualifiedName} moved to {view.QualifiedName}");
                                }
                                catch (Exception exc)
                                {
                                    _toastService.ShowError($"Couldn't move {instance.QualifiedName} to {view.QualifiedName}: {exc.Message}");
                                }
                        });
                    };

                    AddViewDestination(actionMoveToView, view, moveAction);
                }

                if (!hasCurrentViewEntry && !moveAvailableViews.Any())
                {
                    actionMoveToView.DisabledBecause = "No destinations available";
                    actionMoveToView.IsDisabled = true;
                }
            }

            result.Add(actionMoveToView);
        }

        #endregion

        #region Clear failed

        {
            if (instances.Any(i => i.SubJobs.Any(j => j.Status == JobStatus.Failed || j.Status == JobStatus.Aborted)))
            {
                var actionClearFailed = new MenuAction()
                {
                    Name = instances.Count() > 1 ?
                               $"Clear failed in {instances.Count()} instances" :
                               "Clear failed",
                    NeedsConfirmation = true,
                    Appearance = null,
                    TextColor = "var(--error)",
                    BorderColor = "var(--error)",
                    IconSmall = new Icons.Regular.Size16.Broom().WithColor("var(--error)"),
                    IconLarge = new Icons.Regular.Size20.Broom().WithColor("var(--error)")
                };

                actionClearFailed.Action = async () =>
                {
                    foreach (var instance in instances)
                        Task.Run(async () =>
                        {
                            try
                            {
                                await _dataManager.ClearFailedFactoryInstance(_session.User, instance);
                                _toastService.ShowSuccess($"{instance.QualifiedName} cleared (failed)");
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't clear failed in {instance.QualifiedName}: {exc.Message}");
                            }
                        });
                };

                result.Add(actionClearFailed);
            }
        }

        #endregion

        #region Clear all

        {
            if (instances.Any(i => i.SubJobs.Any(j => j.Status != JobStatus.Building)))
            {
                var actionClearAll = new MenuAction()
                {
                    Name = instances.Count() > 1 ?
                               $"Clear all in {instances.Count()} instances" :
                               "Clear all",
                    NeedsConfirmation = true,
                    Appearance = null,
                    TextColor = "var(--error)",
                    BorderColor = "var(--error)",
                    IconSmall = new Icons.Regular.Size16.Broom().WithColor("var(--error)"),
                    IconLarge = new Icons.Regular.Size20.Broom().WithColor("var(--error)")
                };

                actionClearAll.Action = async () =>
                {
                    foreach (var instance in instances)
                        Task.Run(async () =>
                        {
                            try
                            {
                                await _dataManager.ClearFactoryInstance(_session.User, instance);
                                _toastService.ShowSuccess($"{instance.QualifiedName} cleared");
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't clear {instance.QualifiedName}: {exc.Message}");
                            }
                        });
                };

                result.Add(actionClearAll);
            }
        }

        #endregion

        #region Convert to folder

        {
            if (instances.Count() == 1)
            {
                var instance = instances.First();

                var actionConvert = new MenuAction()
                {
                    Name = "Convert to folder",
                    NeedsConfirmation = true,
                    Appearance = null,
                    TextColor = "var(--error)",
                    BorderColor = "var(--error)",
                    IconSmall = new Icons.Regular.Size16.ArrowForward().WithColor("var(--error)"),
                    IconLarge = new Icons.Regular.Size20.ArrowForward().WithColor("var(--error)")
                };

                actionConvert.Action = async () =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var folderId = await _dataManager.ConvertFactoryInstanceToFolder(_session.User, _session.View, instance);
                            _toastService.ShowSuccess($"{instance.QualifiedName} converted to folder");

                            await _session.NavigateToAsync(new NavigationRequest
                            {
                                ProjectId = _session.ProjectId,
                                SpaceId = _session.SpaceId,
                                ViewId = _session.ViewId,
                                FolderId = folderId
                            });
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't convert {instance.QualifiedName} to folder: {exc.Message}");
                        }
                    });
                };

                result.Add(actionConvert);
            }
        }

        #endregion

        #region Delete

        {
            var actionDelete = new MenuAction()
            {
                Name = instances.Count() > 1 ?
                           $"Delete {instances.Count()} instances" :
                           "Delete instance",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.Delete().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.Delete().WithColor("var(--error)")
            };

            if (instances.Any(i => i.SubJobs.Any(j => j.Status.IsUnsettled())))
            {
                actionDelete.DisabledBecause = "Can't delete because some sub-jobs are active";
                actionDelete.IsDisabled = true;
            }
            else
            {
                actionDelete.Action = async () =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var instance in instances)
                            {
                                if (_factoryEditor.CurrentInstance == instance)
                                    await _factoryEditor.SetInstance(null);

                                await _dataManager.DeleteFactoryInstance(_session.User, instance.Space, instance);
                                _toastService.ShowSuccess($"{instance.QualifiedName} deleted");
                            }
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Error occurred while deleting factory instances: {exc.Message}");
                        }

                        await _selectionService.Clear();

                        await _session.NavigateToAsync(new NavigationRequest
                        {
                            ProjectId = _session.ProjectId,
                            SpaceId = _session.SpaceId,
                            ViewId = _session.ViewId
                        });
                    });
                };
            }

            result.Add(actionDelete);
        }

        #endregion

        return result;
    }

    /// <summary>
    /// Gets the list of available actions for one or more factory definitions.
    /// </summary>
    /// <param name="definitions">The factory definitions to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    public List<MenuAction> GetFactoryDefinitionActions(IEnumerable<ReadOnlyFactoryDefinition> definitions)
    {
        List<MenuAction> result = new();
        var defList = definitions.ToList();
        bool isSingle = defList.Count == 1;
        var first = defList.First();

        #region Clone

        if (isSingle)
        {
            var actionClone = new MenuAction()
            {
                Name = "Clone definition",
                NeedsConfirmation = false,
                Appearance = null,
                TextColor = null,
                BorderColor = null,
                IconSmall = new Icons.Regular.Size16.Copy(),
                IconLarge = new Icons.Regular.Size20.Copy()
            };

            actionClone.Action = async () =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await _dataManager.CloneFactoryDefinition(_session.User, _session.Space, first);
                        _toastService.ShowSuccess($"{first.QualifiedName} cloned");
                    }
                    catch (Exception exc)
                    {
                        _toastService.ShowError($"Couldn't clone {first.QualifiedName}: {exc.Message}");
                    }
                });
            };

            result.Add(actionClone);
        }

        #endregion

        #region Delete

        {
            var actionDelete = new MenuAction()
            {
                Name = isSingle ? "Delete definition" : $"Delete {defList.Count} definitions",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.Delete().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.Delete().WithColor("var(--error)")
            };

            var defsWithInstances = defList.Where(d =>
                _session.Space?.FactoryInstances.Any(i => i.DefinitionId == d.Id) == true).ToList();

            if (defsWithInstances.Any())
            {
                actionDelete.DisabledBecause = isSingle
                    ? "Can't delete because instances of this definition exist"
                    : $"Can't delete because {defsWithInstances.Count} definition(s) have instances";
                actionDelete.IsDisabled = true;
            }
            else
            {
                actionDelete.Action = async () =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var def in defList)
                                await _dataManager.DeleteFactoryDefinition(_session.User, _session.Space, def);

                            _toastService.ShowSuccess(isSingle
                                ? $"{first.QualifiedName} deleted"
                                : $"{defList.Count} definitions deleted");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't delete: {exc.Message}");
                        }

                        await _selectionService.Clear();
                    });
                };
            }

            result.Add(actionDelete);
        }

        #endregion

        return result;
    }

    /// <summary>
    /// Gets the list of available actions for jobs in queue view.
    /// </summary>
    /// <param name="jobs">The jobs to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    /// <remarks>
    /// This is a simplified version of GetJobActions specifically for the queue view.
    /// It only provides:
    ///
    /// - Abort: Stop running jobs
    ///
    /// The queue view is focused on monitoring job execution, so it offers
    /// fewer actions than the standard job view.
    /// </remarks>
    public List<MenuAction> GetQueueJobActions(IEnumerable<ReadOnlyJob> jobs)
    {
        List<MenuAction> result = new();

        #region Abort

        {
            if (jobs.Any(j => j.Status.IsOnCluster()))
            {
                var actionAbort = new MenuAction()
                {
                    Name = jobs.Count() > 1 ?
                               $"Abort {jobs.Count()} jobs" :
                               "Abort job",
                    NeedsConfirmation = true,
                    Appearance = null,
                    TextColor = "var(--error)",
                    BorderColor = "var(--error)",
                    IconSmall = new Icons.Regular.Size16.RecordStop().WithColor("var(--error)"),
                    IconLarge = new Icons.Regular.Size20.RecordStop().WithColor("var(--error)")
                };

                if (jobs.Any(j => !j.Status.IsOnCluster()))
                {
                    actionAbort.DisabledBecause = "Can't abort because some jobs are not active";
                    actionAbort.IsDisabled = true;
                }
                else
                {
                    actionAbort.Action = async () =>
                    {
                        foreach (var job in jobs)
                            try
                            {
                                await _dataManager.AbortJob(_session.User, job);
                                _toastService.ShowSuccess($"{job.QualifiedName} aborted");
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't abort {job.QualifiedName}: {exc.Message}");
                            }
                    };
                }

                result.Add(actionAbort);
            }
        }

        #endregion

        return result;
    }
}
