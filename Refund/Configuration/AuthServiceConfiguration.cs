using Microsoft.Extensions.Configuration;
using Refund.Configuration.Constants;

namespace Refund.Configuration;

/// <summary>
/// Configuration class for OAuth/OpenID Connect authentication service settings.
/// 
/// This class holds the necessary configuration parameters for connecting to an OAuth/OpenID
/// Connect provider for single sign-on (SSO) authentication. It's primarily used by the
/// AuthenticationService for implementing the OAuth 2.0 Authorization Code flow with PKCE
/// (Proof Key for Code Exchange) extension for secure client authentication.
/// </summary>
/// <remarks>
/// In actual usage, this configuration is registered as a singleton during application startup
/// via the ConfigurationExtension.RegisterConfigurations method, which loads values from the
/// "AuthService" section of appsettings.json.
/// 
/// The AuthenticationController and AuthenticationService both depend on this configuration
/// to construct URLs for authentication endpoints:
/// 
/// 1. In AuthenticationService.BuildAuthorizationUrlAndPkce:
///    - Constructs the authorization endpoint URL using Authority and ClientId
///    - Combines with PKCE challenge for secure authorization code flow
///    - Uses Scopes to request specific permissions from the identity provider
/// 
/// 2. In AuthenticationController.ProcessLogout:
///    - Constructs the logout endpoint URL using Authority and ClientId
///    - Used only when the authentication type is set to "sso"
/// 
/// 3. In token exchange operations (AuthenticationService.ExchangeCodeForTokensAsync):
///    - Constructs the token endpoint URL using Authority
///    - Uses ClientId for authentication with the identity provider
///    - Includes Scopes to maintain consistent permission requests
/// 
/// The implementation supports standard OpenID Connect endpoints for Keycloak, Azure AD,
/// and other compliant providers.
/// </remarks>
public class AuthServiceConfiguration
{
    /// <summary>
    /// Gets the base URL of the identity provider that will authenticate users.
    /// </summary>
    /// <remarks>
    /// In actual usage, this property is used to construct specific endpoint URLs by 
    /// appending "/protocol/openid-connect/auth" for authorization, 
    /// "/protocol/openid-connect/token" for token exchange, and
    /// "/protocol/openid-connect/logout" for logout operations.
    /// 
    /// Examples of authority values:
    /// - Keycloak: "https://keycloak.example.com/auth/realms/your-realm"
    /// - Azure AD: "https://login.microsoftonline.com/tenant-id"
    /// - Google: "https://accounts.google.com"
    /// </remarks>
    public string Authority { get; init; } = null!;
    
    /// <summary>
    /// Gets the OAuth client identifier issued by the identity provider.
    /// </summary>
    /// <remarks>
    /// This identifier is included in all OAuth requests including authorization,
    /// token exchange, and logout. It identifies this application to the identity provider.
    /// 
    /// In the current implementation, this ClientId is used for the OAuth 2.0 Authorization
    /// Code flow with PKCE, which is secure for public clients without a client secret.
    /// </remarks>
    public string ClientId { get; init; } = null!;
    
    /// <summary>
    /// Gets the OAuth response type to request from the identity provider.
    /// </summary>
    /// <remarks>
    /// Current implementation primarily uses "code" for the Authorization Code flow.
    /// Other common values include "token" for implicit flow, but this is less secure 
    /// and not used in current authentication flows.
    /// 
    /// Note: While this property exists in the configuration, the actual implementation
    /// in AuthenticationService.BuildAuthorizationUrlAndPkce hardcodes "code" as the
    /// response type since PKCE is specifically designed for the Authorization Code flow.
    /// </remarks>
    public string ResponseType { get; init; } = null!;
    
    /// <summary>
    /// Gets the OAuth scope permissions to request from the identity provider.
    /// </summary>
    /// <remarks>
    /// Scopes define the access privileges requested during authentication.
    /// They are included in both the authorization request and token requests.
    /// 
    /// Common OpenID Connect scopes used in the application include:
    /// - "openid": Basic OpenID Connect identity verification
    /// - "profile": Access to user profile information (name, picture, etc.)
    /// - "email": Access to user's email address
    /// 
    /// These scopes are combined into a space-separated string in authorization URLs
    /// and included in token exchange requests to ensure consistent permission levels.
    /// </remarks>
    public string[] Scopes { get; init; } = null!;

    public string AuthorizationPath { get; init; } = "/protocol/openid-connect/auth";
    public string TokenPath { get; init; } = "/protocol/openid-connect/token";
    public string LogoutPath { get; init; } = "/protocol/openid-connect/logout";
    public string LogoutRedirectParameter { get; init; } = "post_logout_redirect_uri";

    public string AuthorizationEndpoint => $"{Authority}{AuthorizationPath}";
    public string TokenEndpoint => $"{Authority}{TokenPath}";
    public string LogoutEndpoint => $"{Authority}{LogoutPath}";
}

/// <summary>
/// Extension methods for loading OAuth service configuration from application settings.
/// </summary>
public static class AuthServiceConfigurationLoader
{
    /// <summary>
    /// Loads OAuth service configuration settings from the specified configuration section.
    /// </summary>
    /// <param name="configuration">The configuration section containing OAuth settings</param>
    /// <returns>A populated AuthServiceConfiguration object with settings from the configuration</returns>
    /// <remarks>
    /// This method is called during application startup via ConfigurationExtension.RegisterConfigurations
    /// to load values from the "AuthService" section of the application configuration.
    /// 
    /// It ensures null safety by providing default empty values when configuration values are missing,
    /// which allows the application to start even with incomplete OAuth configuration
    /// (though authentication will fail without proper values).
    /// 
    /// Typical usage:
    /// ```csharp
    /// containerBuilder.Register(context => context.Resolve<IConfiguration>()
    ///                                             .GetSection(CommonConstants.AuthService)
    ///                                             .LoadAuthServiceConfiguration())
    ///                 .SingleInstance();
    /// ```
    /// </remarks>
    public static AuthServiceConfiguration LoadAuthServiceConfiguration(this IConfiguration configuration) => new()
    {
        Authority = configuration
                        .GetSection(AuthServiceConfigurationConstants.Authority)
                        .Get<string>() ??
                    string.Empty,
        ClientId = configuration
                       .GetSection(AuthServiceConfigurationConstants.ClientId)
                       .Get<string>() ??
                   string.Empty,
        ResponseType = configuration
                           .GetSection(AuthServiceConfigurationConstants.ResponseType)
                           .Get<string>() ??
                       string.Empty,
        Scopes = configuration
                     .GetSection(AuthServiceConfigurationConstants.Scopes)
                     .Get<string[]>() ??
                 [],
        AuthorizationPath = configuration
                                .GetSection(AuthServiceConfigurationConstants.AuthorizationPath)
                                .Get<string>() ??
                            "/protocol/openid-connect/auth",
        TokenPath = configuration
                        .GetSection(AuthServiceConfigurationConstants.TokenPath)
                        .Get<string>() ??
                    "/protocol/openid-connect/token",
        LogoutPath = configuration
                         .GetSection(AuthServiceConfigurationConstants.LogoutPath)
                         .Get<string>() ??
                     "/protocol/openid-connect/logout",
        LogoutRedirectParameter = configuration
                                      .GetSection(AuthServiceConfigurationConstants.LogoutRedirectParameter)
                                      .Get<string>() ??
                                  "post_logout_redirect_uri"
    };
}