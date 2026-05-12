using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new user in the system.
    /// </summary>
    /// <param name="template">Optional template user to copy properties from</param>
    /// <returns>A read-only wrapper of the created user</returns>
    /// <exception cref="Exception">Thrown if user creation fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// It creates a new user with a unique ID, optionally copying properties from a template,
    /// and raises events to notify subscribers about the new user.
    ///
    /// Unlike other creation methods, this one doesn't require an existing user to perform
    /// the operation, as it's typically used during system initialization or by administrators.
    /// </remarks>
    public async Task<ReadOnlyUser> CreateUser(User template = null)
    {
        ReadOnlyUser createdUser = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                User newUser = _userRepository.CreateUser(template);
                createdUser = newUser.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create user from template");
                throw;
            }
        });

        // Raise events outside of lock
        await UserCreated.InvokeHierarchy(createdUser, GroupName.UserHierarchy(null));

        return createdUser;
    }

    /// <summary>
    /// Updates an existing user by applying the specified update action.
    /// </summary>
    /// <param name="user">The user to update</param>
    /// <param name="updateAction">The action to apply to the user</param>
    /// <returns>A task that completes when the update operation is finished</returns>
    /// <exception cref="Exception">Thrown if user cannot be found or if update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable user object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    ///
    /// The update action can modify any property of the user, including username, password, role, etc.
    /// The event notifications are hierarchical, with specific user notifications and global notifications.
    /// </remarks>
    public async Task UpdateUser(ReadOnlyUser user, Action<User> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);

                _userRepository.UpdateUser(originalUser, updateAction);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update user {UserId}", user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await UserUpdated.InvokeHierarchy(user, GroupName.UserHierarchy(user.Id));
    }

    /// <summary>
    /// Deletes an existing user from the system.
    /// </summary>
    /// <param name="user">The user to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if user cannot be found or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the logical deletion in the data model and dispatching the appropriate events.
    /// The deletion occurs within a lock to ensure consistency. After deletion, events are raised to notify
    /// all interested subscribers.
    ///
    /// Deleting a user will not automatically remove them from projects they are members of,
    /// nor will it reassign ownership of projects they own. These operations should be performed
    /// separately before deleting a user, if needed.
    /// </remarks>
    public async Task DeleteUser(ReadOnlyUser user)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);

                _userRepository.DeleteUser(originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete user {UserId}", user.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await UserDeleted.InvokeHierarchy(user, GroupName.UserHierarchy(user.Id));
    }

    #endregion
}
