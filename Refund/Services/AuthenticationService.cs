using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;

namespace Refund.Services;

/// <summary>
/// Provides authentication services for the application, including SSO login flows with PKCE.
/// This service handles OAuth 2.0 authentication using the Authorization Code flow with PKCE
/// extension for public clients.
/// </summary>
public class AuthenticationService
{
    private readonly AuthServiceConfiguration _ssoConfig;
    private readonly DataManager _dataManager;
    private readonly HttpClient _http;
    private readonly MemorySecureStorage _storage;

    private const string CodeVerifierKey = "code_verifier";

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationService"/> class.
    /// </summary>
    /// <param name="ssoConfig">Configuration for the SSO service</param>
    /// <param name="dataManager">Data manager used to find or create users</param>
    /// <param name="http">HTTP client for making requests to the identity provider</param>
    /// <param name="storage">Secure storage for holding the code verifier during the PKCE flow</param>
    public AuthenticationService(AuthServiceConfiguration ssoConfig,
                                 DataManager dataManager,
                                 HttpClient http,
                                 MemorySecureStorage storage)
    {
        _ssoConfig = ssoConfig;
        _dataManager = dataManager;
        _http = http;
        _storage = storage;
    }

    #region SSO Public Methods

    /// <summary>
    /// Build the PKCE code challenge, store the code verifier in secure storage, 
    /// and return the full authorization URL to redirect to.
    /// </summary>
    public string BuildAuthorizationUrlAndPkce(string baseUri)
    {
        // Generate PKCE code verifier & challenge
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);

        // Store the codeVerifier in secure storage (e.g. in-memory or distributed)
        _storage.Set(CodeVerifierKey, codeVerifier);

        // Build the SSO URL
        var scopes = string.Join(" ", _ssoConfig.Scopes);
        var authUrl = $"{_ssoConfig.AuthorizationEndpoint}" +
                      $"?client_id={_ssoConfig.ClientId}" +
                      $"&redirect_uri={baseUri}sso-callback" +
                      $"&response_type=code" +
                      $"&scope={Uri.EscapeDataString(scopes)}" +
                      $"&code_challenge={codeChallenge}" +
                      $"&code_challenge_method=S256";

