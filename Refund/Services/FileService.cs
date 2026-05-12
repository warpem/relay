using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Refund.Services;

/// <summary>
/// Provides secure file access by mapping file paths to cryptographic hashes.
/// </summary>
/// <remarks>
/// This service creates a secure indirection layer for file access:
/// 
/// 1. Instead of exposing actual file paths to clients, it generates secure hashes that serve 
///    as access tokens.
/// 2. These hash tokens are used in URLs served to clients.
/// 3. When a client requests a file using a hash token, the service maps it back to the actual 
///    file path.
/// 
/// This approach has several security benefits:
/// - Prevents path traversal attacks
/// - Hides the actual file system structure from clients
/// - Allows for granular access control to files
/// - Prevents direct file access without proper authorization
/// 
/// The service maintains bidirectional mappings between file paths and their corresponding hashes
/// to efficiently handle both hash generation and resolution.
/// </remarks>
public class FileService
{
    private readonly ILogger<FileService> _logger;
    
    /// <summary>
    /// Maps file paths to their corresponding hash values.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pathToHash = new();
    
    /// <summary>
    /// Maps hash values back to their corresponding file paths.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _hashToPath = new();
    
    /// <summary>
    /// Tracks file access frequency for debugging
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _accessCounts = new();

    public FileService(ILogger<FileService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a secure URL for accessing the specified file.
    /// </summary>
    /// <param name="filePath">The physical path of the file</param>
    /// <returns>A secure URL containing a hash token that maps to the file path</returns>
    /// <remarks>
    /// This method:
    /// 1. Checks if a hash already exists for the file path
    /// 2. If not, creates a new hash based on the file path
    /// 3. Handles hash collisions by generating a GUID if needed
    /// 4. Stores the mapping in both dictionaries
    /// 5. Returns a URL that can be used to access the file
    /// 
    /// The URL format is: /api/file/{hash}
    /// </remarks>
    public string GetUrl(string filePath)
    {
        if(_pathToHash.TryGetValue(filePath, out var existingHash))
            return $"/api/file/{existingHash}";

        var newHash = GetHash(filePath);

        while(_hashToPath.ContainsKey(newHash))
            newHash = Guid.NewGuid().ToString();

        _pathToHash[filePath] = newHash;
        _hashToPath[newHash] = filePath;

        return $"/api/file/{newHash}";
    }

    /// <summary>
    /// Attempts to get the file path associated with a hash token.
    /// </summary>
    /// <param name="hash">The hash token</param>
    /// <param name="filePath">When this method returns, contains the file path associated with the hash if found; otherwise, null</param>
    /// <returns>true if the hash was found and mapped to a file path; otherwise, false</returns>
    public bool TryGetPath(string hash, out string filePath)
    {
        var found = _hashToPath.TryGetValue(hash, out filePath);
        if (found && filePath != null)
        {
            var count = _accessCounts.AddOrUpdate(filePath, 1, (key, value) => value + 1);
            _logger.LogDebug("File access: {FilePath} (count: {Count})", filePath, count);
            
            // Log heavy access patterns
            if (count % 100 == 0)
            {
                _logger.LogWarning("High file access detected: {FilePath} accessed {Count} times", filePath, count);
            }
        }
        return found;
    }

    /// <summary>
    /// Computes a SHA-1 hash of the input string.
    /// </summary>
    /// <param name="input">The string to hash</param>
    /// <returns>A lowercase hexadecimal string representation of the hash</returns>
    private string GetHash(string input)
    {
        using(var sha = SHA1.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(bytes);

            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}