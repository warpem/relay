namespace Refund.Configuration.Constants;

/// <summary>
/// Defines constant string keys used for accessing OAuth/OpenID Connect configuration settings
/// in the application configuration. These keys match the corresponding properties in the
/// AuthServiceConfiguration class.
/// </summary>
/// <remarks>
/// These constants are primarily used in the AuthServiceConfigurationLoader.LoadAuthServiceConfiguration() 
/// extension method to retrieve values from IConfiguration sections:
/// 
/// <code>
/// Authority = configuration
///     .GetSection(AuthServiceConfigurationConstants.Authority)
///     .Get&lt;string&gt;() ?? string.Empty,
/// </code>
/// 
/// Using these constants instead of string literals provides type safety, prevents typos,
/// and centralizes the definition of configuration keys for the authentication service.
/// </remarks>
public class AuthServiceConfigurationConstants
{
    /// <summary>
    /// The configuration key for the OAuth authority URL (identity provider).
    /// </summary>
    /// <remarks>
    /// Used to retrieve the base URL of the identity provider from application settings.
    /// This value is mapped to the AuthServiceConfiguration.Authority property.
    /// 
    /// Examples of authority values in configuration:
    /// - "https://keycloak.example.com/auth/realms/your-realm" (Keycloak)
    /// - "https://login.microsoftonline.com/tenant-id" (Azure AD)
    /// </remarks>
    public const string Authority = "Authority";
    
    /// <summary>
    /// The configuration key for the OAuth client identifier.
    /// </summary>
    /// <remarks>
    /// Used to retrieve the OAuth client ID issued by the identity provider.
    /// This is a required value for any OAuth authentication flow and is used
    /// in authorization, token exchange, and logout operations.
    /// </remarks>
    public const string ClientId = "ClientId";
    
    /// <summary>
    /// The configuration key for the OAuth response type (e.g., "code", "token").
    /// </summary>
    /// <remarks>
    /// Although this constant exists for configuration completeness, the current
    /// implementation in AuthenticationService hardcodes "code" as the response type
    /// since PKCE is specifically designed for the Authorization Code flow.
    /// </remarks>
    public const string ResponseType = "ResponseType";
    
    /// <summary>
    /// The configuration key for the OAuth scopes to request during authentication.
    /// </summary>
    /// <remarks>
    /// Used to retrieve an array of scope strings that define the access permissions
    /// requested during OAuth authentication. Common values include "openid", "profile",
    /// and "email" for standard OpenID Connect flows.
    /// 
    /// In the application configuration, this should be defined as a JSON array:
    /// "Scopes": ["openid", "profile", "email"]
    /// </remarks>
    public const string Scopes = "Scopes";

    public const string AuthorizationPath = "AuthorizationPath";
    public const string TokenPath = "TokenPath";
    public const string LogoutPath = "LogoutPath";
    public const string LogoutRedirectParameter = "LogoutRedirectParameter";
}