        return authUrl;
    }

    /// <summary>
    /// Given the "code" from the IdP callback, exchange it for tokens,
    /// parse the user claims, find or create the user in the DB, and return it.
    /// </summary>
    public async Task<ReadOnlyUser> ExchangeCodeForUser(string code, string baseUri)
    {
        // Retrieve the code verifier we stored
        var codeVerifier = _storage.Get(CodeVerifierKey);
        if (string.IsNullOrEmpty(codeVerifier))
            throw new Exception("No code verifier found in secure storage");

        _storage.Remove(CodeVerifierKey);

        // Exchange code for tokens
        var tokens = await ExchangeCodeForTokensAsync(code, codeVerifier, baseUri);

        // Extract username and display name (prefer ID token which has profile claims)
        var tokenToParse = !string.IsNullOrEmpty(tokens.IdToken) ? tokens.IdToken : tokens.AccessToken;
        var (username, fullName, email) = ParseUserDataFromToken(tokenToParse);

        // Find or create user
        var user = _dataManager.FindUser(username);
        if (user == null)
        {
            user = await _dataManager.CreateUser(new User
            {
                Username = username,
                Name = fullName,
                Email = email,
                Role = _dataManager.Users.Any() ? UserRole.User : UserRole.Admin
            });
        }

        // Optionally store tokens for further use
        // _storage.Set(username, JsonSerializer.Serialize(tokens));

        return user;
    }

    #endregion

    #region Internal SSO Helpers

    /// <summary>
    /// Exchanges the authorization code for access and refresh tokens.
    /// </summary>
    /// <param name="code">The authorization code returned from the identity provider</param>
    /// <param name="codeVerifier">The PKCE code verifier that was used to generate the code challenge</param>
    /// <param name="baseUri">The base URI of the application, used to build the callback URL</param>
    /// <returns>A TokenData object containing the access token, refresh token, and expiration time</returns>
    /// <exception cref="HttpRequestException">Thrown when the token endpoint returns an error response</exception>
    /// <remarks>
    /// This method implements the token exchange part of the OAuth 2.0 Authorization Code Flow with PKCE.
    /// It sends the authorization code and code verifier to the token endpoint to obtain access and refresh tokens.
    /// </remarks>
    private async Task<TokenData> ExchangeCodeForTokensAsync(string code, string codeVerifier, string baseUri)
    {
        var tokenEndpoint = _ssoConfig.TokenEndpoint;
        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _ssoConfig.ClientId,
            ["code_verifier"] = codeVerifier,
            ["code"] = code,
            ["redirect_uri"] = $"{baseUri}sso-callback",
            ["scope"] = string.Join(" ", _ssoConfig.Scopes)
        };

        var response = await _http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(parameters));
        response.EnsureSuccessStatusCode(); // throws on 400+
        
        var content = await response.Content.ReadAsStringAsync();
        
        return ParseTokenResponse(content);
    }

    /// <summary>
    /// Parses the token response JSON from the identity provider.
    /// </summary>
    /// <param name="json">The JSON response string from the token endpoint</param>
    /// <returns>A TokenData object containing access token, refresh token, and expiration</returns>
    private TokenData ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new TokenData
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty,
            IdToken = root.TryGetProperty("id_token", out var idToken) ? idToken.GetString() ?? string.Empty : string.Empty,
            RefreshToken = root.GetProperty("refresh_token").GetString() ?? string.Empty,
            Expiration = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32())
        };
    }

    /// <summary>
    /// Extracts the username and full name from the JWT access token.
    /// </summary>
    /// <param name="accessToken">The JWT access token</param>
    /// <returns>A tuple containing the username and optional full name</returns>
    /// <exception cref="Exception">Thrown when no username claim is found in the token</exception>
    /// <remarks>
    /// This method handles different claim types that might contain the username:
    /// - preferred_username: Standard OpenID Connect claim 
    /// - sub: Subject identifier, fallback when preferred_username is not available
    /// 
    /// For the full name, it checks:
    /// - name: Standard OpenID Connect claim for the full name
    /// - given_name: Fallback when the full name is not available
    /// </remarks>
    private (string Username, string? FullName, string? Email) ParseUserDataFromToken(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(accessToken);

        var username = token.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                    ?? token.Claims.FirstOrDefault(c => c.Type == "cognito:username")?.Value
                    ?? token.Claims.FirstOrDefault(c => c.Type == "username")?.Value
                    ?? token.Claims.FirstOrDefault(c => c.Type == "sub")?.Value
                    ?? throw new Exception("No username claim found in token.");

        var fullName = token.Claims.FirstOrDefault(c => c.Type == "name")?.Value
                    ?? token.Claims.FirstOrDefault(c => c.Type == "given_name")?.Value;

        var email = token.Claims.FirstOrDefault(c => c.Type == "email")?.Value;

        return (username, fullName, email);
    }

    #endregion

    #region PKCE Utilities

    /// <summary>
    /// Generates a cryptographically secure random string for use as a PKCE code verifier.
    /// </summary>
    /// <returns>A URL-safe base64 encoded random string of 32 bytes (43 characters)</returns>
    /// <remarks>
    /// The code verifier must satisfy these requirements from RFC 7636:
    /// - Must be between 43-128 characters
    /// - Can only contain alphanumeric characters, hyphens, periods, underscores, and tildes
    /// 
    /// This implementation generates a 32-byte random value and base64url-encodes it,
    /// which produces a 43-character string, satisfying the minimum length requirement.
    /// </remarks>
    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Generates a PKCE code challenge from the given code verifier using the S256 method.
    /// </summary>
    /// <param name="codeVerifier">The code verifier to hash</param>
    /// <returns>A URL-safe base64 encoded SHA-256 hash of the code verifier</returns>
    /// <remarks>
    /// The S256 method (SHA-256) is defined in RFC 7636 as:
    /// 1. Take the SHA-256 hash of the code verifier
    /// 2. Base64url-encode the hash
    /// 
    /// This provides better security than the plain method, as the code challenge
    /// cannot be reversed to obtain the code verifier.
    /// </remarks>
    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(challengeBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    #endregion
}

/// <summary>
/// Represents authentication tokens obtained from the identity provider.
/// </summary>
/// <remarks>
/// This class stores the OAuth 2.0 tokens obtained from the token endpoint.
/// The access token is used for authenticating API requests, while the refresh token
/// can be used to obtain a new access token without requiring the user to log in again.
/// </remarks>
public class TokenData
{
    /// <summary>
    /// Gets or sets the access token used to authenticate API requests.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// Gets or sets the ID token containing user profile claims.
    /// </summary>
    public string IdToken { get; set; } = "";

    /// <summary>
    /// Gets or sets the refresh token used to obtain a new access token when the current one expires.
    /// </summary>
    public string RefreshToken { get; set; } = "";
    
    /// <summary>
    /// Gets or sets the expiration time of the access token.
    /// </summary>
    public DateTime Expiration { get; set; }
}
