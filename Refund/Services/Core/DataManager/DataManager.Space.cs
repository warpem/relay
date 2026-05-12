using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new space within a project.
    /// </summary>
    /// <param name="user">The user creating the space</param>
    /// <param name="project">The project in which to create the space</param>
    /// <param name="template">Optional template space to copy properties from</param>
    /// <returns>A read-only wrapper of the created space</returns>
    /// <exception cref="Exception">Thrown if user or project cannot be found, or if space creation fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// It creates a new space with a unique ID, optionally copying properties from a template,
    /// and raises events to notify subscribers about the new space.
    ///
    /// A space serves as a container for jobs and their connections, representing a complete
    /// data processing workflow. Each space is created with a default view that provides
    /// a visual representation of the job graph. The space has its own directory structure
    /// for storing job data and resources.
    /// </remarks>
    public async Task<ReadOnlySpace> CreateSpace(ReadOnlyUser user, ReadOnlyProject project, Space template = null)
    {
        ReadOnlySpace createdSpace = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalProject = ResolveProject(project.Id);

                Space newSpace = _dataRepository.CreateSpace(originalUser, originalProject, template);
                createdSpace = newSpace.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create space for user {UserId} in project {ProjectId}", user.Id, project.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await SpaceCreated.InvokeHierarchy(createdSpace, GroupName.SpaceHierarchy(project.Id, null));

        return createdSpace;
    }

    /// <summary>
    /// Reconnects an existing space from disk to a project.
    /// </summary>
    /// <param name="user">The user reconnecting the space</param>
    /// <param name="project">The project to reconnect the space to</param>
    /// <param name="spacePath">The path to the space file</param>
    /// <returns>A read-only wrapper of the reconnected space</returns>
    /// <exception cref="Exception">Thrown if user or project cannot be found, or if reconnection fails</exception>
    /// <remarks>
    /// This method loads a space from disk and adds it to the specified project.
    /// If the space ID conflicts with an existing space in the project, a new ID is assigned.
    /// The space retains all its jobs, connections, and other data, but is now associated with
    /// the specified project.
    /// </remarks>
    public async Task<ReadOnlySpace> ReconnectSpace(ReadOnlyUser user, ReadOnlyProject project, string spacePath)
    {
        ReadOnlySpace reconnectedSpace = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalProject = ResolveProject(project.Id);

                // Delegate to the DataRepository to handle the actual reconnection
                Space space = _dataRepository.ReconnectSpace(originalUser, originalProject, spacePath, _userRepository.Users);
                reconnectedSpace = space.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to reconnect space from {SpacePath} for user {UserId} in project {ProjectId}", spacePath, user.Id, project.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await SpaceCreated.InvokeHierarchy(reconnectedSpace, GroupName.SpaceHierarchy(project.Id, null));

        return reconnectedSpace;
    }

    /// <summary>
    /// Updates an existing space by applying the specified update action.
    /// </summary>
    /// <param name="user">The user updating the space</param>
    /// <param name="space">The space to update</param>
    /// <param name="updateAction">The action to apply to the space</param>
    /// <returns>A task that completes when the update operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or space cannot be found, or if update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable space object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    ///
    /// Space updates typically involve modifying metadata such as the alias (display name),
    /// description, or other properties that don't affect the underlying job structure.
    /// More complex operations like adding or removing jobs are handled by specialized methods.
    /// </remarks>
    public async Task UpdateSpace(ReadOnlyUser user, ReadOnlySpace space, Action<Space> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);

                _dataRepository.UpdateSpace(originalUser, originalSpace, updateAction);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update space {SpaceId} by user {UserId}", space.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await SpaceUpdated.InvokeHierarchy(space, GroupName.SpaceHierarchy(space.Project.Id, space.Id));
    }

    /// <summary>
    /// Deletes an existing space from a project.
    /// </summary>
    /// <param name="user">The user deleting the space</param>
    /// <param name="space">The space to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or space cannot be found, or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the logical deletion in the data model and dispatching the appropriate events.
    /// The deletion occurs within a lock to ensure consistency. After deletion, events are raised to notify
    /// all interested subscribers.
    ///
    /// Deleting a space will remove all jobs, edges, and views contained within it. This operation
    /// is not reversible, and all data associated with the space (including job outputs and resources)
    /// will be permanently lost. The physical deletion of files is handled asynchronously to avoid
    /// blocking the UI during large space deletions.
    /// </remarks>
    public async Task DeleteSpace(ReadOnlyUser user, ReadOnlySpace space)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);

                _dataRepository.DeleteSpace(originalUser, originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete space {SpaceId} by user {UserId}", space.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await SpaceDeleted.InvokeHierarchy(space, GroupName.SpaceHierarchy(space.Project.Id, space.Id));
    }

    #endregion
}
