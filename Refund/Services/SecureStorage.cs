using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;

namespace Refund.Services;

/// <summary>
/// Provides thread-safe in-memory storage for sensitive data with encryption.
/// </summary>
/// <remarks>
/// This class securely stores sensitive data in memory using ASP.NET Core's data protection API.
/// The data is encrypted at rest in memory and only decrypted when explicitly retrieved.
/// 
/// It's designed for temporary sensitive data like:
/// - Authentication tokens
/// - PKCE code verifiers during OAuth flows
/// - Session-bound secrets
/// 
/// Note that this storage is in-memory only and does not persist across application restarts.
/// It's most suitable for web applications with a single instance. For distributed scenarios,
/// consider using a distributed cache with encryption.
/// </remarks>
public class MemorySecureStorage
{
    private readonly ConcurrentDictionary<string, string> _storage = new();
    private readonly IDataProtector _protector;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="MemorySecureStorage"/> class.
    /// </summary>
    /// <param name="dataProtectionProvider">The data protection provider used for encryption</param>
    public MemorySecureStorage(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("Relay.SecureStorage");
    }

    /// <summary>
    /// Retrieves a value from secure storage.
    /// </summary>
    /// <param name="key">The key of the value to retrieve</param>
    /// <returns>The decrypted value, or null if the key doesn't exist</returns>
    public string Get(string key)
    {
        if (_storage.TryGetValue(key, out var protectedData))
            return _protector.Unprotect(protectedData);
        
        return null;
    }

    /// <summary>
    /// Stores a value in secure storage.
    /// </summary>
    /// <param name="key">The key to associate with the value</param>
    /// <param name="value">The value to encrypt and store</param>
    /// <remarks>
    /// If the key already exists, its value will be updated.
    /// The value is encrypted before being stored in memory.
    /// </remarks>
    public void Set(string key, string value)
    {
        var protectedData = _protector.Protect(value);
        _storage.AddOrUpdate(key, protectedData, (_, _) => protectedData);
    }

    /// <summary>
    /// Removes a value from secure storage.
    /// </summary>
    /// <param name="key">The key of the value to remove</param>
    /// <remarks>
    /// If the key doesn't exist, this method has no effect.
    /// </remarks>
    public void Remove(string key)
    {
        _storage.TryRemove(key, out _);
    }
}