using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Relay.Pages;
using AuthenticationService = Refund.Services.AuthenticationService;

namespace Relay.Controllers;

/// <summary>
/// Controller handling all authentication-related HTTP endpoints including login, logout,
/// single sign-on (SSO), and user registration.
/// </summary>
public class AuthenticationController : ControllerBase
{
    private readonly AuthenticationConfiguration authenticationConfiguration;
    private readonly AuthServiceConfiguration authServiceConfiguration;
    private readonly AuthenticationService authenticationService;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly DataManager dataManager;
    private readonly SecurityTokenService tokenService;
    private readonly ILogger<AuthenticationController> _logger;

    /// <summary>
    /// Gets the current HTTP context from the accessor.
    /// </summary>
    private HttpContext httpContext => httpContextAccessor.HttpContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationController"/> class.
    /// </summary>
    /// <param name="authenticationConfiguration">Configuration for authentication methods</param>
    /// <param name="authServiceConfiguration">Configuration for the SSO authentication service</param>
    /// <param name="authenticationService">Service that handles authentication logic</param>
    /// <param name="httpContextAccessor">Accessor for HTTP context</param>
    /// <param name="dataManager">Data manager for user operations</param>
    /// <param name="tokenService">Service for managing security tokens</param>
    /// <param name="logger">Logger for authentication events</param>
    public AuthenticationController(AuthenticationConfiguration authenticationConfiguration,
                                    AuthServiceConfiguration authServiceConfiguration,
                                    AuthenticationService authenticationService,
                                    IHttpContextAccessor httpContextAccessor,
                                    DataManager dataManager,
                                    SecurityTokenService tokenService,
                                    ILogger<AuthenticationController> logger)
    {
        this.authenticationConfiguration = authenticationConfiguration;
        this.authServiceConfiguration = authServiceConfiguration;
        this.authenticationService = authenticationService;
        this.httpContextAccessor = httpContextAccessor;
        this.dataManager = dataManager;
        this.tokenService = tokenService;
        _logger = logger;
    }

    /// <summary>
    /// Processes a local login request with username and password.
    /// </summary>
    /// <param name="request">The login request containing username, password, and remember me flag</param>
    /// <returns>Success response if login succeeds, unauthorized if credentials are invalid</returns>
    [HttpPost]
    [Route("/process-login")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Login([FromForm] LoginRequest request)
    {
        var user = dataManager.FindUser(request.Username);

        if (user == null || !Refund.DataModel.User.VerifyPassword(request.Password, user.PasswordHash))
            return Unauthorized();

        var claims = new List<Claim> { new Claim(ClaimTypes.Name, user.Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var props = new AuthenticationProperties
        {
            IsPersistent = request.RememberMe,
            ExpiresUtc = DateTime.UtcNow.AddDays(30),
        };

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Initiates the SSO authentication flow by redirecting to the identity provider's authorization endpoint.
    /// Uses PKCE (Proof Key for Code Exchange) to ensure secure authorization code flow.
    /// </summary>
    /// <returns>Redirect to the SSO provider's authorization endpoint</returns>
    [HttpGet]
    [Route("/start-sso")]
    public IActionResult StartSso()
    {
        var baseUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}/";

        var ssoUrl = authenticationService.BuildAuthorizationUrlAndPkce(baseUri);

        return Redirect(ssoUrl);
    }

    /// <summary>
    /// Handles the callback from the SSO provider after user authentication.
    /// Exchanges the authorization code for an access token and user information.
    /// </summary>
    /// <param name="code">The authorization code returned by the SSO provider</param>
    /// <returns>Redirect to the home page if successful, or to the login page with error if failed</returns>
    [HttpGet]
    [Route("/sso-callback")]
    public async Task<IActionResult> SsoCallback(string code)
    {
        try
        {
            var baseUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}/";

            var user = await authenticationService.ExchangeCodeForUser(code, baseUri);

            var claims = new List<Claim> { new Claim(ClaimTypes.Name, user.Username) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            var props = new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTime.UtcNow.AddDays(30)
            };
            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);

            return Redirect("/");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSO callback failed");
            return Redirect("/login?error=1");
        }
    }

    /// <summary>
    /// Processes a logout request, signing the user out of the application.
    /// For SSO authentication, also redirects to the identity provider's logout endpoint.
    /// </summary>
    /// <param name="code">Optional authorization code (not used in current implementation)</param>
    /// <returns>Redirect to the SSO provider's logout endpoint or login page</returns>
    [HttpGet]
    [Route("/process-logout")]
    public async Task<IActionResult> ProcessLogout(string code)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (authenticationConfiguration.AuthenticationType == "sso")
        {
            var baseUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}/";

            var logoutUrl = $"{authServiceConfiguration.LogoutEndpoint}" +
                            $"?client_id={authServiceConfiguration.ClientId}" +
                            $"&{authServiceConfiguration.LogoutRedirectParameter}={Uri.EscapeDataString(baseUri)}";
            
            return Redirect(logoutUrl);
        }
        else
        {
            return Redirect("/login");
        }
    }
    
    /// <summary>
    /// Processes a user registration request, creating a new user if all validations pass.
    /// Requires a valid security token that is consumed when used (can only be used once).
    /// </summary>
    /// <param name="request">The registration request containing user details and security token</param>
    /// <returns>Success response if registration succeeds, error response with message if it fails</returns>
    [HttpPost]
    [Route("/register")]
    [IgnoreAntiforgeryToken]
    public async Task<ActionResult<RegistrationResponse>> Register([FromBody] RegistrationRequest request)
    {
        try
        {
            var isFirstUser = !dataManager.Users.Any();

            // First user on a fresh install doesn't need a token; all others do
            if (!isFirstUser)
            {
                if (!await tokenService.ValidateAndUseToken(request.SecurityToken))
                {
                    return BadRequest(new RegistrationResponse
                    {
                        Success = false,
                        Message = "Invalid or expired security token"
                    });
                }
            }

            // Check for existing username
            if (dataManager.Users.Any(u => u.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new RegistrationResponse
                {
                    Success = false,
                    Message = "Username already exists"
                });
            }

            // Check for existing email
            if (dataManager.Users.Any(u => u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new RegistrationResponse
                {
                    Success = false,
                    Message = "Email already exists"
                });
            }

            // Create new user — first user gets Admin role, same as SSO flow
            await dataManager.CreateUser(new User
            {
                Username = request.Username,
                Name = request.Name,
                Email = request.Email,
                PasswordHash = Refund.DataModel.User.HashPassword(request.Password),
                Role = isFirstUser ? UserRole.Admin : UserRole.User
            });

            return Ok(new RegistrationResponse 
            { 
                Success = true,
                Message = "Registration successful"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new RegistrationResponse 
            { 
                Success = false,
                Message = "Registration failed: " + ex.Message
            });
        }
    }
}

/// <summary>
/// Data transfer object for login requests.
/// </summary>
public class LoginRequest
{
    /// <summary>
    /// Username for authentication.
    /// </summary>
    public string Username { get; set; }
    
    /// <summary>
    /// Password for authentication.
    /// </summary>
    public string Password { get; set; }
    
    /// <summary>
    /// Flag indicating whether the user should remain logged in after browser session ends.
    /// </summary>
    public bool RememberMe { get; set; }
}