using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using Refund.DataModel;
using Refund.Jobs;

namespace Refund.Services.Core.Repositories;

/// <summary>
/// Repository for user data management, providing CRUD operations for user accounts
/// and persistence to disk with auto-saving functionality.
/// </summary>
public class UserRepository : IDisposable
{
    /// <summary>
    /// The file path where users are stored.
    /// </summary>
    private readonly string _usersPath;
    
    /// <summary>
    /// JSON serialization options used for reading/writing data.
    /// </summary>
    private readonly JsonSerializerOptions _jsonOptions;
    
    /// <summary>
    /// Logger for user repository operations.
    /// </summary>
    private readonly ILogger _logger = Log.ForContext<UserRepository>();
    
    // Core data collection
    private readonly List<User> _users = new();

    // Synchronization
    private readonly object _saveLock = new();
    
    // Autosave
    private int _autoSaveInterval;
    private Timer _autoSaveTimer;
    private bool _disposed;
    private readonly HashSet<User> _pendingUpdateUsers = new();

    /// <summary>
    /// Read-only collection of all users.
    /// </summary>
    public ReadOnlyCollection<User> Users => _users.AsReadOnly();

    /// <summary>
    /// Initializes a new instance of the UserRepository class.
    /// Sets up the path for user data storage and configures JSON serialization options.
    /// </summary>
    /// <param name="usersPath">The file path where user data will be stored</param>
    public UserRepository(string usersPath)
    {
        _usersPath = usersPath;
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        _jsonOptions.MakeReadOnly();
    }
    
    /// <summary>
    /// Starts periodically saving changes to users.
    /// </summary>
    /// <param name="milliseconds">The interval, in milliseconds, at which to save changes.</param>
    public void StartAutoSave(int milliseconds)
    {
        _autoSaveInterval = milliseconds;
        _autoSaveTimer = new Timer(SaveChanges, null, _autoSaveInterval, Timeout.Infinite);
    }

    /// <summary>
    /// Stops periodically saving changes to users.
    /// </summary>
    public void StopAutoSave()
    {
        _autoSaveTimer?.Dispose();
    }

    /// <summary>
    /// Creates a new user, optionally based on a template.
    /// </summary>
    /// <param name="template">Optional template user to base the new user on.</param>
    /// <returns>The newly created user.</returns>
    public User CreateUser(User template = null)
    {
        lock (_saveLock)
        {
            var user = new User();
            if (template != null)
                user.AdoptState(template);

            user.Id = _users.Select(u => u.Id).DefaultIfEmpty(0).Max() + 1;
            _users.Add(user);
            _pendingUpdateUsers.Add(user);

            _logger.Information("Successfully created user {UserId} with name {UserName}", user.Id, user.Name);
            return user;
        }
    }

    /// <summary>
    /// Updates an existing user.
    /// </summary>
    /// <param name="user">The user to update.</param>
    /// <param name="updateAction">The action to perform on the user.</param>
    /// <exception cref="ArgumentNullException">Thrown when user is null.</exception>
    public void UpdateUser(User user, Action<User> updateAction)
    {
        if (user == null) throw new ArgumentNullException(nameof(user));

        lock (_saveLock)
        {
            updateAction(user);
            _pendingUpdateUsers.Add(user);
            _logger.Information("Successfully updated user {UserId} ({UserName})", user.Id, user.Name);
        }
    }

    /// <summary>
    /// Deletes a user from the repository.
    /// </summary>
    /// <param name="user">The user to delete.</param>
    /// <exception cref="ArgumentNullException">Thrown when user is null.</exception>
    public void DeleteUser(User user)
    {
        if (user == null) throw new ArgumentNullException(nameof(user));

        lock (_saveLock)
        {
            _users.Remove(user);
            _pendingUpdateUsers.Remove(user);
            SaveUsers();
            _logger.Information("Successfully deleted user {UserId} ({UserName})", user.Id, user.Name);
        }
    }

    /// <summary>
    /// Loads all users from persistent storage. If no users are found, creates a set of default users.
    /// Sets a default password for the first user for development environments.
    /// </summary>
    public void LoadUsers()
    {
        if (!File.Exists(_usersPath))
        {
            _logger.Information("No previous user data found at {UsersPath}", Path.GetFullPath(_usersPath));
        }
        else
        {
            var usersString = File.ReadAllText(_usersPath);
            var usersNode = JsonNode.Parse(usersString);

            if (usersNode == null)
                throw new Exception($"Couldn't parse JSON from {Path.GetFullPath(_usersPath)}");

            if (usersNode["Users"] != null)
            {
                _users.Clear();

                usersNode["Users"]
                    .Deserialize<List<JsonObject>>()
                    ?.ForEach(u =>
                    {
                        var loadedUser = new User();
                        loadedUser.ReadFromJson(u);
                        _users.Add(loadedUser);
                    });
                
                _logger.Information("Successfully loaded {UserCount} users from {UsersPath}", _users.Count, Path.GetFullPath(_usersPath));
            }
        }

    }

    /// <summary>
    /// Timer callback that saves any pending user changes to disk.
    /// Reschedules itself after completion if the repository is not disposed.
    /// </summary>
    /// <param name="state">State object passed by the Timer (not used)</param>
    private void SaveChanges(object state)
    {
        lock (_saveLock)
        {
            try
            {
                if (_pendingUpdateUsers.Count > 0)
                {
                    SaveUsers();
                    _logger.Information("Successfully saved {UpdatedUserCount} user changes to disk", _pendingUpdateUsers.Count);
                    _pendingUpdateUsers.Clear();
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error saving user changes");
            }
            finally
            {
                if (!_disposed)
                    _autoSaveTimer?.Change(_autoSaveInterval, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Persists all users to the users file.
    /// Creates the directory if it doesn't exist.
    /// Serializes each user to JSON using the WriteToJson method.
    /// </summary>
    private void SaveUsers()
    {
        var directoryPath = Path.GetDirectoryName(_usersPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var usersJson = new JsonObject();
        usersJson["Users"] = new JsonArray(_users.Select(u =>
        {
            var writer = new JsonObject();
            u.WriteToJson(writer);
            return writer;
        }).ToArray<JsonNode>());

        File.WriteAllText(_usersPath, usersJson.ToJsonString(_jsonOptions));
    }

    /// <summary>
    /// Finds a user by their ID.
    /// </summary>
    /// <param name="userId">The ID of the user to find.</param>
    /// <returns>The user with the specified ID, or null if not found.</returns>
    public User FindUser(int userId)
    {
        User user = _users.FirstOrDefault(u => u.Id == userId);
        if (user == null)
        {
            _logger.Warning("User with ID {UserId} not found", userId);
            return null;
        }

        return user;
    }

    /// <summary>
    /// Finds a user by their username.
    /// </summary>
    /// <param name="username">The username of the user to find.</param>
    /// <returns>The user with the specified username, or null if not found.</returns>
    public User FindUser(string username)
    {
        User user = _users.FirstOrDefault(u => u.Username == username);
        if (user == null)
        {
            _logger.Warning("User with username {Username} not found", username);
            return null;
        }

        return user;
    }

    /// <summary>
    /// Disposes of the resources used by the UserRepository.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _autoSaveTimer?.Dispose();
            }

            _disposed = true;
        }
    }
}