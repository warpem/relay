namespace Refund.Configuration.Constants;

/// <summary>
/// Defines common constant string keys used for accessing configuration sections throughout the application.
/// </summary>
/// <remarks>
/// These constants represent the top-level configuration section names in appsettings.json files.
/// Using these constants instead of hardcoded strings provides several benefits:
/// 
/// 1. Consistency across the application for configuration key names
/// 2. Compile-time checking to prevent typos in configuration section names
/// 3. Single place to update if configuration structure changes
/// 4. Better IDE support with code completion and navigation
/// 
/// These constants are primarily used in two critical places:
/// 
/// 1. In Program.cs during application startup to load configuration values:
///    ```csharp
///    var authConfig = builder.Configuration.GetSection(CommonConstants.Authentication)
///                           .LoadAuthenticationConfiguration();
///    ```
///
/// 2. In ConfigurationExtension.RegisterConfigurations() to register configuration objects
///    with the dependency injection container:
///    ```csharp
///    containerBuilder.Register(context => context.Resolve<IConfiguration>()
///                                                .GetSection(CommonConstants.AuthService)
///                                                .LoadAuthServiceConfiguration())
///                    .SingleInstance();
///    ```
/// </remarks>
public class CommonConstants
{
    /// <summary>
    /// The top-level configuration section for Relay application settings.
    /// </summary>
    /// <remarks>
    /// This section typically contains application-specific settings like file paths,
    /// default values, and behavior configuration. In Program.cs, these settings are
    /// bound to a RelayConfiguration object using:
    /// 
    /// ```csharp
    /// var relayOptions = new RelayConfiguration();
    /// builder.Configuration.GetSection(RelayConfiguration.Relay).Bind(relayOptions);
    /// builder.Services.AddSingleton(relayOptions);
    /// ```
    /// 
    /// The "Relay" section in appsettings.json typically includes settings like:
    /// ```json
    /// {
    ///   "Relay": {
    ///     "ProjectsPath": "projects.relay"
    ///   }
    /// }
    /// ```
    /// </remarks>
    public const string Relay = "Relay";
    
    /// <summary>
    /// The configuration section for caching settings.
    /// </summary>
    /// <remarks>
    /// This section configures the application's caching behavior, including 
    /// expiration times and whether caching is enabled.
    /// 
    /// The "Cache" section in appsettings.json typically includes settings like:
    /// ```json
    /// {
    ///   "Cache": {
    ///     "SlidingExpirationInSeconds": 900,
    ///     "IsEnabled": true
    ///   }
    /// }
    /// ```
    /// </remarks>
    public const string Cache = "Cache";
    
    /// <summary>
    /// The configuration section for OAuth/OpenID Connect service settings.
    /// </summary>
    /// <remarks>
    /// This section contains the OAuth/OpenID Connect settings used for Single Sign-On (SSO)
    /// authentication, including the authority URL, client ID, and requested scopes.
    /// 
    /// It's used in ConfigurationExtension.RegisterConfigurations() to load the 
    /// AuthServiceConfiguration:
    /// 
    /// ```csharp
    /// containerBuilder.Register(context => context.Resolve<IConfiguration>()
    ///                                             .GetSection(CommonConstants.AuthService)
    ///                                             .LoadAuthServiceConfiguration())
    ///                 .SingleInstance();
    /// ```
    /// 
    /// The AuthService configuration is consumed by AuthenticationService to construct
    /// URLs for authentication endpoints during OAuth flows.
    /// </remarks>
    public const string AuthService = "AuthService";
    
    /// <summary>
    /// The configuration section for general authentication settings.
    /// </summary>
    /// <remarks>
    /// This section contains high-level authentication configuration, most importantly
    /// the AuthenticationType setting which determines whether to use native (username/password)
    /// or SSO (OAuth) authentication.
    /// 
    /// It's used in both Program.cs to load the authentication configuration during startup:
    /// 
    /// ```csharp
    /// var authConfig = builder.Configuration.GetSection(CommonConstants.Authentication)
    ///                        .LoadAuthenticationConfiguration();
    /// ```
    /// 
    /// And in ConfigurationExtension.RegisterConfigurations() to register the
    /// AuthenticationConfiguration with the dependency injection container:
    /// 
    /// ```csharp
    /// containerBuilder.Register(context => context.Resolve<IConfiguration>()
    ///                                             .GetSection(CommonConstants.Authentication)
    ///                                             .LoadAuthenticationConfiguration())
    ///                 .SingleInstance();
    /// ```
    /// 
    /// The "Authentication" section in appsettings.json typically includes settings like:
    /// ```json
    /// {
    ///   "Authentication": {
    ///     "AuthenticationType": "native" // or "sso"
    ///   }
    /// }
    /// ```
    /// </remarks>
    public const string Authentication = "Authentication";
}