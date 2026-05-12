using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.Session;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Services;

public partial class MenuActionService
{
    /// <summary>
    /// Gets the list of available actions for the given projects.
    /// </summary>
    /// <param name="projects">The projects to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    /// <remarks>
    /// Currently supports:
    /// - Delete: Deletes projects that have no spaces
    /// </remarks>
    public List<MenuAction> GetProjectActions(IEnumerable<ReadOnlyProject> projects)
    {
        List<MenuAction> result = new();

        // Delete
        {
            var actionDelete = new MenuAction()
            {
                Name = projects.Count() > 1 ?
                           $"Delete {projects.Count()} projects" :
                           "Delete project",
                DisabledBecause = projects.Count() > 1 ?
                                      "Can't delete, at least one project is not empty" :
                                      "Can't delete non-empty project",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.Delete().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.Delete().WithColor("var(--error)")
            };

            if (projects.Any(p => p.Spaces.Any()))
                actionDelete.IsDisabled = true;
            else
                actionDelete.Action = async () =>
                {
                    foreach (var project in projects)
                        try
                        {
                            await _dataManager.DeleteProject(project);
                            _toastService.ShowSuccess($"{project.QualifiedName} deleted");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't delete {project.QualifiedName}: {exc.Message}");
                        }

                    await _selectionService.Clear();
                };

            result.Add(actionDelete);
        }

        return result;
    }

    /// <summary>
    /// Gets the list of available actions for the given spaces.
    /// </summary>
    /// <param name="spaces">The spaces to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    /// <remarks>
    /// Currently supports:
    /// - Disconnect: Removes spaces from the project (if they have no active jobs)
    /// </remarks>
    public List<MenuAction> GetSpaceActions(IEnumerable<ReadOnlySpace> spaces)
    {
        List<MenuAction> result = new();

        // Disconnect
        {
            var actionDisconnect = new MenuAction()
            {
                Name = spaces.Count() > 1 ?
                           $"Disconnect {spaces.Count()} spaces" :
                           "Disconnect space",
                DisabledBecause = "Can't disconnect because some jobs are active",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.PlugDisconnected().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.PlugDisconnected().WithColor("var(--error)")
            };

            if (spaces.Any(s => s.Jobs.Any(j => j.Status.IsUnsettled())))
                actionDisconnect.IsDisabled = true;
            else
                actionDisconnect.Action = async () =>
                {
                    foreach (var space in spaces)
                        try
                        {
                            await _dataManager.DeleteSpace(_session.User, space);

                            await _session.NavigateToAsync(new()
                            {
                                ProjectId = space.Project.Id,
                                Overlay = _session.CurrentOverlay
                            });

                            _toastService.ShowSuccess($"{space.QualifiedName} disconnected");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't disconnect {space.QualifiedName}: {exc.Message}");
                        }

                    await _selectionService.Clear();
                };

            result.Add(actionDisconnect);
        }

        return result;
    }

    /// <summary>
    /// Gets the list of available actions for the given views.
    /// </summary>
    /// <param name="views">The views to get actions for</param>
    /// <returns>A list of available menu actions</returns>
    /// <remarks>
    /// Currently supports:
    /// - Delete: Deletes views (if all jobs in the views are also in other views)
    ///
    /// Views can only be deleted if doing so wouldn't leave any jobs without a view.
    /// This ensures that all jobs remain accessible through at least one view.
    /// </remarks>
    public List<MenuAction> GetViewActions(IEnumerable<ReadOnlyView> views)
    {
        List<MenuAction> result = new();

        // Delete
        {
            var actionDelete = new MenuAction()
            {
                Name = views.Count() > 1 ?
                           $"Delete {views.Count()} views" :
                           "Delete view",
                DisabledBecause = "Can't delete, at least one job would be left without a view",
                NeedsConfirmation = true,
                Appearance = null,
                TextColor = "var(--error)",
                BorderColor = "var(--error)",
                IconSmall = new Icons.Regular.Size16.Delete().WithColor("var(--error)"),
                IconLarge = new Icons.Regular.Size20.Delete().WithColor("var(--error)")
            };

            var parentSpace = views.First().Space;

            // Check if deleting these views would leave any job without a view
            // For each view we're deleting, check if all its jobs are in at least one other view
            // that we're not deleting
            if (views.Any(v => v.Jobs.Any(j => !parentSpace.Views.Any(vv => !views.Contains(vv) &&
                                                                            vv.Jobs.Contains(j)))))
                actionDelete.IsDisabled = true;
            else
                actionDelete.Action = async () =>
                {
                    foreach (var view in views)
                        try
                        {
                            await _dataManager.DeleteView(_session.User, view);
                            _toastService.ShowSuccess($"{view.QualifiedName} deleted");
                        }
                        catch (Exception exc)
                        {
                            _toastService.ShowError($"Couldn't delete {view.QualifiedName}: {exc.Message}");
                        }

                    await _selectionService.Clear();
                };

            result.Add(actionDelete);
        }

        return result;
    }
}
