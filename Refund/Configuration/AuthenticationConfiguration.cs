using Microsoft.Extensions.Configuration;
using Refund.Configuration.Constants;

namespace Refund.Configuration;

/// <summary>
/// Configuration class for general authentication settings.
/// This class defines high-level authentication configuration options that control
/// how users authenticate with the application.
/// </summary>
/// <remarks>
/// This configuration class is injected into components and controllers that need to 
/// determine the authentication method, such as:
/// - Login.razor for implementing the appropriate authentication flow
/// - LeftBar.razor for handling logout based on authentication type
/// - AuthenticationController for processing authentication requests
/// 
/// The configuration is loaded during application startup and registered as a singleton
/// in the dependency injection container.
/// </remarks>
public class AuthenticationConfiguration
{
    /// <summary>
    /// Gets the type of authentication mechanism used by the application.
    /// Valid values are defined in AuthenticationConfigurationConstants:
    /// - "sso" for Single Sign-On with an OAuth provider
    /// - "native" for username/password authentication
    /// </summary>
    /// <remarks>
    /// This property is critical for conditional authentication logic throughout the application.
    /// It is used to determine:
    /// 1. Which login flow to present to users in Login.razor
    /// 2. How to handle logout in LeftBar.razor.cs (redirecting to the appropriate endpoint)
    /// 3. What authentication mechanism to use in AuthenticationController 
    ///    (e.g., for the "/process-logout" endpoint)
    /// 
    /// Example usage in LeftBar.razor.cs:
    /// ```csharp
    /// if (AuthenticationConfiguration.AuthenticationType == AuthenticationConfigurationConstants.AuthenticationTypeNative)
    /// {
    ///     Navigation.NavigateTo("/process-logout", forceLoad: true);
    /// }
    /// ```
    /// </remarks>
    public string AuthenticationType { get; init; }
}

/// <summary>
/// Extension methods for loading authentication configuration from application settings
/// </summary>
public static class AuthenticationConfigurationLoader
{
    /// <summary>
    /// Loads authentication configuration settings from the specified configuration section
    /// </summary>
    /// <param name="configuration">The configuration section containing authentication settings</param>
    /// <returns>A populated AuthenticationConfiguration object with settings from the configuration</returns>
    /// <remarks>
    /// This method is typically called during application startup to determine the authentication
    /// mechanism that should be used for user login.
    /// 
    /// It is used in two primary places:
    /// 1. Directly in Program.cs during application startup:
    ///    ```csharp
    ///    var authConfig = builder.Configuration.GetSection(CommonConstants.Authentication)
    ///                           .LoadAuthenticationConfiguration();
    ///    ```
    /// 
    /// 2. In ConfigurationExtension.RegisterConfigurations() to register the configuration
    ///    with the Autofac dependency injection container:
    ///    ```csharp
    ///    containerBuilder.Register(context => context.Resolve<IConfiguration>()
    ///                                               .GetSection(CommonConstants.Authentication)
    ///                                               .LoadAuthenticationConfiguration())
    ///                   .SingleInstance();
    ///    ```
    /// 
    /// This centralized loading approach ensures consistent authentication configuration
    /// throughout the application.
    /// </remarks>
    public static AuthenticationConfiguration LoadAuthenticationConfiguration(this IConfiguration configuration)
        => new()
        {
            AuthenticationType = configuration.GetSection(AuthenticationConfigurationConstants.AuthenticationType)
                                              .Get<string>(),
        };
}