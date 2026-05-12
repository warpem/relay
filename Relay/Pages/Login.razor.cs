using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Configuration;
using Refund.Services.Core.DataManager;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;

namespace Relay.Pages;

/// <summary>
/// Login page component handling user authentication and registration.
/// Provides user interface for both login and registration forms with validation.
/// Integrates with the AuthenticationController for actual authentication and user creation.
/// </summary>
public partial class Login : IAsyncDisposable
{
    /// <summary>
    /// Authentication configuration that determines the authentication type and behavior.
    /// </summary>
    [Inject]
    public AuthenticationConfiguration AuthConfig { get; set; }
    
    /// <summary>
    /// Navigation manager for handling client-side navigation and redirects after authentication.
    /// </summary>
    [Inject]
    public NavigationManager NavigationManager { get; set; }
    
    /// <summary>
    /// JavaScript runtime for interop with browser APIs, used specifically for fetch-based login
    /// to maintain proper authentication cookies without page refreshes.
    /// </summary>
    [Inject]
    public IJSRuntime JsRuntime { get; set; }
    
    /// <summary>
    /// HTTP context accessor for checking the user's current authentication status
    /// to automatically redirect authenticated users.
    /// </summary>
    [Inject]
    public IHttpContextAccessor HttpContextAccessor { get; set; }
    
    /// <summary>
    /// HTTP client for making API requests to the registration endpoint.
    /// </summary>
    [Inject]
    public HttpClient HttpClient { get; set; }
    
    /// <summary>
    /// Data manager for checking whether any users exist (fresh install detection).
    /// </summary>
    [Inject]
    public DataManager DataManager { get; set; }

    /// <summary>
    /// Toast service for showing success/error notifications during authentication operations.
    /// </summary>
    [Inject]
    public IToastService ToastService { get; set; }
    
    /// <summary>
    /// Logger for authentication operations.
    /// </summary>
    [Inject]
    public ILogger<Login> Logger { get; set; } = default!;

    private readonly LoginModel _loginModel = new();
    private readonly RegistrationRequest _registrationModel = new();
    private bool _isRegistering;
    private bool _isFirstUser;
    private string? _errorMessage;
    private bool _isLoading;
    private IJSObjectReference? _metaballModule;

    /// <summary>
    /// Initializes the login page. If the user is already authenticated, redirects to the home page.
    /// </summary>
    protected override void OnInitialized()
    {
        var isAuthenticated = HttpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
        if (isAuthenticated)
            NavigationManager.NavigateTo("/");

        _isFirstUser = !DataManager.Users.Any();
        if (_isFirstUser)
        {
            _isRegistering = true;
            _registrationModel.SecurityToken = "------"; // Placeholder to pass client validation; server skips token check for first user
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _metaballModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Pages/Login.razor.js");
            await _metaballModule.InvokeVoidAsync("start");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_metaballModule is not null)
        {
            try
            {
                await _metaballModule.InvokeVoidAsync("stop");
                await _metaballModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
    }
    
    /// <summary>
    /// Handles keyboard events for the login form, triggering login on Enter key press.
    /// </summary>
    /// <param name="e">Keyboard event arguments</param>
    private async Task HandleLoginKeyPress(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !_isRegistering && !_isLoading)
            await HandleNativeLogin();
    }

    /// <summary>
    /// Processes the login form submission using native authentication.
    /// Submits credentials via fetch API to the authentication endpoint.
    /// </summary>
    private async Task HandleNativeLogin()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;
            
            var loginPayload = new
            {
                username = _loginModel.Username,
                password = _loginModel.Password,
                rememberMe = _loginModel.RememberMe
            };

            // Use JavaScript interop to make a fetch request to the login endpoint
            var result = await JsRuntime.InvokeAsync<bool>("exampleLogin.loginViaFetch", 
                                                             [loginPayload]);

            if (result)
            {
                // Force a full page reload to establish the authentication context
                NavigationManager.NavigateTo("/", forceLoad: true);
            }
            else
            {
                _errorMessage = "Login failed, please check your username/password.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login failed for user {Username}", _loginModel.Username);
            _errorMessage = "Login failed. Please try again.";
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// Processes the registration form submission.
    /// Creates a new user account if validation passes and the security token is valid.
    /// </summary>
    private async Task HandleRegistration()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;

            var response = await HttpClient.PostAsJsonAsync(NavigationManager.BaseUri + "register", _registrationModel);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<RegistrationResponse>();
                if (result?.Success == true)
                {
                    ToastService.ShowSuccess("Registration successful! You can now log in.");
                    _isRegistering = false;
                    // Pre-fill the login form with the registration username
                    _loginModel.Username = _registrationModel.Username;
                    return;
                }
            }

