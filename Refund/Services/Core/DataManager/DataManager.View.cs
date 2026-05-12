using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new view within a space.
    /// </summary>
    /// <param name="user">The user creating the view</param>
    /// <param name="space">The space in which to create the view</param>
    /// <param name="template">Optional template view to copy properties from</param>
    /// <returns>A read-only wrapper of the created view</returns>
    /// <exception cref="Exception">Thrown if user or space cannot be found, or if view creation fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// It creates a new view with a unique ID, optionally copying properties from a template,
    /// and raises events to notify subscribers about the new view and the updated space.
    ///
    /// A view provides a specific visualization or perspective of the jobs in a space. Each view
    /// has its own layout settings that control the visual positioning of jobs. A space can have
    /// multiple views, allowing users to organize jobs in different ways for different purposes
    /// (e.g., by workflow stage, by job type, or by data dependency).
    /// </remarks>
    public async Task<ReadOnlyView> CreateView(ReadOnlyUser user, ReadOnlySpace space, View template = null)
    {
        ReadOnlyView createdView = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);

                View newView = _dataRepository.CreateView(originalUser, originalSpace, template);
                createdView = newView.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create view for user {UserId} in space {SpaceId}", user.Id, space.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewCreated.InvokeHierarchy(createdView, GroupName.ViewHierarchy(space.Project.Id, space.Id, null));

        await SpaceUpdated.InvokeHierarchy(createdView.Space, GroupName.SpaceHierarchy(createdView.Space.Project.Id, createdView.Space.Id));

        return createdView;
    }

    /// <summary>
    /// Updates an existing view by applying the specified update action.
    /// </summary>
    /// <param name="user">The user updating the view</param>
    /// <param name="view">The view to update</param>
    /// <param name="updateAction">The action to apply to the view</param>
    /// <returns>A task that completes when the update operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or view cannot be found, or if update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable view object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    ///
    /// View updates typically involve modifying metadata such as the alias (display name),
    /// description, or other properties that don't affect the underlying job structure.
    /// More complex operations like adding or removing jobs, or changing layouts, are handled
    /// by specialized methods.
    /// </remarks>
    public async Task UpdateView(ReadOnlyUser user, ReadOnlyView view, Action<View> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                _dataRepository.UpdateView(originalUser, originalView, updateAction);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update view {ViewId} by user {UserId}", view.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Deletes an existing view from a space.
    /// </summary>
    /// <param name="user">The user deleting the view</param>
    /// <param name="view">The view to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or view cannot be found, or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the logical deletion in the data model and dispatching the appropriate events.
    /// The deletion occurs within a lock to ensure consistency. After deletion, events are raised to notify
    /// all interested subscribers about the view deletion and the space update.
    ///
    /// Deleting a view removes only the visual representation and layout information for jobs,
    /// not the jobs themselves or their data. If the view being deleted is the last view in a space,
    /// the operation will fail, as each space must have at least one view.
    /// </remarks>
    public async Task DeleteView(ReadOnlyUser user, ReadOnlyView view)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                _dataRepository.DeleteView(originalUser, originalView);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete view {ViewId} by user {UserId}", view.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewDeleted.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));

        await SpaceUpdated.InvokeHierarchy(view.Space, GroupName.SpaceHierarchy(view.Space.Project.Id, view.Space.Id));
    }

    /// <summary>
    /// Adds a job to a view, making it visible in that visualization.
    /// </summary>
    /// <param name="user">The user adding the job to the view</param>
    /// <param name="view">The view to add the job to</param>
    /// <param name="job">The job to add to the view</param>
    /// <returns>A task that completes when the addition operation is finished</returns>
    /// <exception cref="Exception">Thrown if user, view, or job cannot be found, or if addition fails</exception>
    /// <remarks>
    /// This method handles adding a job to a view's visible job collection. The job must already
    /// exist in the space, and this operation only affects its visibility in the specific view.
    /// After the addition, events are raised to notify subscribers about both the view update
    /// and the job update.
    ///
    /// Adding a job to a view makes it visible in that particular visualization of the space's
    /// workflow.
    /// </remarks>
    public async Task AddJobToView(ReadOnlyUser user, ReadOnlyView view, ReadOnlyJob job)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                _dataRepository.AddJobToView(originalUser, originalView, originalJob);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to add job {JobId} to view {ViewId} by user {UserId}", job.Id, view.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    /// <summary>
    /// Removes a job from a view, hiding it in that visualization.
    /// </summary>
    /// <param name="user">The user removing the job from the view</param>
    /// <param name="view">The view to remove the job from</param>
    /// <param name="job">The job to remove from the view</param>
    /// <returns>A task that completes when the removal operation is finished</returns>
    /// <exception cref="Exception">Thrown if user, view, or job cannot be found, or if removal fails</exception>
    /// <remarks>
    /// This method handles removing a job from a view's visible job collection. The job remains
    /// in the space, and this operation only affects its visibility in the specific view.
    /// After the removal, events are raised to notify subscribers about both the view update
    /// and the job update.
    ///
    /// Removing a job from a view hides it in that particular visualization of the space's
    /// workflow. This can be useful for focusing on specific parts of a complex workflow
    /// by hiding jobs that are not relevant to the current task.
    /// </remarks>
    public async Task RemoveJobFromView(ReadOnlyUser user, ReadOnlyView view, ReadOnlyJob job)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                _dataRepository.RemoveJobFromView(originalUser, originalView, originalJob);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to remove job {JobId} from view {ViewId} by user {UserId}", job.Id, view.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    /// <summary>
    /// Moves a job to a new position within a view's job list.
    /// </summary>
    /// <param name="user">The user performing the reorder</param>
    /// <param name="view">The view containing the job</param>
    /// <param name="job">The job to move</param>
    /// <param name="newIndex">The target index for the job</param>
    public async Task ReorderJobInView(ReadOnlyUser user, ReadOnlyView view, ReadOnlyJob job, int newIndex)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                _dataRepository.UpdateView(originalUser, originalView, v => v.MoveJob(originalJob, newIndex));
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to reorder job {JobId} in view {ViewId} by user {UserId}", job.Id, view.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Moves any root-level item (job or folder) to a new position within a view's root items list.
    /// </summary>
    /// <param name="user">The user performing the reorder</param>
    /// <param name="view">The view containing the item</param>
    /// <param name="item">The item to move (job or folder)</param>
    /// <param name="newIndex">The target index for the item</param>
    public async Task ReorderItemInView(ReadOnlyUser user, ReadOnlyView view, IViewItem item, int newIndex)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                IFolderContent mutableItem = item.ItemType switch
                {
                    ItemType.Job => _dataRepository.FindJob(view.Space.Project.Id, view.Space.Id, item.Id)
                        ?? throw new Exception($"Job {item.Id} not found"),
                    ItemType.Folder => (IFolderContent)(originalView.FindFolder(item.Id)
                        ?? throw new Exception($"Folder {item.Id} not found")),
                    _ => throw new Exception($"Unknown item type: {item.ItemType}")
                };

                _dataRepository.UpdateView(originalUser, originalView, v => v.MoveItem(mutableItem, newIndex));
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to reorder item {ItemId} in view {ViewId} by user {UserId}", item.Id, view.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    #endregion
}
