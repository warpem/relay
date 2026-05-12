using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.Session;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Services;

public partial class MenuActionService
{
    /// <summary>
    /// Gets the list of available actions for the given jobs.
    /// </summary>
    /// <param name="jobs">The jobs to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    /// <remarks>
    /// This method builds a comprehensive set of actions that can be performed on jobs:
    ///
    /// - Run: Queue jobs for execution (locally or on a cluster)
    /// - Edit: Open a job for editing
    /// - Abort: Stop running jobs
    /// - Clone: Create copies of jobs
    /// - Add to view: Add jobs to another view
    /// - Move to view: Move jobs from current view to another view
    /// - Remove from view: Remove jobs from the current view
    /// - Clear: Clean up job results
    /// - Delete: Permanently delete jobs
    ///
    /// Each action includes validation logic to determine if it should be enabled or disabled based on
    /// the current state of the jobs (e.g., can't delete running jobs, can't remove a job from a view
    /// if it's not in any other view).
    /// </remarks>
    public List<MenuAction> GetJobActions(IEnumerable<ReadOnlyJob> jobs)
    {
        List<MenuAction> result = new();

        // View context is required for job actions (move, clone, add to view, etc.)
        // In the factory builder, actions are handled separately by the builder screen.
        if (_session.View == null)
            return result;

        // In factory instance browse mode, only Run, Abort, Clear are allowed
        bool isBrowseMode = _session.FactoryInstance != null;

        #region Run

        {
            // If no job is in building state, don't add the action at all
            if (jobs.Any(j => j.Status == JobStatus.Building))
            {
                bool allLocal = jobs.All(j => typeof(ILocalJob).IsAssignableFrom(j.GetOriginalType()));
                bool allCluster = jobs.All(j => typeof(IClusterJob).IsAssignableFrom(j.GetOriginalType()));

                var actionRun = new MenuAction()
                {
                    Name = jobs.Count() > 1 ?
                               $"Run {jobs.Count()} jobs" :
                               "Run job",
                    NeedsConfirmation = false,
                    Appearance = Appearance.Accent,
                    TextColor = null,
                    BorderColor = null,
                    IconSmall = new Icons.Regular.Size16.Play(),
                    IconLarge = new Icons.Regular.Size20.Play()
                };

                if (jobs.Any(j => j.Status != JobStatus.Building))
                {
                    actionRun.IsDisabled = true;
                    actionRun.DisabledBecause = "Can't run because some jobs are not in Building state";
                }
                else if (jobs.Any(j => (j.ValidatePortInputs()?.Any() ?? false) ||
                                       (j.ValidateInputs()?.Any() ?? false)))
                {
                    actionRun.IsDisabled = true;
                    actionRun.DisabledBecause = "Can't run because some jobs aren't configured correctly";
                }
                else if (allLocal)
                {
                    actionRun.Action = async () =>
                    {
                        foreach (var job in jobs)
                            Task.Run(async () =>
                            {
                                try
                                {
                                    await _dataManager.QueueLocalJob(_session.User, job);
                                }
                                catch (Exception exc)
                                {
                                    _toastService.ShowError($"Couldn't run {job.QualifiedName} in local queue: {exc.Message}");
                                }
                            });
                    };
                }
                else
                {
                    // Find queues that can accommodate all jobs
                    var compatibleQueues = jobs.Select(j => _dataManager.ClusterQueues.Where(q => (q.QueueType&j.QueueType) != 0));
                    var commonQueues = compatibleQueues.First();

                    foreach (var queues in compatibleQueues)
                        commonQueues = commonQueues.Intersect(queues);

                    if (!commonQueues.Any())
                    {
                        actionRun.IsDisabled = true;
                        actionRun.DisabledBecause = "Can't run because there is no queue compatible with all selected jobs";
                    }
                    else
                    {
                        foreach (var queue in commonQueues)
                        {
                            MenuAction actionRunInQueue = new()
                            {
                                Name = $"Run in {queue.Alias}",
                            };

                            actionRunInQueue.Action = async () =>
                            {
                                foreach (var job in jobs)
                                    Task.Run(async () =>
                                    {
                                        try
                                        {
                                            if (typeof(IClusterJob).IsAssignableFrom(job.GetOriginalType()))
                                                await _dataManager.QueueClusterJob(_session.User, job, queue);
                                            else
                                                await _dataManager.QueueLocalJob(_session.User, job);
                                        }
                                        catch (Exception exc)
                                        {
                                            _toastService.ShowError($"Couldn't run {job.QualifiedName} in {queue.Alias}: {exc.Message}");
                                        }
                                    });
                            };

                            actionRun.SubActions.Add(actionRunInQueue);
                        }
                    }
                }

                result.Add(actionRun);
            }
        }

        #endregion

        #region Edit

        if (jobs.Count() == 1 && jobs.First().Status == JobStatus.Building)
        {
            var actionEdit = new MenuAction()
            {
                Name = "Edit job",
                DisabledBecause = "",
                NeedsConfirmation = false,
                Appearance = null,
                TextColor = null,
                BorderColor = null,
                IconSmall = new Icons.Regular.Size16.Edit(),
                IconLarge = new Icons.Regular.Size20.Edit()
            };

            actionEdit.Action = async () => await _jobEditor.SetJob(jobs.First());

            result.Add(actionEdit);
        }

        #endregion

        #region Abort

        {
            bool allOnCluster = jobs.All(j => j.Status.IsOnCluster());
            bool anyOnCluster = jobs.Any(j => j.Status.IsOnCluster());

            if (anyOnCluster)
            {
                bool IsOrphaned(ReadOnlyJob j) =>
                    j.Status.IsOnCluster()
                    && !_dataManager.LocalQueue.QueuedJobs.Contains(j)
                    && !_dataManager.ClusterQueues.Any(q => q.QueuedJobs.Contains(j));

                var onClusterJobs = jobs.Where(j => j.Status.IsOnCluster()).ToList();
                bool allOrphaned = onClusterJobs.All(IsOrphaned);
                bool anyOrphaned = onClusterJobs.Any(IsOrphaned);

                if (allOnCluster && allOrphaned)
                {
                    // All selected jobs are orphaned — offer force abort
                    var actionAbort = new MenuAction()
                    {
                        Name = jobs.Count() > 1
                            ? $"Abort {jobs.Count()} orphaned jobs"
                            : "Abort orphaned job",
                        NeedsConfirmation = true,
                        Appearance = null,
                        TextColor = "var(--error)",
                        BorderColor = "var(--error)",
                        IconSmall = new Icons.Regular.Size16.RecordStop().WithColor("var(--error)"),
                        IconLarge = new Icons.Regular.Size20.RecordStop().WithColor("var(--error)")
                    };

                    actionAbort.Action = async () =>
                    {
                        foreach (var job in jobs)
                            Task.Run(async () =>
                            {
                                try
                                {
                                    await _dataManager.ForceAbortOrphanedJob(_session.User, job);
                                    _toastService.ShowSuccess($"{job.QualifiedName} aborted");
                                }
                                catch (Exception exc)
                                {
                                    _toastService.ShowError($"Couldn't abort {job.QualifiedName}: {exc.Message}");
                                }
                            });
                    };

                    result.Add(actionAbort);
                }
                else if (!anyOrphaned)
                {
                    // No orphans — regular abort flow
                    var actionAbort = new MenuAction()
                    {
                        Name = jobs.Count() > 1
                            ? $"Abort {jobs.Count()} jobs"
                            : "Abort job",
                        NeedsConfirmation = true,
                        Appearance = null,
                        TextColor = "var(--error)",
                        BorderColor = "var(--error)",
                        IconSmall = new Icons.Regular.Size16.RecordStop().WithColor("var(--error)"),
                        IconLarge = new Icons.Regular.Size20.RecordStop().WithColor("var(--error)")
                    };

                    if (jobs.Any(j => !j.Status.IsOnCluster() || j.Status == JobStatus.Aborting))
                    {
                        actionAbort.DisabledBecause = "Can't abort because some jobs are not active";
                        actionAbort.IsDisabled = true;
                    }
                    else
                    {
                        actionAbort.Action = async () =>
                        {
                            foreach (var job in jobs)
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await _dataManager.AbortJob(_session.User, job);
                                        _toastService.ShowSuccess($"{job.QualifiedName} aborted");
                                    }
                                    catch (Exception exc)
                                    {
                                        _toastService.ShowError($"Couldn't abort {job.QualifiedName}: {exc.Message}");
                                    }
                                });
                        };
                    }

                    result.Add(actionAbort);
                }
                // Mixed state (some orphaned, some not) — show neither action
            }
        }

        #endregion

        // In factory instance browse mode, skip all actions except Run, Abort, Clear
        if (!isBrowseMode)
        {

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
                        foreach (var job in jobs)
                        {
                            try
                            {
                                await _dataManager.UpdateJob(_session.User, job, j => j.ColorTag = color);
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't set color on {job.QualifiedName}: {exc.Message}");
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
                    foreach (var job in jobs)
                    {
                        try
                        {
                            await _dataManager.UpdateJob(_session.User, job, j => j.ColorTag = null);
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't remove color from {job.QualifiedName}: {exc.Message}");
                        }
                    }
                }
            });

            result.Add(actionColor);
        }

        #endregion

        #region Create factory from selection

        if (jobs.Any())
        {
            var actionCreateFactory = new MenuAction()
            {
                Name = "Create factory from selection",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.BuildingFactory(),
                IconLarge = new Icons.Regular.Size20.BuildingFactory()
            };

            actionCreateFactory.Action = async () =>
            {
                try
                {
                    // Collect edges between selected jobs
                    var jobSet = new HashSet<int>(jobs.Select(j => j.Id));
                    var selectedEdges = jobs
                        .SelectMany(j => j.PortsIn.Values
                            .SelectMany(p => p.Edges)
                            .Where(e => jobSet.Contains(e.Source.Job.Id)))
                        .Distinct();

                    var def = await _dataManager.CreateFactoryDefinitionFromJobs(
                        _session.User, _session.Space, jobs, selectedEdges);
                    await _session.NavigateToAsync(new NavigationRequest
                    {
                        ProjectId = _session.Project.Id,
                        SpaceId = _session.Space.Id,
                        FactoryDefinitionId = def.Id
                    });
                }
                catch (Exception exc)
                {
                    _toastService.ShowError("Couldn't create factory: " + exc.Message);
                }
            };

            result.Add(actionCreateFactory);
        }

        #endregion

        #region Clone

        {
            var jobSet = new HashSet<ReadOnlyJob>(jobs);
            bool areInterconnected = jobSet.Count > 1 && jobs.Any(j =>
                j.GetParents().Any(p => jobSet.Contains(p)) ||
                j.GetChildren().Any(c => jobSet.Contains(c)));

            // Factory: creates the clone action body for a given target view
            Func<ReadOnlyView, Func<Task>> makeCloneAction;
            string cloneLabel;

            if (areInterconnected)
            {
                cloneLabel = $"Clone {jobs.Count()} jobs as tree";
                makeCloneAction = (view) => async () =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            await _dataManager.CloneJobTree(_session.User, jobs, view);
                            _toastService.ShowSuccess($"Cloned {jobs.Count()} jobs as tree");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't clone job tree: {exc.Message}");
                        }
                    });
                };
            }
            else
            {
                cloneLabel = jobs.Count() > 1 ? $"Clone {jobs.Count()} jobs" : "Clone job";
                makeCloneAction = (view) => async () =>
                {
                    foreach (var job in jobs)
                        try
                        {
                            var clone = await _dataManager.CloneJob(_session.User, job, view);

                            var folder = view.FindFolderContainingJob(job.Id);
                            if (folder != null)
                                await _dataManager.MoveJobToFolder(_session.User, view, clone, folder);

                            _toastService.ShowSuccess($"{job.QualifiedName} cloned");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't clone {job.QualifiedName}: {exc.Message}");
                        }
                };
            }

            // Clone into current view
            result.Add(new MenuAction()
            {
                Name = cloneLabel,
                DisabledBecause = "",
                NeedsConfirmation = false,
                Appearance = null,
                TextColor = null,
                BorderColor = null,
                IconSmall = new Icons.Regular.Size16.Copy(),
                IconLarge = new Icons.Regular.Size20.Copy(),
                Action = makeCloneAction(_session.View)
            });

            // Clone into another view (sub-menu)
            var actionCloneToView = new MenuAction()
            {
                Name = cloneLabel + " into view",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.Copy(),
                IconLarge = new Icons.Regular.Size20.Copy()
            };

            var availableViews = jobs.First().Space.Views.Reverse()
                .Where(v => v != _session.View);

            if (!availableViews.Any())
            {
                actionCloneToView.DisabledBecause = "No other views available";
                actionCloneToView.IsDisabled = true;
            }
            else
            {
                foreach (var view in availableViews)
                {
                    actionCloneToView.SubActions.Add(new MenuAction()
                    {
                        Name = view.Alias,
                        Action = makeCloneAction(view)
                    });
                }
            }

            result.Add(actionCloneToView);
        }

        #endregion

        #region Clone and connect to self

        if (jobs.Count() == 1 &&
            jobs.First().PortsOut.Any() &&
            jobs.First().PortsIn.Any())
        {
            var job = jobs.First();

            // Figure out if there are any ports we can connect to the original job

            List<ReadOnlyPortOut> portsOut = job.PortsOut.Values.ToList();
            List<ReadOnlyPortIn> portsIn = job.PortsIn.Values.ToList();
            List<(string, string)> possibleConnections = new();
            foreach (var portOut in portsOut)
                foreach (var portIn in portsIn)
                    if (portOut.ResourceType == portIn.ResourceType)
                    {
                        possibleConnections.Add((portOut.Name, portIn.Name));
                        portsIn.Remove(portIn);
                        break;
                    }

            if (possibleConnections.Any())
            {
                var actionCloneConnect = new MenuAction()
                {
                    Name = "Clone and connect",
                    DisabledBecause = "",
                    NeedsConfirmation = false,
                    Appearance = null,
                    TextColor = null,
                    BorderColor = null,
                    IconSmall = new Icons.Regular.Size16.CopyArrowRight(),
                    IconLarge = new Icons.Regular.Size20.CopyArrowRight()
                };

                actionCloneConnect.Action = async () =>
                {
                    try
                    {
                        var clone = await _dataManager.CloneJob(_session.User, job, _session.View);

                        var cloneFolder = _session.View.FindFolderContainingJob(job.Id);
                        if (cloneFolder != null)
                            await _dataManager.MoveJobToFolder(_session.User, _session.View, clone, cloneFolder);

                        _toastService.ShowSuccess($"{job.QualifiedName} cloned");

                        foreach (var (_, inName) in possibleConnections)
                            foreach (var edge in clone.PortsIn[inName].Edges)
                                await _dataManager.DeleteEdge(edge);

                        foreach (var (outName, inName) in possibleConnections)
                            await _dataManager.CreateEdge(job.Space,
                                                          job.PortsOut[outName],
                                                          clone.PortsIn[inName]);

                        _toastService.ShowSuccess($"{job.QualifiedName} connected to clone");
                    }
                    catch (Exception exc)
                    {
                        _toastService.ShowError($"Couldn't clone and connect {job.QualifiedName}: {exc.Message}");
                    }
                };

                result.Add(actionCloneConnect);
            }
        }

        #endregion

        } // end if (!isBrowseMode) — resume with Finalize and Download card, which are allowed

        #region Finalize

        {
            // If no job is in failed or aborted state, don't add the action at all
            if (jobs.Any(j => (j.Status == JobStatus.Failed ||
                               j.Status == JobStatus.Aborted) &&
                              j.CanBeFinalized))
            {
                var actionFinalize = new MenuAction()
                {
                    Name = jobs.Count() > 1 ?
                               $"Finalize {jobs.Count()} jobs" :
                               "Finalize job",
                    NeedsConfirmation = false,
                    Appearance = Appearance.Accent,
                    TextColor = null,
                    BorderColor = null,
                    IconSmall = new Icons.Regular.Size16.Flag(),
                    IconLarge = new Icons.Regular.Size20.FlagCheckered()
                };

                if (jobs.Any(j => j.Status != JobStatus.Failed && j.Status != JobStatus.Aborted))
                {
                    actionFinalize.IsDisabled = true;
                    actionFinalize.DisabledBecause = "Can't finalize because some jobs are not in Failed or Aborted state";
                }
                else if (jobs.Any(j => !j.CanBeFinalized))
                {
                    actionFinalize.IsDisabled = true;
                    actionFinalize.DisabledBecause = "Can't finalize because some jobs are not finalizeable";
                }
                else
                {
                    actionFinalize.Action = async () =>
                    {
                        foreach (var job in jobs)
                            try
                            {
                                await _dataManager.FinalizeLocalJob(_session.User, job);
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't finalize {job.QualifiedName} in local queue: {exc.Message}");
                            }
                    };
                }

                result.Add(actionFinalize);
            }
        }

        #endregion

        #region Download card

        {
            if (jobs.Count() == 1)
            {
                var job = jobs.First();

                if (job.VisAvailableIteration >= 0)
                {
                    int iter = job.IsIterative ? job.VisAvailableIteration : 0;
                    string pngPath = job.VisCard(iter);
                    string pdfPath = job.VisCardPdf(iter);
                    bool pngExists = File.Exists(pngPath);
                    bool pdfExists = File.Exists(pdfPath);
                    string baseName = $"P{job.Space.Project.Id}_S{job.Space.Id}_J{job.Id}_card";

                    if (pngExists || pdfExists)
                    {
                        var actionDownloadCard = new MenuAction()
                        {
                            Name = "Download card",
                            NeedsConfirmation = false,
                            Appearance = null,
                            IconSmall = new Icons.Regular.Size16.Image(),
                            IconLarge = new Icons.Regular.Size20.Image()
                        };

                        if (pngExists)
                            actionDownloadCard.SubActions.Add(new MenuAction()
                            {
                                Name = "PNG",
                                Action = async () =>
                                {
                                    string url = _fileService.GetUrl(pngPath);
                                    await _jsRuntime.InvokeVoidAsync("downloadFile", url, $"{baseName}.png");
                                }
                            });

                        if (pdfExists)
                            actionDownloadCard.SubActions.Add(new MenuAction()
                            {
                                Name = "PDF",
                                Action = async () =>
                                {
                                    string url = _fileService.GetUrl(pdfPath);
                                    await _jsRuntime.InvokeVoidAsync("downloadFile", url, $"{baseName}.pdf");
                                }
                            });

                        result.Add(actionDownloadCard);
                    }
                }
            }
        }

        #endregion

        if (!isBrowseMode)
        {

        #region Add to view

        {
            var actionAddToView = new MenuAction()
            {
                Name = jobs.Count() > 1 ?
                           $"Add {jobs.Count()} jobs to another view" :
                           "Add job to another view",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.LinkAdd(),
                IconLarge = new Icons.Regular.Size20.LinkAdd()
            };

            bool selectionHasFolders = _selectionService.IdsOfType(ItemType.Folder).Any();

            if (selectionHasFolders)
            {
                actionAddToView.DisabledBecause = "Use folder actions to add folders to another view";
                actionAddToView.IsDisabled = true;
            }
            else
            {
                var availableViews = jobs.First()
                                         .Space.Views.Reverse().Where(v => v != _session.View &&
                                                                 jobs.All(j => !v.Jobs.Contains(j)));

                if (!availableViews.Any())
                {
                    actionAddToView.DisabledBecause = "No other views available that none of the selected jobs are part of";
                    actionAddToView.IsDisabled = true;
                }
                else
                {
                    foreach (var view in availableViews)
                    {
                        Func<ReadOnlyFolder, Task> addAction = async (targetFolder) =>
                        {
                            foreach (var job in jobs)
                                try
                                {
                                    await _dataManager.AddJobToView(_session.User, view, job);
                                    if (targetFolder != null)
                                        await _dataManager.MoveJobToFolder(_session.User, view, job, targetFolder);
                                    _toastService.ShowSuccess($"{job.QualifiedName} added to {view.QualifiedName}");
                                }
                                catch (Exception exc)
                                {
                                    _toastService.ShowError($"Couldn't add {job.QualifiedName} to {view.QualifiedName}: {exc.Message}");
                                }
                        };

                        AddViewDestination(actionAddToView, view, addAction);
                    }
                }
            }

            result.Add(actionAddToView);
        }

        #endregion

        #region Move to view

        {
            var actionMoveToView = new MenuAction()
            {
                Name = jobs.Count() > 1 ?
                           $"Move {jobs.Count()} jobs" :
                           "Move job",
                NeedsConfirmation = false,
                Appearance = null,
                IconSmall = new Icons.Regular.Size16.ArrowForward(),
                IconLarge = new Icons.Regular.Size20.ArrowForward()
            };

            bool moveFoldersSelected = _selectionService.IdsOfType(ItemType.Folder).Any();

            if (moveFoldersSelected)
            {
                actionMoveToView.DisabledBecause = "Use folder actions to move folders to another view";
                actionMoveToView.IsDisabled = true;
            }
            else
            {
                var currentView = _session.View;
                bool hasCurrentViewEntry = false;

                // Current view entry: intra-view folder movement
                if (currentView.Folders.Count > 0)
                {
                    // Determine which folder to exclude: if all selected jobs share the same parent folder, exclude it
                    int? excludeFolderId = null;
                    var parentFolders = jobs.Select(j => currentView.FindFolderContainingJob(j.Id)).ToList();
                    if (parentFolders.All(f => f != null && f.Id == parentFolders[0]?.Id))
                        excludeFolderId = parentFolders[0]?.Id;

                    bool allAtRoot = parentFolders.All(f => f == null);

                    Func<ReadOnlyFolder, Task> intraViewMoveAction = async (targetFolder) =>
                    {
                        foreach (var job in jobs)
                            try
                            {
                                await _dataManager.MoveJobToFolder(_session.User, currentView, job, targetFolder);
                            }
                            catch (Exception exc)
                            {
                                _toastService.ShowError($"Couldn't move {job.QualifiedName}: {exc.Message}");
                            }
                    };

                    var currentViewAction = new MenuAction() { Name = $"{currentView.Alias} (current)" };

                    // Show "(root level)" only if not all jobs are already at root
                    if (!allAtRoot)
                    {
                        currentViewAction.SubActions.Add(new MenuAction()
                        {
                            Name = "(root level)",
                            Action = async () => await intraViewMoveAction(null)
                        });
                    }

                    BuildFolderSubActions(currentViewAction, currentView.Folders.Where(f => f.Parent == null), intraViewMoveAction, excludeFolderId);

                    // Only add current view entry if there are actual destinations
                    if (currentViewAction.SubActions.Count > 0)
                    {
                        actionMoveToView.SubActions.Add(currentViewAction);
                        hasCurrentViewEntry = true;
                    }
                }

                // Other views: cross-view move
                var availableViews = jobs.First()
                                         .Space.Views.Reverse().Where(v => v != currentView &&
                                                                 jobs.All(j => !v.Jobs.Contains(j)));

                foreach (var view in availableViews)
                {
                    Func<ReadOnlyFolder, Task> moveAction = async (targetFolder) =>
                    {
                        var jobList = jobs.ToList();

                        Task.Run(async () =>
                        {
                            await _selectionService.Clear();

                            foreach (var job in jobList)
                                try
                                {
                                    await _dataManager.AddJobToView(_session.User, view, job);
                                    if (targetFolder != null)
                                        await _dataManager.MoveJobToFolder(_session.User, view, job, targetFolder);
                                    await _dataManager.RemoveJobFromView(_session.User, currentView, job);
                                    _toastService.ShowSuccess($"{job.QualifiedName} moved to {view.QualifiedName}");
                                }
                                catch (Exception exc)
                                {
                                    _toastService.ShowError($"Couldn't move {job.QualifiedName} to {view.QualifiedName}: {exc.Message}");
                                }
                        });
                    };

                    AddViewDestination(actionMoveToView, view, moveAction);
                }

                if (!hasCurrentViewEntry && !availableViews.Any())
                {
                    actionMoveToView.DisabledBecause = "No destinations available";
                    actionMoveToView.IsDisabled = true;
                }
            }

            result.Add(actionMoveToView);
        }

        #endregion

        #region Remove from view

        {
            var actionRemoveFromView = new MenuAction()
            {
                Name = jobs.Count() > 1 ?
                           $"Remove {jobs.Count()} jobs from this view" :
                           "Remove job from this view",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.LinkDismiss().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.LinkDismiss().WithColor("var(--error)")
            };

            var currentView = _session.View;

            if (!jobs.All(j => j.Space.Views.Any(v => v != currentView && v.Jobs.Contains(j))))
            {
                actionRemoveFromView.DisabledBecause = "Some jobs would no longer be part of any view";
                actionRemoveFromView.IsDisabled = true;
            }
            else
            {
                actionRemoveFromView.Action = async () =>
                {
                    await _selectionService.Clear();
                    foreach (var job in jobs)
                        try
                        {
                            await _dataManager.RemoveJobFromView(_session.User, currentView, job);
                            _toastService.ShowSuccess($"{job.QualifiedName} removed from {currentView.QualifiedName}");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't remove {job.QualifiedName} from {currentView.QualifiedName}: {exc.Message}");
                        }
                };
            }

            result.Add(actionRemoveFromView);
        }

        #endregion

        } // end if (!isBrowseMode) — resume with Clear, which is allowed

        #region Clear

        {
            if (jobs.Any(j => j.CanTransitionState(JobStatus.Clearing)))
            {
                var actionClear = new MenuAction()
                {
                    Name = jobs.Count() > 1 ?
                               $"Clear {jobs.Count()} jobs" :
                               "Clear job",
                    NeedsConfirmation = true,
                    Appearance = null,
                    TextColor = "var(--error)",
                    BorderColor = "var(--error)",
                    IconSmall = new Icons.Regular.Size16.Broom().WithColor("var(--error)"),
                    IconLarge = new Icons.Regular.Size20.Broom().WithColor("var(--error)")
                };

                if (jobs.Any(j => j.Status.IsUnsettled()))
                {
                    actionClear.DisabledBecause = "Can't clear because some jobs are active";
                    actionClear.IsDisabled = true;
                }
                else if (jobs.Any(j => !j.CanTransitionState(JobStatus.Clearing)))
                {
                    actionClear.DisabledBecause = "Can't clear because some jobs don't have any results to clear";
                    actionClear.IsDisabled = true;
                }
                else
                    actionClear.Action = async () =>
                    {
                        foreach (var job in jobs)
                            Task.Run(async () =>
                            {
                                try
                                {
                                    await _dataManager.ClearJob(_session.User, job);
                                    _toastService.ShowSuccess($"{job.QualifiedName} cleared");
                                }
                                catch (Exception exc)
                                {
                                    _toastService.ShowError($"Couldn't clear {job.QualifiedName}: {exc.Message}");
                                }
                            });
                    };

                result.Add(actionClear);
            }
        }

        #endregion

        if (!isBrowseMode)
        {

        #region Delete

        {
            var actionDelete = new MenuAction()
            {
                Name = jobs.Count() > 1 ?
                           $"Delete {jobs.Count()} jobs" :
                           "Delete job",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.Delete().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.Delete().WithColor("var(--error)")
            };

            if (jobs.Any(j => j.Status.IsUnsettled()))
            {
                actionDelete.DisabledBecause = "Can't delete because some jobs are active";
                actionDelete.IsDisabled = true;
            }
            else if (jobs.Any(j1 => j1.GetChildren().Any(c => !jobs.Contains(c))))
            {
                actionDelete.DisabledBecause = "Can't delete because some child jobs would become orphaned";
                actionDelete.IsDisabled = true;
            }
            else
                actionDelete.Action = async () =>
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            // If there are multiple jobs to be deleted, run multiple rounds of deletion where
                            // each round deletes jobs that are currently leaves, i.e. don't have any child jobs

                            var jobsToDelete = jobs.ToList();

                            while (jobsToDelete.Any())
                            {
                                var leaves = jobsToDelete.Where(j => !j.GetChildren().Any()).ToList();

                                if (!leaves.Any() && jobsToDelete.Any())
                                    throw new Exception($"Still some jobs left to delete, but none of them are leaves:\n" +
                                                        $"{string.Join(",\n", jobsToDelete.Select(j => j.QualifiedName))}");

                                jobsToDelete.RemoveAll(j => leaves.Contains(j));

                                foreach (var job in leaves)
                                {
                                    if (_jobEditor.CurrentJob == job)
                                        await _jobEditor.SetJob(null);

                                    await _dataManager.DeleteJob(_session.User, job);
                                    _toastService.ShowSuccess($"{job.QualifiedName} deleted");
                                }
                            }
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Error occurred while deleting jobs: {exc.Message}");
                        }

                        await _selectionService.Clear();
                    });
                };

            result.Add(actionDelete);
        }

        #endregion

        } // end if (!isBrowseMode)

        return result;
    }
}