            var error = await response.Content.ReadFromJsonAsync<RegistrationResponse>();
            _errorMessage = error?.Message ?? "Registration failed. Please try again.";
        }
        catch (Exception ex)
        {
            _errorMessage = "An error occurred during registration. Please try again.";
            Logger.LogError(ex, "Registration error for user {Username}", _registrationModel.Username);
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// Model for login form with validation attributes.
    /// </summary>
    private class LoginModel
    {
        /// <summary>
        /// Username for login authentication.
        /// </summary>
        [Required(ErrorMessage = "Username is required")]
        public string Username { get; set; } = "";

        /// <summary>
        /// Password for login authentication.
        /// </summary>
        [Required(ErrorMessage = "Password is required")]
        [MinLength(6, ErrorMessage = "Password must be at least 6 characters")]
        public string Password { get; set; } = "";

        /// <summary>
        /// Option to keep the user logged in between sessions.
        /// </summary>
        public bool RememberMe { get; set; }
    }
}

/// <summary>
/// Data transfer object for user registration requests with validation attributes.
/// Used by the /register API endpoint in AuthenticationController to create new user accounts.
/// </summary>
public class RegistrationRequest
{
    /// <summary>
    /// Unique username for the new account. Must only contain alphanumeric characters, underscores, and hyphens.
    /// Used to check for existing users to prevent duplicate accounts and as the primary identifier for login.
    /// </summary>
    [Required(ErrorMessage = "Username is required")]
    [MinLength(3, ErrorMessage = "Username must be at least 3 characters")]
    [RegularExpression(@"^[a-zA-Z0-9_-]+$", ErrorMessage = "Username can only contain letters, numbers, underscores and hyphens")]
    public string Username { get; set; }

    /// <summary>
    /// Display name for the user shown in the application interface.
    /// Stored directly in the User entity during account creation.
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [MinLength(2, ErrorMessage = "Name must be at least 2 characters")]
    public string Name { get; set; }

    /// <summary>
    /// Email address for the user's account.
    /// Checked for uniqueness during registration to prevent duplicate accounts with the same email.
    /// </summary>
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public string Email { get; set; }

    /// <summary>
    /// Password for the new account.
    /// Hashed using User.HashPassword before being stored in the database.
    /// </summary>
    [Required(ErrorMessage = "Password is required")]
    [MinLength(6, ErrorMessage = "Password must be at least 6 characters")]
    public string Password { get; set; }

    /// <summary>
    /// Security token required for registration to prevent unauthorized account creation.
    /// This token is validated by SecurityTokenService.ValidateAndUseToken() before account creation is allowed.
    /// Each token can only be used once.
    /// </summary>
    [Required(ErrorMessage = "Security token is required")]
    [Length(6, 6, ErrorMessage = "Security token must be exactly 6 characters")]
    public string SecurityToken { get; set; }
}

/// <summary>
/// Response model for registration requests.
/// Used by the AuthenticationController to return results of registration attempts to the client.
/// Contains both success status and descriptive messages for UI feedback.
/// </summary>
public class RegistrationResponse
{
    /// <summary>
    /// Indicates whether the registration was successful.
    /// Set to false when registration fails due to duplicate username/email or invalid security token.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Message describing the result of the registration attempt.
    /// Contains specific error details such as "Username already exists",
    /// "Email already in use", or "Invalid or expired security token".
    /// </summary>
    public string Message { get; set; }
}