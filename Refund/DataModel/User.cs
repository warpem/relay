using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

/// <summary>
/// Represents a user in the system with authentication and authorization information.
/// Users can own and participate in projects, and have different roles that determine their permissions.
/// </summary>
public class User : RelayBase
{
    /// <summary>
    /// Cache of read-only wrappers for users, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<User, ReadOnlyUser> ReadOnlyCache = new();
    
    /// <summary>
    /// Unique identifier for this user.
    /// </summary>
    [RelayProperty(Order = 0)]
    public int Id { get; set; } = -1;

    /// <summary>
    /// Login identifier for this user.
    /// The username is used for authentication and must be unique in the system.
    /// </summary>
    [RelayProperty(Order = 1)]
    public string Username { get; set; } = "";

    /// <summary>
    /// Hashed password for this user.
    /// The password is stored as a salted hash for security.
    /// </summary>
    [RelayProperty(Order = 2)]
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// Display name for this user.
    /// This is shown in the UI and can include the user's full name.
    /// </summary>
    [RelayProperty(Order = 3)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Email address for this user.
    /// Can be used for notifications and password recovery.
    /// </summary>
    [RelayProperty(Order = 4)]
    public string Email { get; set; } = "";

    /// <summary>
    /// Role of this user in the system.
    /// The role determines the user's permissions and access level.
    /// </summary>
    [RelayProperty(Order = 5)]
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// Returns a read-only wrapper for this user.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each user to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this user</returns>
    public ReadOnlyUser AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, user => new ReadOnlyUser(user));
    }
    
    #region Hashing

    /// <summary>
    /// Size of the salt in bytes (128 bits).
    /// The salt prevents rainbow table attacks against the password hashes.
    /// </summary>
    private const int SaltSize = 16;
    
    /// <summary>
    /// Size of the password hash key in bytes (256 bits).
    /// </summary>
    private const int KeySize = 32;
    
    /// <summary>
    /// Number of iterations for the PBKDF2 algorithm.
    /// Higher iteration count increases the work factor to protect against brute force attacks.
    /// </summary>
    private const int Iterations = 1000;

    /// <summary>
    /// Hashes a password using a secure, salted hashing algorithm (PBKDF2 with SHA-256).
    /// The salt is randomly generated and stored as part of the hash.
    /// </summary>
    /// <param name="password">The password to hash</param>
    /// <returns>A base64-encoded string containing the salt and hashed password</returns>
    public static string HashPassword(string password)
    {
        // Handle the special case of an empty password
        if(string.IsNullOrEmpty(password))
        {
            password = "";
        }

        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        using(var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
        {
            var hash = pbkdf2.GetBytes(KeySize);
            var hashBytes = new byte[SaltSize + KeySize];
            Array.Copy(salt, 0, hashBytes, 0, SaltSize);
            Array.Copy(hash, 0, hashBytes, SaltSize, KeySize);

            return Convert.ToBase64String(hashBytes);
        }
    }

    /// <summary>
    /// Verifies a password against a stored hash.
    /// The method extracts the salt from the hash, then computes the hash of the provided password
    /// and compares it with the stored hash.
    /// </summary>
    /// <param name="password">The password to verify</param>
    /// <param name="hashedPassword">The stored password hash to compare against</param>
    /// <returns>True if the password matches the hash, false otherwise</returns>
    public static bool VerifyPassword(string password, string hashedPassword)
    {
        // Handle the special case of an empty password
        if(string.IsNullOrEmpty(password))
        {
            password = "";
        }

        var hashBytes = Convert.FromBase64String(hashedPassword);
        var salt = new byte[SaltSize];
        Array.Copy(hashBytes, 0, salt, 0, SaltSize);

        using(var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
        {
            var hash = pbkdf2.GetBytes(KeySize);

            for(var i = 0; i < KeySize; i++)
            {
                if(hashBytes[i + SaltSize] != hash[i])
                {
                    return false;
                }
            }
        }

        return true;
    }
    #endregion
}

/// <summary>
/// Defines the possible roles for users in the system.
/// Each role has different permissions and access levels.
/// </summary>
public enum UserRole
{
    /// <summary>
    /// Standard user role with normal access to owned projects.
    /// Users can create and edit their own projects and participate in projects they are members of.
    /// </summary>
    User = 0,
    
    /// <summary>
    /// Administrator role with full system access.
    /// Admins can manage all projects, users, and system settings.
    /// </summary>
    Admin = 1,
    
    /// <summary>
    /// Read-only role with limited access.
    /// Viewers can see projects they have been granted access to but cannot make changes.
    /// </summary>
    Viewer = 2
}