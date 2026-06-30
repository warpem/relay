namespace Refund.Configuration;

/// <summary>
/// Main configuration class for the Relay application.
/// This class defines core application settings, particularly file paths for persistence storage
/// of various application entities. These paths determine where application data is stored on disk.
/// </summary>
/// <remarks>
/// This configuration class is created during application startup in Program.cs and registered
/// as a singleton service:
/// 
/// <code>
/// var relayOptions = new RelayConfiguration();
/// builder.Configuration.GetSection(RelayConfiguration.Relay).Bind(relayOptions);
/// builder.Services.AddSingleton(relayOptions);
/// </code>
/// 
/// The configuration values are loaded from the "Relay" section of appsettings.json and bound
/// to this class. The configured file paths are then used by various repositories and services
/// for persisting application data.
/// </remarks>
public class RelayConfiguration
{
    /// <summary>
    /// The configuration section name for Relay-specific settings.
    /// </summary>
    /// <remarks>
    /// Used in Program.cs to locate the configuration section:
    /// <code>
    /// builder.Configuration.GetSection(RelayConfiguration.Relay).Bind(relayOptions);
    /// </code>
    /// </remarks>
    public const string Relay = "Relay";

    /// <summary>
    /// Gets or sets the file path where project data is stored.
    /// This file contains serialized Project objects and their relationships.
    /// </summary>
    /// <remarks>
    /// Used by the DataRepository in DataManager to determine where to save project data:
    /// 
    /// <code>
    /// _dataRepository = new DataRepository(config.ProjectsPath);
    /// _dataRepository.LoadAll(_userRepository.Users);
    /// </code>
    /// 
    /// This file stores all project definitions, including their spaces, views, and jobs,
    /// representing the core data model of the application.
    /// </remarks>
    public string ProjectsPath { get; set; } = "projects.relay";
    
    /// <summary>
    /// Gets or sets the file path where user data is stored.
    /// This file contains serialized User objects including their permissions and preferences.
    /// </summary>
    /// <remarks>
    /// Used by the UserRepository in DataManager to load and persist user information:
    /// 
    /// <code>
    /// _userRepository = new UserRepository(config.UsersPath);
    /// _userRepository.LoadUsers();
    /// </code>
    /// 
    /// The users file is loaded first during application startup, as other entities
    /// reference users through their ownership and permissions models.
    /// </remarks>
    public string UsersPath { get; set; } = "users.relay";
    
    /// <summary>
    /// Gets or sets the file path where job queue data is stored.
    /// This file contains information about job queues and their configuration.
    /// </summary>
    /// <remarks>
    /// Used by the QueueRepository in DataManager to persist queue definitions and states:
    /// 
    /// <code>
    /// _queueRepository = new QueueRepository(config.QueuesPath, (job, action) => {
    ///     UpdateJob(job.UpdatedBy.AsReadOnly(), job.AsReadOnly(), action).Wait();
    /// });
    /// </code>
    /// 
    /// The queues file stores information about all job processing queues (local, cluster)
    /// including their configuration settings and current state.
    /// </remarks>
    public string QueuesPath { get; set; } = "queues.relay";
    
    /// <summary>
    /// Gets or sets the file path where authentication tokens are stored.
    /// This file contains security tokens for authenticated users.
    /// </summary>
    /// <remarks>
    /// Used by the SecurityTokenService to persist authentication tokens:
    /// 
    /// <code>
    /// public SecurityTokenService(RelayConfiguration config)
    /// {
    ///     _tokensPath = config.TokensPath;
    ///     // Load tokens and start cleanup timer...
    /// }
    /// </code>
    /// 
    /// These tokens are used for validating authenticated user sessions and API requests.
    /// The SecurityTokenService automatically cleans up expired tokens from this file.
    /// </remarks>
    public string TokensPath { get; set; } = "tokens.relay";

    /// <summary>Path to the file storing personal access tokens (MCP/agent auth).</summary>
    public string PatsPath { get; set; } = "pats.relay";
}