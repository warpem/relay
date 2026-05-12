using System.Text.Json.Nodes;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the User class, providing immutable access to user data.
/// Users represent system accounts with authentication and authorization information.
/// </summary>
public sealed class ReadOnlyUser
{
    /// <summary>
    /// The wrapped mutable user instance.
    /// </summary>
    private readonly User _user;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyUser"/> class.
    /// </summary>
    /// <param name="user">The mutable user to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the user parameter is null.</exception>
    internal ReadOnlyUser(User user)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <summary>
    /// Gets the unique identifier for this user.
    /// </summary>
    public int Id => _user.Id;
    
    /// <summary>
    /// Gets the username for this user.
    /// The username is used for authentication and is unique within the system.
    /// </summary>
    public string Username => _user.Username;
    
    /// <summary>
    /// Gets the display name for this user.
    /// The name is used in the UI to identify the user in a friendly way.
    /// </summary>
    public string Name => _user.Name;
    
    /// <summary>
    /// Gets the email address for this user.
    /// The email can be used for notifications and recovery.
    /// </summary>
    public string Email => _user.Email;
    
    /// <summary>
    /// Gets the role assigned to this user.
    /// The role determines the user's permissions within the system.
    /// </summary>
    public UserRole Role => _user.Role;
    
    /// <summary>
    /// Gets the hashed password for this user.
    /// This is a secure representation of the user's password, not the actual password.
    /// </summary>
    public string PasswordHash => _user.PasswordHash;
    
    /// <summary>
    /// Converts this user to a JSON representation.
    /// </summary>
    /// <returns>A JSON node containing the serialized user data.</returns>
    public JsonNode ToJson() => _user.ToJson();
}