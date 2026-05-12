namespace Refund.Configuration.Constants;

/// <summary>
/// Defines constant string keys and values used for authentication configuration in the application.
/// These constants define both the configuration keys for authentication settings and the 
/// possible values for authentication types.
/// </summary>
/// <remarks>
/// These constants are used in two primary ways:
/// 
/// 1. The configuration keys (like AuthenticationType) are used in AuthenticationConfigurationLoader
///    to load settings from the application configuration:
///    
///    <code>
///    AuthenticationType = configuration.GetSection(AuthenticationConfigurationConstants.AuthenticationType)
///                                      .Get&lt;string&gt;()
///    </code>
///    
/// 2. The authentication type values (AuthenticationTypeSSO, AuthenticationTypeNative) are used
///    in application logic to determine authentication behavior, such as in LeftBar.Logout():
///    
///    <code>
///    if (AuthenticationConfiguration.AuthenticationType == AuthenticationConfigurationConstants.AuthenticationTypeNative)
///    {
///        Navigation.NavigateTo("/process-logout", forceLoad: true);
///    }
///    </code>
/// </remarks>
public class AuthenticationConfigurationConstants
{
    /// <summary>
    /// Configuration key to indicate whether authentication is enabled.
    /// </summary>
    /// <remarks>
    /// This setting can be used to completely disable authentication for development
    /// or testing environments. When set to false in the configuration, the application
    /// may bypass login screens or use a default user identity.
    /// </remarks>
    public const string IsEnabled = "IsEnabled";
    
    /// <summary>
    /// Configuration key for the type of authentication to use (SSO or native).
    /// </summary>
    /// <remarks>
    /// This key is used in AuthenticationConfigurationLoader.LoadAuthenticationConfiguration()
    /// to retrieve the authentication type setting from appsettings.json. The returned value
    /// is expected to match one of the defined authentication type constants: 
    /// AuthenticationTypeSSO or AuthenticationTypeNative.
    /// </remarks>
    public const string AuthenticationType = "AuthenticationType";

    /// <summary>
    /// Value indicating Single Sign-On authentication should be used.
    /// </summary>
    /// <remarks>
    /// When the application is configured to use this authentication type,
    /// it will use the OAuth/OpenID Connect flow defined in AuthenticationService
    /// for user login, utilizing the settings in AuthServiceConfiguration.
    /// 
    /// This value is used in conditional logic throughout the application to
    /// determine authentication behavior, such as in LeftBar.Logout() and
    /// AuthenticationController.
    /// </remarks>
    public const string AuthenticationTypeSSO = "sso";
    
    /// <summary>
    /// Value indicating native (username/password) authentication should be used.
    /// </summary>
    /// <remarks>
    /// When the application is configured to use this authentication type,
    /// it will use the built-in username/password authentication rather than
    /// an external identity provider.
    /// 
    /// This value is used in conditional logic throughout the application to
    /// determine authentication behavior, especially in login/logout flows.
    /// </remarks>
    public const string AuthenticationTypeNative = "native";
}