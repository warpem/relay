using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Refund.Services;

/// <summary>
/// Manages security tokens for one-time authentication operations.
/// </summary>
/// <remarks>
/// This service provides functionality for generating, validating, and managing
/// short-lived security tokens. These tokens are typically used for operations like:
/// 
/// - Password reset links
/// - Email verification
/// - One-time authentication links
/// - Secure file download links
/// 
/// The service runs as a hosted service, cleaning up expired tokens periodically,
/// and persists tokens to disk to survive application restarts.
/// </remarks>
public class SecurityTokenService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<SecurityTokenService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _tokensPath;
    private readonly ConcurrentDictionary<string, SecurityToken> _tokens = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly PeriodicTimer _cleanupTimer;
    private CancellationTokenSource _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityTokenService"/> class.
    /// </summary>
    /// <param name="logger">The logger for this service</param>
    /// <param name="config">The application configuration containing the tokens file path</param>
    public SecurityTokenService(ILogger<SecurityTokenService> logger, RelayConfiguration config)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        _jsonOptions.MakeReadOnly();
        
        _tokensPath = config.TokensPath;
        _cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        LoadTokens();
    }

    /// <summary>
    /// Generates a new security token.
    /// </summary>
    /// <param name="creator">The user creating the token</param>
    /// <returns>A newly created security token</returns>
    /// <remarks>
    /// This method creates a new security token, associates it with the creator, and persists it
    /// to storage. The token is initially valid and unused.
    /// 
    /// Thread safety is ensured using a lock to prevent race conditions when updating
    /// the token collection.
    /// </remarks>
    public async Task<SecurityToken> GenerateToken(ReadOnlyUser creator)
    {
        await _lock.WaitAsync();
        try
        {
            var token = new SecurityToken();
            _tokens[token.Token] = token;
            await SaveTokens();
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Validates a token and marks it as used if valid.
    /// </summary>
    /// <param name="tokenString">The token string to validate</param>
    /// <returns>True if the token is valid and now marked as used, false otherwise</returns>
    /// <remarks>
    /// This method implements the one-time use security token pattern. A token can
    /// only be successfully validated once; after that, it's marked as used and will
    /// no longer validate.
    /// 
    /// Thread safety is ensured using a lock to prevent race conditions when updating
    /// the token's used status.
    /// </remarks>
    public async Task<bool> ValidateAndUseToken(string tokenString)
    {
        await _lock.WaitAsync();
        try
        {
            if (_tokens.TryGetValue(tokenString, out var token) && token.IsValid())
            {
                token.IsUsed = true;
                await SaveTokens();
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invalidates all tokens by marking them as used.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is typically used for security events like password changes,
    /// when all existing tokens should be invalidated as a precaution.
    /// 
    /// Thread safety is ensured using a lock to prevent race conditions when updating
    /// the tokens' used status.
    /// </remarks>
    public async Task InvalidateAllTokens()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var token in _tokens.Values)
                token.IsUsed = true;
            
            await SaveTokens();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes expired or used tokens from the collection.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is called periodically by the cleanup timer to prevent
    /// the token collection from growing indefinitely. It removes tokens that
    /// are either used or have exceeded their expiration time.
    /// 
    /// If any tokens are removed, the updated collection is persisted to storage.
    /// </remarks>
    private async Task CleanupExpiredTokens()
    {
        await _lock.WaitAsync();
        try
        {
            var expiredTokens = _tokens.Values.Where(t => !t.IsValid())
                                              .Select(t => t.Token)
                                              .ToList();

            foreach (var token in expiredTokens)
                _tokens.TryRemove(token, out _);

            if (expiredTokens.Any())
                await SaveTokens();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Loads tokens from persistent storage.
    /// </summary>
    /// <remarks>
    /// This method is called during service initialization to restore tokens
    /// that were persisted before the application was last shut down.
    /// 
    /// Any errors during loading are logged but don't prevent the service from starting.
    /// </remarks>
    private void LoadTokens()
    {
        if (!File.Exists(_tokensPath)) return;

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(_tokensPath));
            if (json == null) return;

            var tokens = json["Tokens"].AsArray();
            foreach (var tokenNode in tokens)
            {
                var token = new SecurityToken();
                token.ReadFromJson(tokenNode);
                _tokens[token.Token] = token;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading security tokens");
        }
    }

    /// <summary>
    /// Saves the current token collection to persistent storage.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is called whenever tokens are added, used, or expired to ensure
    /// that the persistent storage is kept up-to-date with the in-memory collection.
    /// 
    /// Any errors during saving are logged but don't interrupt the operation that
    /// triggered the save.
    /// </remarks>
    private async Task SaveTokens()
    {
        try
        {
            var json = new JsonObject
            {
                ["Tokens"] = new JsonArray(_tokens.Values.Select(t =>
                {
                    var node = new JsonObject();
                    t.WriteToJson(node);
                    return node;
                }).ToArray<JsonNode>())
            };

            await File.WriteAllTextAsync(_tokensPath, json.ToJsonString(_jsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving security tokens");
        }
    }

    /// <summary>
    /// Starts the token cleanup background task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that can be used to cancel the task</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is called by the ASP.NET Core host when the application starts.
    /// It sets up a background task that periodically cleans up expired tokens.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunCleanupLoop(_cts.Token);
    }

    /// <summary>
    /// Runs the token cleanup loop on a background task.
    /// </summary>
    /// <param name="ct">The cancellation token that signals when the loop should terminate</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method runs in a background task, periodically cleaning up expired tokens
    /// until the application shuts down.
    /// </remarks>
    private async Task RunCleanupLoop(CancellationToken ct)
    {
        try
        {
            while (await _cleanupTimer.WaitForNextTickAsync(ct))
                await CleanupExpiredTokens();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Stops the token cleanup background task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that can be used to cancel the stop operation</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is called by the ASP.NET Core host when the application is shutting down.
    /// It gracefully stops the background cleanup task.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Disposes of resources used by the service.
    /// </summary>
    /// <returns>A task representing the asynchronous dispose operation</returns>
    /// <remarks>
    /// This method ensures that all resources are properly released when the service
    /// is disposed, including the cancellation token source, timer, and lock.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cts != null)
            {
                await _cts.CancelAsync();
                _cts.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing security token service");
        }

        _cleanupTimer?.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// Represents a security token for one-time authentication operations.
/// </summary>
/// <remarks>
/// This class extends <see cref="RelayBase"/> to support JSON serialization and
/// provides properties and methods for managing the lifecycle of a security token.
/// 
/// Tokens are:
/// - Generated with cryptographically secure random values
/// - Valid for up to 7 days
/// - Usable only once
/// - Persisted to disk to survive application restarts
/// </remarks>
public class SecurityToken : RelayBase
{
    /// <summary>
    /// Gets or sets the token string value.
    /// </summary>
    [RelayProperty]
    public string Token { get; set; }
    
    /// <summary>
    /// Gets or sets the date and time when the token was created.
    /// </summary>
    [RelayProperty]
    public DateTime CreationDate { get; set; }
    
    /// <summary>
    /// Gets or sets a value indicating whether the token has been used.
    /// </summary>
    /// <remarks>
    /// Once a token is used, it can no longer be validated.
    /// </remarks>
    [RelayProperty] 
    public bool IsUsed { get; set; }
    
    /// <summary>
    /// Gets or sets the user who created the token.
    /// </summary>
    public User CreatedBy { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityToken"/> class.
    /// </summary>
    /// <remarks>
    /// Creates a new token with a random value, current timestamp, and initial unused state.
    /// </remarks>
    public SecurityToken()
    {
        Token = GenerateToken();
        CreationDate = DateTime.UtcNow;
        IsUsed = false;
    }

    /// <summary>
    /// Generates a cryptographically secure random token string.
    /// </summary>
    /// <returns>A URL-safe token string</returns>
    /// <remarks>
    /// The token is a 6-character URL-safe base64 string generated from
    /// cryptographically secure random bytes. This makes tokens both
    /// secure and easily usable in URLs.
    /// </remarks>
    private static string GenerateToken()
    {
        var tokenBytes = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(tokenBytes);
        }
        // Convert to URL-safe base64 string
        return Convert.ToBase64String(tokenBytes)
                      .Replace('+', '-')
                      .Replace('/', '_')
                      .Replace("=", "")
                      .Substring(0, 6);
    }

    /// <summary>
    /// Determines whether the token is valid.
    /// </summary>
    /// <returns>true if the token is unused and not expired; otherwise, false</returns>
    /// <remarks>
    /// A token is considered valid if:
    /// 1. It has not been used yet
    /// 2. It is less than 7 days old
    /// </remarks>
    public bool IsValid() => !IsUsed && (DateTime.UtcNow - CreationDate).TotalDays <= 7;
}