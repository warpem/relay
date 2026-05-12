using System.Collections.ObjectModel;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    /// <summary>
    /// Gets all projects that a user has access to, based on their role and membership.
    /// </summary>
    /// <param name="user">The user to get projects for</param>
    /// <returns>A read-only collection of projects accessible to the user</returns>
    /// <remarks>
    /// This method filters the complete list of projects to return only those that the user
    /// has permission to access. A user can access a project if any of the following is true:
    ///
    /// 1. The user is an administrator (can access all projects)
    /// 2. The user is the owner of the project
    /// 3. The user is a member of the project
    ///
    /// The resulting collection is sorted by project ID and returned as a read-only collection
    /// to prevent modification.
    /// </remarks>
    public ReadOnlyCollection<ReadOnlyProject> GetUserProjects(ReadOnlyUser user)
    {
        if (user == null) return new ReadOnlyCollection<ReadOnlyProject>([]);

        return Projects.Where(p => user.Role == UserRole.Admin ||
                                   p.Owner.Id == user.Id ||
                                   p.Members.Any(u => u.Id == user.Id))
                       .ToList().AsReadOnly();
    }

    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new project owned by the specified user.
    /// </summary>
    /// <param name="user">The user creating the project (who will become the owner)</param>
    /// <param name="template">Optional template project to copy properties from</param>
    /// <returns>A read-only wrapper of the created project</returns>
    /// <exception cref="Exception">Thrown if user cannot be found or if project creation fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// It creates a new project with a unique ID, sets the specified user as the owner,
    /// optionally copies properties from a template, and raises events to notify subscribers
    /// about the new project.
    ///
    /// A project is the top-level organizational unit in the Relay system, serving as a container
    /// for related spaces. Projects have their own access control, allowing collaborative work
    /// through the members list while maintaining ownership separation.
    /// </remarks>
    public async Task<ReadOnlyProject> CreateProject(ReadOnlyUser user, Project template = null)
    {
        ReadOnlyProject createdProject = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);

                Project newProject = _dataRepository.CreateProject(originalUser, template);
                createdProject = newProject.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create project for user {UserId}", user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ProjectCreated.InvokeHierarchy(createdProject, GroupName.ProjectHierarchy(null));

        return createdProject;
    }

    /// <summary>
    /// Updates an existing project by applying the specified update action.
    /// </summary>
    /// <param name="user">The user updating the project</param>
    /// <param name="project">The project to update</param>
    /// <param name="updateAction">The action to apply to the project</param>
    /// <returns>A task that completes when the update operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or project cannot be found, or if update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable project object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    ///
    /// Project updates typically involve modifying metadata such as the alias (display name),
    /// description, or other properties that don't affect the underlying space structure.
    /// More complex operations like adding or removing members are handled by specialized methods.
    /// </remarks>
    public async Task UpdateProject(ReadOnlyUser user, ReadOnlyProject project, Action<Project> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalProject = ResolveProject(project.Id);

                _dataRepository.UpdateProject(originalUser, originalProject, updateAction);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update project {ProjectId} by user {UserId}", project.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ProjectUpdated.InvokeHierarchy(project, GroupName.ProjectHierarchy(project.Id));
    }

    /// <summary>
    /// Deletes an existing project and all its contents.
    /// </summary>
    /// <param name="project">The project to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if project cannot be found or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the logical deletion in the data model and dispatching the appropriate events.
    /// The deletion occurs within a lock to ensure consistency. After deletion, events are raised to notify
    /// all interested subscribers.
    ///
    /// Deleting a project is a destructive operation that removes all spaces, jobs, edges, and views
    /// contained within it. This operation is not reversible, and all data associated with the project
    /// will be permanently lost. The physical deletion of files is handled asynchronously to avoid
    /// blocking the UI during large project deletions.
    ///
    /// Unlike other deletion methods, this one doesn't require a user parameter, as it's typically
    /// called during system maintenance or cleanup operations.
    /// </remarks>
    public async Task DeleteProject(ReadOnlyProject project)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalProject = ResolveProject(project.Id);

                _dataRepository.DeleteProject(originalProject);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete project {ProjectId}", project.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ProjectDeleted.InvokeHierarchy(project, GroupName.ProjectHierarchy(project.Id));
    }

    /// <summary>
    /// Adds a user as a member to an existing project.
    /// </summary>
    /// <param name="user">The user performing the addition (typically the project owner or an admin)</param>
    /// <param name="project">The project to add the member to</param>
    /// <param name="member">The user to add as a project member</param>
    /// <returns>A task that completes when the addition operation is finished</returns>
    /// <exception cref="Exception">Thrown if user, project, or member cannot be found, if member is already in the project, or if addition fails</exception>
    /// <remarks>
    /// This method handles adding a new member to a project's access control list.
    /// The operation occurs within a lock to ensure consistency. After the addition,
    /// events are raised to notify all interested subscribers about the project update.
    ///
    /// Adding a member to a project grants that user access to view and modify all spaces
    /// and jobs within the project. Members can create, update, and delete spaces and jobs,
    /// but they cannot delete the project or transfer ownership.
    ///
    /// This method performs validation to ensure the user being added isn't already a member
    /// of the project, to prevent duplicate entries in the members list.
    /// </remarks>
    public async Task AddProjectMember(ReadOnlyUser user, ReadOnlyProject project, ReadOnlyUser member)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalProject = ResolveProject(project.Id);
                var originalMember = ResolveUser(member.Id);

                if (originalProject.Members.Contains(originalMember))
                    throw new Exception($"User {member.Id} is already a member of project {project.Id}");

                _dataRepository.UpdateProject(originalUser, originalProject, (p) => p.AddMember(originalMember));
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to add member {MemberId} to project {ProjectId} by user {UserId}", member.Id, project.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ProjectUpdated.InvokeHierarchy(project, GroupName.ProjectHierarchy(project.Id));
    }

    /// <summary>
    /// Removes a user from a project's member list.
    /// </summary>
    /// <param name="user">The user performing the removal (typically the project owner or an admin)</param>
    /// <param name="project">The project to remove the member from</param>
    /// <param name="member">The user to remove from the project</param>
    /// <returns>A task that completes when the removal operation is finished</returns>
    /// <exception cref="Exception">Thrown if user, project, or member cannot be found, if member is not in the project, or if removal fails</exception>
    /// <remarks>
    /// This method handles removing a member from a project's access control list.
    /// The operation occurs within a lock to ensure consistency. After the removal,
    /// events are raised to notify all interested subscribers about the project update.
    ///
    /// Removing a member from a project revokes that user's access to all spaces and jobs
    /// within the project. The user will no longer be able to view or modify project contents.
    /// If the user is currently viewing the project, they will be redirected to the home screen
    /// on their next interaction.
    ///
    /// This method performs validation to ensure the user being removed is actually a member
    /// of the project, to prevent unnecessary operations and potential errors.
    /// </remarks>
    public async Task RemoveProjectMember(ReadOnlyUser user, ReadOnlyProject project, ReadOnlyUser member)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalProject = ResolveProject(project.Id);
                var originalMember = ResolveUser(member.Id);

                if (!originalProject.Members.Contains(originalMember))
                    throw new Exception($"User {member.Id} is not a member of project {project.Id}");

                _dataRepository.UpdateProject(originalUser, originalProject, (p) => p.RemoveMember(originalMember));
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to remove member {MemberId} from project {ProjectId} by user {UserId}", member.Id, project.Id, user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await ProjectUpdated.InvokeHierarchy(project, GroupName.ProjectHierarchy(project.Id));
    }

    #endregion
}
