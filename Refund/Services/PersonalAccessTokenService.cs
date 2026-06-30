using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Refund.Configuration;
using Refund.DataModel;

namespace Refund.Services;

/// <summary>
/// File-backed store of personal access tokens used to authenticate agents over MCP.
/// Mirrors <see cref="SecurityTokenService"/>: a singleton + hosted service with periodic
/// persistence. Only token hashes are stored; raw tokens are returned once from <see cref="Generate"/>.
/// </summary>
public class PersonalAccessTokenService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<PersonalAccessTokenService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _path;
    private readonly ConcurrentDictionary<string, PersonalAccessToken> _tokens = new(); // key: TokenHash
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(1));
    private CancellationTokenSource _cts;
    private volatile bool _dirty;

    public PersonalAccessTokenService(ILogger<PersonalAccessTokenService> logger, RelayConfiguration config)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        _jsonOptions.MakeReadOnly();
        _path = config.PatsPath;
        Load();
    }

    public static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }

    private static string NewRawToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "relay_pat_" + b64;
    }

    public async Task<string> Generate(int ownerUserId, string name,
        AccessLevel projectAccess, AccessLevel spaceAccess, AccessLevel jobAccess,
        DateTime? expiry = null)
    {
        var raw = NewRawToken();
        await _lock.WaitAsync();
        try
        {
            var pat = new PersonalAccessToken
            {
                Id = _tokens.IsEmpty ? 1 : _tokens.Values.Max(t => t.Id) + 1,
                TokenHash = HashToken(raw),
                Name = name,
                OwnerUserId = ownerUserId,
                CreationDate = DateTime.UtcNow,
                LastUsedDate = null,
                ExpirationDate = expiry,
                ProjectAccess = projectAccess,
                SpaceAccess = spaceAccess,
                JobAccess = jobAccess
            };
            _tokens[pat.TokenHash] = pat;
            await Save();
        }
        finally
        {
            _lock.Release();
        }
        return raw;
    }

    public int? Validate(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        if (!_tokens.TryGetValue(HashToken(rawToken), out var pat)) return null;
        if (pat.IsExpired) return null;
        // Best-effort last-used stamp. Written without _lock: the DateTime? write is atomic on 64-bit
        // and _tokens enumeration in Save() is safe (ConcurrentDictionary); flushed by the cleanup loop.
        pat.LastUsedDate = DateTime.UtcNow;
        _dirty = true;
        return pat.OwnerUserId;
    }

    public IReadOnlyList<PersonalAccessToken> ListForUser(int ownerUserId) =>
        _tokens.Values.Where(t => t.OwnerUserId == ownerUserId).OrderBy(t => t.CreationDate).ToList();

    public async Task Revoke(int ownerUserId, int tokenId)
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _tokens.FirstOrDefault(kvp => kvp.Value.Id == tokenId && kvp.Value.OwnerUserId == ownerUserId);
            if (entry.Key != null && _tokens.TryRemove(entry.Key, out _))
                await Save();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(_path));
            var arr = json?["Tokens"]?.AsArray();
            if (arr == null) return;
            foreach (var node in arr)
            {
                var pat = new PersonalAccessToken();
                pat.ReadFromJson(node);
                _tokens[pat.TokenHash] = pat;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading personal access tokens");
        }
    }

    private async Task Save()
    {
        try
        {
            var json = new JsonObject
            {
                ["Tokens"] = new JsonArray(_tokens.Values.Select(t =>
                {
                    var node = new JsonObject();
                    t.WriteToJson(node);
                    return (JsonNode)node;
                }).ToArray())
            };
            await File.WriteAllTextAsync(_path, json.ToJsonString(_jsonOptions));
            _dirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving personal access tokens");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunLoop(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunLoop(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
            {
                await _lock.WaitAsync(ct);
                try
                {
                    var expired = _tokens.Values.Where(t => t.IsExpired).Select(t => t.TokenHash).ToList();
                    foreach (var h in expired) _tokens.TryRemove(h, out _);
                    if (expired.Count > 0 || _dirty) await Save();
                }
                finally { _lock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null) { await _cts.CancelAsync(); _cts.Dispose(); _cts = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null) { await _cts.CancelAsync(); _cts.Dispose(); }
        _timer.Dispose();
        _lock.Dispose();
    }
}
