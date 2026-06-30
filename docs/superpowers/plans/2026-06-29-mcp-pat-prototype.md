# MCP Read-Only Prototype with Personal Access Tokens — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Relay reachable by an LLM agent over MCP, authenticated with user-minted Personal Access Tokens, exposing read-only tools (projects, spaces, jobs, job-type catalog).

**Architecture:** Unit-testable core (PAT model, PAT service, MCP DTO projections) lives in the `Refund` library and is covered by xUnit tests. The glue and UI (a custom `Pat` bearer auth scheme, the in-process MCP host wired in `Relay/Program.cs`, the MCP tool class, and a new Blazor "Personal" overlay) live in the `Relay` web project and are verified by `dotnet build` plus manual end-to-end checks, because the `Refund.Tests` project references only `Refund`, not `Relay`.

**Tech Stack:** C# / .NET 10, ASP.NET Core, Blazor Server, FluentUI Blazor, Autofac DI, xUnit, `ModelContextProtocol.AspNetCore` SDK.

## Global Constraints

- **Read-only only.** No tool may create, modify, queue, or delete Relay data in this prototype.
- **Permission scoping is mandatory.** Every tool starts from the authenticated user and returns only what that user can see (via `DataManager.GetUserProjects(user)` and the object graph reachable from it).
- **Raw tokens are shown exactly once** at creation and **never persisted**; only a SHA-256 hash is stored.
- **Token format:** `relay_pat_` + base64url(32 random bytes).
- **MCP endpoint path:** `/api/mcp` (already exempt from the login-redirect middleware in `Relay/Program.cs`).
- **Auth scheme name:** `"Pat"` (string constant, used in registration and the endpoint authorization policy).
- **Serialization:** model objects extend `RelayBase` and use `[RelayProperty]`; avoid nullable serialized properties (use `DateTime.MinValue` / `DateTime.MaxValue` sentinels) since the `RelayBase` serializer's handling of `Nullable<T>` is unverified.
- **Existing patterns to mirror:** `Refund/Services/SecurityTokenService.cs` (file-backed token store) and `Relay/Panels/Left/LeftBar.razor[.cs]` (overlay open buttons).

---

### Task 1: `PersonalAccessToken` model + config path

**Files:**
- Create: `Refund/DataModel/PersonalAccessToken.cs`
- Modify: `Refund/Configuration/RelayConfiguration.cs` (add `PatsPath` near `TokensPath`, ~line 105)
- Test: `Refund.Tests/Mcp/PersonalAccessTokenTests.cs`

**Interfaces:**
- Produces: `PersonalAccessToken : RelayBase` with `int Id`, `string TokenHash`, `string Name`, `int OwnerUserId`, `DateTime CreationDate`, `DateTime LastUsedDate` (MinValue = never), `DateTime ExpirationDate` (MaxValue = no expiry), and `bool IsExpired`.
- Produces: `RelayConfiguration.PatsPath` (string, default `"pats.relay"`).

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/PersonalAccessTokenTests.cs`:

```csharp
using Refund.DataModel;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenTests
{
    [Fact]
    public void NewToken_HasNoExpiryByDefault_AndIsNotExpired()
    {
        var pat = new PersonalAccessToken();
        Assert.Equal(DateTime.MaxValue, pat.ExpirationDate);
        Assert.Equal(DateTime.MinValue, pat.LastUsedDate);
        Assert.False(pat.IsExpired);
    }

    [Fact]
    public void Token_WithPastExpiration_IsExpired()
    {
        var pat = new PersonalAccessToken { ExpirationDate = DateTime.UtcNow.AddMinutes(-1) };
        Assert.True(pat.IsExpired);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~PersonalAccessTokenTests"`
Expected: FAIL — compile error, `PersonalAccessToken` does not exist.

- [ ] **Step 3: Create the model**

Create `Refund/DataModel/PersonalAccessToken.cs`:

```csharp
namespace Refund.DataModel;

/// <summary>
/// A personal access token used to authenticate an LLM agent (over MCP) as a Relay user.
/// Only the SHA-256 hash of the raw token is ever stored; the raw value is shown once at creation.
/// </summary>
public class PersonalAccessToken : RelayBase
{
    [RelayProperty] public int Id { get; set; }

    /// <summary>SHA-256 (hex) hash of the raw token. The raw token is never persisted.</summary>
    [RelayProperty] public string TokenHash { get; set; } = "";

    /// <summary>User-supplied label, e.g. "Claude on my laptop".</summary>
    [RelayProperty] public string Name { get; set; } = "";

    /// <summary>Id of the owning <see cref="User"/>.</summary>
    [RelayProperty] public int OwnerUserId { get; set; }

    [RelayProperty] public DateTime CreationDate { get; set; } = DateTime.UtcNow;

    /// <summary><see cref="DateTime.MinValue"/> means the token has never been used.</summary>
    [RelayProperty] public DateTime LastUsedDate { get; set; } = DateTime.MinValue;

    /// <summary><see cref="DateTime.MaxValue"/> means the token never expires.</summary>
    [RelayProperty] public DateTime ExpirationDate { get; set; } = DateTime.MaxValue;

    public bool IsExpired => ExpirationDate <= DateTime.UtcNow;
}
```

- [ ] **Step 4: Add the config path**

In `Refund/Configuration/RelayConfiguration.cs`, immediately after the `TokensPath` property (~line 105), add:

```csharp
    /// <summary>Path to the file storing personal access tokens (MCP/agent auth).</summary>
    public string PatsPath { get; set; } = "pats.relay";
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~PersonalAccessTokenTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add Refund/DataModel/PersonalAccessToken.cs Refund/Configuration/RelayConfiguration.cs Refund.Tests/Mcp/PersonalAccessTokenTests.cs
git commit -m "feat: add PersonalAccessToken model and PatsPath config"
```

---

### Task 2: `PersonalAccessTokenService`

**Files:**
- Create: `Refund/Services/PersonalAccessTokenService.cs`
- Test: `Refund.Tests/Mcp/PersonalAccessTokenServiceTests.cs`

**Interfaces:**
- Consumes: `PersonalAccessToken` (Task 1), `RelayConfiguration.PatsPath` (Task 1).
- Produces: `PersonalAccessTokenService` (singleton + `IHostedService`) with:
  - `Task<string> Generate(int ownerUserId, string name, DateTime? expiry = null)` — returns the raw token once.
  - `int? Validate(string rawToken)` — returns owner id or null; stamps `LastUsedDate` in memory.
  - `IReadOnlyList<PersonalAccessToken> ListForUser(int ownerUserId)`.
  - `Task Revoke(int ownerUserId, int tokenId)`.
  - `static string HashToken(string rawToken)`.

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/Mcp/PersonalAccessTokenServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.Services;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenServiceTests
{
    private static PersonalAccessTokenService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var config = new RelayConfiguration { PatsPath = path };
        return new PersonalAccessTokenService(NullLogger<PersonalAccessTokenService>.Instance, config);
    }

    [Fact]
    public async Task Generate_ReturnsPrefixedRawToken()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(ownerUserId: 7, name: "laptop");
        Assert.StartsWith("relay_pat_", raw);
    }

    [Fact]
    public async Task Generate_DoesNotPersistRawToken()
    {
        var svc = NewService(out var path);
        var raw = await svc.Generate(7, "laptop");
        var fileContents = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(raw, fileContents);
        Assert.Contains(PersonalAccessTokenService.HashToken(raw), fileContents);
    }

    [Fact]
    public async Task Validate_ReturnsOwnerId_AndStampsLastUsed()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(42, "laptop");
        var ownerId = svc.Validate(raw);
        Assert.Equal(42, ownerId);
        Assert.NotEqual(DateTime.MinValue, svc.ListForUser(42).Single().LastUsedDate);
    }

    [Fact]
    public void Validate_UnknownToken_ReturnsNull()
    {
        var svc = NewService(out _);
        Assert.Null(svc.Validate("relay_pat_bogus"));
    }

    [Fact]
    public async Task Validate_ExpiredToken_ReturnsNull()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(1, "old", expiry: DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(svc.Validate(raw));
    }

    [Fact]
    public async Task Revoke_RemovesToken_SoItNoLongerValidates()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(5, "laptop");
        var id = svc.ListForUser(5).Single().Id;
        await svc.Revoke(5, id);
        Assert.Empty(svc.ListForUser(5));
        Assert.Null(svc.Validate(raw));
    }

    [Fact]
    public async Task Revoke_OtherUsersToken_DoesNothing()
    {
        var svc = NewService(out _);
        await svc.Generate(5, "mine");
        var id = svc.ListForUser(5).Single().Id;
        await svc.Revoke(ownerUserId: 999, tokenId: id);
        Assert.Single(svc.ListForUser(5));
    }

    [Fact]
    public async Task Tokens_SurviveReload()
    {
        var svc = NewService(out var path);
        var raw = await svc.Generate(8, "laptop");

        var config = new RelayConfiguration { PatsPath = path };
        var reloaded = new PersonalAccessTokenService(NullLogger<PersonalAccessTokenService>.Instance, config);
        Assert.Equal(8, reloaded.Validate(raw));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~PersonalAccessTokenServiceTests"`
Expected: FAIL — compile error, `PersonalAccessTokenService` does not exist.

(If the build fails because `Microsoft.Extensions.Logging.Abstractions` is not resolvable in the test project, add it: `dotnet add Refund.Tests/Refund.Tests.csproj package Microsoft.Extensions.Logging.Abstractions`.)

- [ ] **Step 3: Implement the service**

Create `Refund/Services/PersonalAccessTokenService.cs`:

```csharp
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

    public async Task<string> Generate(int ownerUserId, string name, DateTime? expiry = null)
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
                LastUsedDate = DateTime.MinValue,
                ExpirationDate = expiry ?? DateTime.MaxValue
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~PersonalAccessTokenServiceTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add Refund/Services/PersonalAccessTokenService.cs Refund.Tests/Mcp/PersonalAccessTokenServiceTests.cs Refund.Tests/Refund.Tests.csproj
git commit -m "feat: add PersonalAccessTokenService with file-backed hashed token store"
```

---

### Task 3: MCP DTOs + projections

**Files:**
- Create: `Refund/Mcp/McpDtos.cs`
- Create: `Refund/Mcp/RelayMcpProjections.cs`
- Test: `Refund.Tests/Mcp/RelayMcpProjectionsTests.cs`

**Interfaces:**
- Consumes: `Job.Types`, `Job.TypeNames`, `Job.TypeCategories`, `Job.TypeUiFields` (populated by `Job.PopulateStatic()`), `UiFieldBase`, `ReadOnlyProject`, `ReadOnlySpace`, `ReadOnlyJob`.
- Produces DTOs: `ProjectDto(int Id, string Alias, string Role)`, `SpaceDto(int Id, string Alias)`, `JobDto(int Id, string Alias, string TypeName, string Status)`, `JobDetailDto(int Id, string Alias, string TypeName, string TypeGuid, string Status)`, `JobTypeParamDto(string Name, string Label, string Type, string? Help, bool Advanced)`, `JobTypeDto(string TypeGuid, string TypeName, string Category, IReadOnlyList<JobTypeParamDto> Parameters)`.
- Produces helpers: `RelayMcpProjections.ComputeProjectRole(int ownerId, IEnumerable<int> memberIds, int currentUserId) -> string`, `RelayMcpProjections.BuildJobTypeCatalog() -> IReadOnlyList<JobTypeDto>`, plus `ToDto(...)` mappers for project/space/job.

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/Mcp/RelayMcpProjectionsTests.cs`:

```csharp
using Refund.DataModel;
using Refund.Mcp;

namespace Refund.Tests.Mcp;

[Collection("JobRegistry")]
public class RelayMcpProjectionsTests
{
    private static readonly object _lock = new();
    private static void EnsurePopulated()
    {
        lock (_lock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    [Theory]
    [InlineData(10, new[] { 20, 30 }, 10, "owner")]
    [InlineData(10, new[] { 20, 30 }, 20, "member")]
    [InlineData(10, new[] { 20, 30 }, 99, "none")]
    public void ComputeProjectRole_ClassifiesCaller(int ownerId, int[] members, int caller, string expected)
    {
        Assert.Equal(expected, RelayMcpProjections.ComputeProjectRole(ownerId, members, caller));
    }

    [Fact]
    public void BuildJobTypeCatalog_IncludesKnownTypeWithParameters()
    {
        EnsurePopulated();
        var catalog = RelayMcpProjections.BuildJobTypeCatalog();
        Assert.NotEmpty(catalog);
        // MotionAndCTF2D, a known concrete job type.
        var motion = catalog.SingleOrDefault(t => t.TypeGuid == "77cdcb73-1bd0-43e0-b206-3d93acecafa8");
        Assert.NotNull(motion);
        Assert.Equal("Motion & CTF", motion!.TypeName);
        Assert.NotEmpty(motion.Parameters);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~RelayMcpProjectionsTests"`
Expected: FAIL — compile error, `Refund.Mcp` namespace / types do not exist.

- [ ] **Step 3: Create the DTOs**

Create `Refund/Mcp/McpDtos.cs`:

```csharp
namespace Refund.Mcp;

/// <summary>Serializable shapes returned by the read-only MCP tools.</summary>
public record ProjectDto(int Id, string Alias, string Role);
public record SpaceDto(int Id, string Alias);
public record JobDto(int Id, string Alias, string TypeName, string Status);
public record JobDetailDto(int Id, string Alias, string TypeName, string TypeGuid, string Status);
public record JobTypeParamDto(string Name, string Label, string Type, string? Help, bool Advanced);
public record JobTypeDto(string TypeGuid, string TypeName, string Category, IReadOnlyList<JobTypeParamDto> Parameters);
```

- [ ] **Step 4: Create the projections**

Create `Refund/Mcp/RelayMcpProjections.cs`:

```csharp
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;

namespace Refund.Mcp;

/// <summary>
/// Pure projections from Relay's read-only model (and the static job-type registry) to MCP DTOs.
/// Kept free of ASP.NET / DataManager dependencies so it is unit-testable in Refund.Tests.
/// </summary>
public static class RelayMcpProjections
{
    public static string ComputeProjectRole(int ownerId, IEnumerable<int> memberIds, int currentUserId)
    {
        if (currentUserId == ownerId) return "owner";
        return memberIds.Contains(currentUserId) ? "member" : "none";
    }

    public static ProjectDto ToDto(ReadOnlyProject p, int currentUserId) =>
        new(p.Id, p.Alias, ComputeProjectRole(p.Owner.Id, p.Members.Select(m => m.Id), currentUserId));

    public static SpaceDto ToDto(ReadOnlySpace s) => new(s.Id, s.Alias);

    public static JobDto ToDto(ReadOnlyJob j) =>
        new(j.Id, j.AliasOrId, j.TypeName, j.Status.ToString());

    public static JobDetailDto ToDetailDto(ReadOnlyJob j) =>
        new(j.Id, j.AliasOrId, j.TypeName, j.TypeGuid, j.Status.ToString());

    public static IReadOnlyList<JobTypeDto> BuildJobTypeCatalog()
    {
        var result = new List<JobTypeDto>();
        foreach (var (typeGuid, clrType) in Job.Types)
        {
            var name = Job.TypeNames.TryGetValue(clrType, out var n) ? n : clrType.Name;
            var category = Job.TypeCategories.FirstOrDefault(kvp => kvp.Value == clrType).Key ?? "";
            var parameters = new List<JobTypeParamDto>();
            if (Job.TypeUiFields.TryGetValue(clrType, out var fields))
                foreach (var (prop, uiField) in fields)
                    parameters.Add(new JobTypeParamDto(
                        Name: prop.Name,
                        Label: uiField.Label ?? prop.Name,
                        Type: prop.PropertyType.Name,
                        Help: string.IsNullOrEmpty(uiField.HelpText) ? null : uiField.HelpText,
                        Advanced: uiField.IsAdvanced));
            result.Add(new JobTypeDto(typeGuid, name, category, parameters));
        }
        return result;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~RelayMcpProjectionsTests"`
Expected: PASS (4 tests: 3 theory cases + 1 fact).

- [ ] **Step 6: Commit**

```bash
git add Refund/Mcp/ Refund.Tests/Mcp/RelayMcpProjectionsTests.cs
git commit -m "feat: add MCP DTOs and read-only model projections"
```

---

### Task 4: PAT bearer auth scheme + MCP host wiring

**Files:**
- Create: `Relay/Services/PatAuthenticationHandler.cs`
- Modify: `Relay/Program.cs` (service registration ~lines 90-91; auth registration ~lines 161-166; endpoint mapping ~line 216)
- Modify: `Relay/Relay.csproj` (add NuGet package)

**Interfaces:**
- Consumes: `PersonalAccessTokenService` (Task 2), `DataManager.FindUser(int)`.
- Produces: auth scheme `"Pat"` that attaches a `ClaimsPrincipal` with `ClaimTypes.Name = username`; an MCP endpoint at `/api/mcp` requiring that scheme; DI registration of `PersonalAccessTokenService` (single shared instance) and the MCP server with `RelayMcpTools` (added in Task 5).

This task is glue in the `Relay` project (not referenced by `Refund.Tests`), so verification is `dotnet build` plus a deterministic auth smoke test (401 without a token).

- [ ] **Step 1: Add the MCP SDK package**

Run: `dotnet add Relay/Relay.csproj package ModelContextProtocol.AspNetCore --prerelease`
Expected: package added to `Relay.csproj`. (Pin the resolved version; `--prerelease` only if no stable version resolves.)

- [ ] **Step 2: Create the auth handler**

Create `Relay/Services/PatAuthenticationHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refund.Services;
using Refund.Services.Core.DataManager;

namespace Relay.Services;

/// <summary>
/// Authenticates requests bearing a Relay personal access token
/// (<c>Authorization: Bearer relay_pat_...</c>) and resolves them to a Relay user.
/// Returns NoResult for any non-PAT request so it never interferes with cookie auth.
/// </summary>
public class PatAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Pat";
    private const string Prefix = "Bearer relay_pat_";

    private readonly PersonalAccessTokenService _pats;
    private readonly DataManager _dataManager;

    public PatAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PersonalAccessTokenService pats,
        DataManager dataManager) : base(options, logger, encoder)
    {
        _pats = pats;
        _dataManager = dataManager;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = header["Bearer ".Length..].Trim();
        var ownerId = _pats.Validate(raw);
        if (ownerId == null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired personal access token"));

        var user = _dataManager.FindUser(ownerId.Value);
        if (user == null)
            return Task.FromResult(AuthenticateResult.Fail("Token owner no longer exists"));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

- [ ] **Step 3: Register the service as a single shared instance**

In `Relay/Program.cs`, after the `SecurityTokenService` registration (lines 90-91), add:

```csharp
// PersonalAccessTokenService stores PATs used to authenticate agents over MCP.
// Register once and reuse the same instance as the hosted service so there is a single store.
builder.Services.AddSingleton<PersonalAccessTokenService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersonalAccessTokenService>());
```

Add the using at the top if not already present: `using Refund.Services;` is already imported (line 12). Add `using Relay.Services;` (the `Relay.Services` namespace is already imported at line 17).

- [ ] **Step 4: Register the Pat auth scheme**

In `Relay/Program.cs`, change the authentication registration (lines 161-166) from:

```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
       .AddCookie(options =>
       {
           options.ExpireTimeSpan = TimeSpan.FromDays(30);
           options.SlidingExpiration = true;
       });
```

to:

```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
       .AddCookie(options =>
       {
           options.ExpireTimeSpan = TimeSpan.FromDays(30);
           options.SlidingExpiration = true;
       })
       .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Relay.Services.PatAuthenticationHandler>(
           Relay.Services.PatAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization();
```

- [ ] **Step 5: Register the MCP server and map the endpoint**

In `Relay/Program.cs`, add the MCP server registration alongside the other `builder.Services` calls (e.g. just before `var app = builder.Build();`, line 169):

```csharp
// In-process MCP server (read-only tools), authenticated by the Pat scheme.
builder.Services.AddMcpServer()
       .WithHttpTransport(o => o.Stateless = true)
       .AddAuthorizationFilters()
       .WithTools<Relay.Services.RelayMcpTools>();
```

Then, after `app.MapControllers();` (line 216), add:

```csharp
// MCP endpoint: requires a valid personal access token (Pat scheme).
var patPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
        Relay.Services.PatAuthenticationHandler.SchemeName)
    .RequireAuthenticatedUser()
    .Build();
app.MapMcp("/api/mcp").RequireAuthorization(patPolicy);
```

Note: `WithTools<RelayMcpTools>()` references the class created in Task 5. Implement Task 5 before building, or temporarily create an empty `RelayMcpTools` stub to satisfy the compiler. The recommended order is to do Task 5's Step 1 (create the class) before building this task.

- [ ] **Step 6: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: build succeeds (after Task 5's class exists).

- [ ] **Step 7: Auth smoke test (manual, deterministic)**

Start the app (`dotnet run --project Relay/Relay.csproj`), then in another shell:

```bash
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5000/api/mcp \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Expected: `401` (no token → unauthorized). Confirm the port from the app's startup log; substitute if different.

- [ ] **Step 8: Commit**

```bash
git add Relay/Services/PatAuthenticationHandler.cs Relay/Program.cs Relay/Relay.csproj
git commit -m "feat: add Pat bearer auth scheme and in-process MCP host at /api/mcp"
```

---

### Task 5: `RelayMcpTools` (read-only tools)

**Files:**
- Create: `Relay/Services/RelayMcpTools.cs`

**Interfaces:**
- Consumes: `IHttpContextAccessor`, `DataManager` (`GetUserProjects`, `FindUser`, `FindProject`, `FindSpace`, `FindJob`), `RelayMcpProjections` + DTOs (Task 3).
- Produces: MCP tools `list_projects`, `list_spaces`, `list_jobs`, `get_job`, `list_job_types`.

This is glue in `Relay`; verification is `dotnet build` + the manual MCP call in Task 8.

- [ ] **Step 1: Create the tool class**

Create `Relay/Services/RelayMcpTools.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using Refund.DataModel.ReadOnly;
using Refund.Mcp;
using Refund.Services.Core.DataManager;

namespace Relay.Services;

/// <summary>
/// Read-only MCP tools exposing the calling user's Relay data. Every method resolves the
/// current user from the authenticated principal and returns only data that user can see.
/// </summary>
[McpServerToolType]
public class RelayMcpTools(IHttpContextAccessor contextAccessor, DataManager dataManager)
{
    private ReadOnlyUser CurrentUser()
    {
        var username = contextAccessor.HttpContext?.User?.Identity?.Name
            ?? throw new InvalidOperationException("No authenticated user.");
        return dataManager.FindUser(username)
            ?? throw new InvalidOperationException("Authenticated user not found.");
    }

    [McpServerTool(Name = "list_projects"), Description("List the projects the current user can access.")]
    public IReadOnlyList<ProjectDto> ListProjects()
    {
        var user = CurrentUser();
        return dataManager.GetUserProjects(user)
            .Select(p => RelayMcpProjections.ToDto(p, user.Id))
            .ToList();
    }

    [McpServerTool(Name = "list_spaces"), Description("List the spaces in a project the current user can access.")]
    public IReadOnlyList<SpaceDto> ListSpaces(
        [Description("The project id.")] int projectId)
    {
        var user = CurrentUser();
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) return [];
        return project.Spaces.Select(RelayMcpProjections.ToDto).ToList();
    }

    [McpServerTool(Name = "list_jobs"), Description("List the jobs in a space, with their status.")]
    public IReadOnlyList<JobDto> ListJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        var space = project?.FindSpace(spaceId);
        if (space == null) return [];
        return space.Jobs.Select(RelayMcpProjections.ToDto).ToList();
    }

    [McpServerTool(Name = "get_job"), Description("Get details for a single job.")]
    public JobDetailDto? GetJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        var job = project?.FindSpace(spaceId)?.FindJob(jobId);
        return job == null ? null : RelayMcpProjections.ToDetailDto(job);
    }

    [McpServerTool(Name = "list_job_types"), Description("List all available job types and their parameters.")]
    public IReadOnlyList<JobTypeDto> ListJobTypes()
    {
        _ = CurrentUser(); // require authentication
        return RelayMcpProjections.BuildJobTypeCatalog();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: build succeeds. (If `FindSpace`/`FindJob` member names differ on the ReadOnly wrappers, correct them against `Refund/DataModel/ReadOnly/ReadOnlyProject.cs` and `ReadOnlySpace.cs` — they are `ReadOnlyProject.FindSpace(int)` and `ReadOnlySpace.FindJob(int)`.)

- [ ] **Step 3: Commit**

```bash
git add Relay/Services/RelayMcpTools.cs
git commit -m "feat: add read-only MCP tools (projects, spaces, jobs, job-type catalog)"
```

---

### Task 6: Personal settings overlay + entry point

**Files:**
- Modify: `Refund/Services/Core/Session/RelaySession.cs:891` (the `OverlayScreenType` enum)
- Modify: `Relay/Shared/MainLayout.razor` (overlay switch, ~lines 60-80)
- Modify: `Relay/Panels/Left/LeftBar.razor` (add a button, ~lines 18-38)
- Modify: `Relay/Panels/Left/LeftBar.razor.cs` (add a handler, ~lines 91-117)
- Create: `Relay/Screens/Overlay/Personal/OverlayPersonal.razor`

**Interfaces:**
- Consumes: `RelaySession.NavigateToAsync`, `OverlayScreenType`.
- Produces: `OverlayScreenType.Personal`; a toolbar button that opens it; `OverlayPersonal` rendering `AccessTokenManager` (Task 7).

This is UI/glue; verification is `dotnet build` + manual click-through in Task 8.

- [ ] **Step 1: Add the enum value**

In `Refund/Services/Core/Session/RelaySession.cs` (line ~891), change:

```csharp
public enum OverlayScreenType { None, Queues, Settings }
```

to:

```csharp
public enum OverlayScreenType { None, Queues, Settings, Personal }
```

- [ ] **Step 2: Render the overlay**

In `Relay/Shared/MainLayout.razor`, inside the `switch (Session.CurrentOverlay)` block (after the `Settings` case, ~line 75), add:

```razor
            case OverlayScreenType.Personal:
                <PageTitle>Relay: Personal</PageTitle>
                <Relay.Screens.Overlay.Personal.OverlayPersonal @key="@("OverlayPersonal")" />
                break;
```

- [ ] **Step 3: Create the overlay shell**

Create `Relay/Screens/Overlay/Personal/OverlayPersonal.razor`:

```razor
@namespace Relay.Screens.Overlay.Personal

<OverlayBase>
    <FluentTabs Style="margin-top: -8px; height: 100%;" Class="tab-h100">
        <FluentTab Label="Access tokens">
            <Relay.Screens.Overlay.Personal.AccessTokenManager />
        </FluentTab>
    </FluentTabs>
</OverlayBase>
```

- [ ] **Step 4: Add the toolbar button**

In `Relay/Panels/Left/LeftBar.razor`, replicate the Settings button markup (in the lines 18-38 button group) for a non-admin-gated "Personal" button. Add (adjust `Icon` to one already imported in this file, e.g. a person icon):

```razor
<FluentButton Appearance="@(Session.CurrentOverlay == OverlayScreenType.Personal ? Appearance.Accent : Appearance.Stealth)"
              Title="Personal settings"
              @onclick="OnPersonalButtonClick">
    <FluentIcon Value="@(new Icons.Regular.Size24.Person())" />
</FluentButton>
```

(Match the exact `FluentButton`/icon style used by the existing Settings/Queues buttons in this file; the snippet above is the shape, not necessarily byte-identical to the surrounding markup.)

- [ ] **Step 5: Add the click handler**

In `Relay/Panels/Left/LeftBar.razor.cs`, next to `OnSettingsButtonClick` (lines 107-117), add:

```csharp
    private async Task OnPersonalButtonClick()
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId   = Session.SpaceId,
            ViewId    = Session.ViewId,
            JobId     = Session.JobId,
            Overlay   = OverlayScreenType.Personal
        });
    }
```

- [ ] **Step 6: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: build succeeds. (`AccessTokenManager` is created in Task 7; create its file first or temporarily stub an empty component so this builds.)

- [ ] **Step 7: Commit**

```bash
git add Refund/Services/Core/Session/RelaySession.cs Relay/Shared/MainLayout.razor Relay/Panels/Left/LeftBar.razor Relay/Panels/Left/LeftBar.razor.cs Relay/Screens/Overlay/Personal/OverlayPersonal.razor
git commit -m "feat: add Personal settings overlay and toolbar entry point"
```

---

### Task 7: `AccessTokenManager` UI panel

**Files:**
- Create: `Relay/Screens/Overlay/Personal/AccessTokenManager.razor`
- Create: `Relay/Screens/Overlay/Personal/AccessTokenManager.razor.cs`

**Interfaces:**
- Consumes: `PersonalAccessTokenService` (`ListForUser`, `Generate`, `Revoke`), `RelaySession.User`, `IToastService`.
- Produces: a panel listing the user's tokens (name, created, last used, expiry, revoke) and a "New token" flow that shows the raw token once.

UI/glue; verification is `dotnet build` + manual flow in Task 8.

- [ ] **Step 1: Create the code-behind**

Create `Relay/Screens/Overlay/Personal/AccessTokenManager.razor.cs`:

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.Services;
using Refund.Services.Core.Session;

namespace Relay.Screens.Overlay.Personal;

public partial class AccessTokenManager : ComponentBase
{
    [Inject] private PersonalAccessTokenService Pats { get; set; } = default!;
    [Inject] private RelaySession Session { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;

    private IReadOnlyList<PersonalAccessToken> _tokens = [];
    private bool _showCreate;
    private string _newName = "";
    private string? _createdRawToken; // shown once after creation

    protected override void OnInitialized() => Refresh();

    private void Refresh() => _tokens = Pats.ListForUser(Session.User.Id);

    private void OpenCreate()
    {
        _newName = "";
        _createdRawToken = null;
        _showCreate = true;
    }

    private async Task CreateToken()
    {
        if (string.IsNullOrWhiteSpace(_newName))
        {
            ToastService.ShowError("Please enter a name for the token.");
            return;
        }
        try
        {
            _createdRawToken = await Pats.Generate(Session.User.Id, _newName.Trim());
            Refresh();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create token: " + exc.Message);
        }
    }

    private void CloseCreate()
    {
        _showCreate = false;
        _createdRawToken = null;
    }

    private async Task RevokeToken(int tokenId)
    {
        try
        {
            await Pats.Revoke(Session.User.Id, tokenId);
            Refresh();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't revoke token: " + exc.Message);
        }
    }

    private static string Format(DateTime dt) =>
        dt == DateTime.MinValue ? "Never"
        : dt == DateTime.MaxValue ? "—"
        : dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
```

- [ ] **Step 2: Create the markup**

Create `Relay/Screens/Overlay/Personal/AccessTokenManager.razor`:

```razor
@namespace Relay.Screens.Overlay.Personal

<div style="padding: 16px; display: flex; flex-direction: column; gap: 12px;">
    <div style="display:flex; align-items:center; justify-content:space-between;">
        <h3 style="margin:0;">Personal access tokens</h3>
        <FluentButton Appearance="Appearance.Accent" @onclick="OpenCreate">New token</FluentButton>
    </div>

    <p style="margin:0; opacity:0.8;">
        Use a personal access token to let an AI agent connect to Relay over MCP at
        <code>/api/mcp</code> as you. Tokens act with your permissions and are read-only in this release.
    </p>

    @if (_tokens.Count == 0)
    {
        <p style="opacity:0.7;">You have no tokens yet.</p>
    }
    else
    {
        <FluentDataGrid Items="@_tokens.AsQueryable()" GridTemplateColumns="2fr 1fr 1fr 1fr auto">
            <PropertyColumn Title="Name" Property="@(t => t.Name)" />
            <TemplateColumn Title="Created">@Format(context.CreationDate)</TemplateColumn>
            <TemplateColumn Title="Last used">@Format(context.LastUsedDate)</TemplateColumn>
            <TemplateColumn Title="Expires">@Format(context.ExpirationDate)</TemplateColumn>
            <TemplateColumn Title="">
                <FluentButton Appearance="Appearance.Stealth" @onclick="@(() => RevokeToken(context.Id))">Revoke</FluentButton>
            </TemplateColumn>
        </FluentDataGrid>
    }
</div>

@if (_showCreate)
{
    <FluentDialog @ondialogdismiss="CloseCreate">
        <FluentDialogBody>
            @if (_createdRawToken == null)
            {
                <h4>New personal access token</h4>
                <FluentTextField @bind-Value="_newName" Placeholder="Name (e.g. Claude on my laptop)" style="width:100%;" />
                <div style="display:flex; gap:8px; margin-top:16px; justify-content:flex-end;">
                    <FluentButton @onclick="CloseCreate">Cancel</FluentButton>
                    <FluentButton Appearance="Appearance.Accent" @onclick="CreateToken">Create</FluentButton>
                </div>
            }
            else
            {
                <h4>Copy your token now</h4>
                <p style="color: var(--error);">
                    This is the only time the token will be shown. Store it somewhere safe.
                </p>
                <FluentTextField Value="@_createdRawToken" ReadOnly="true" style="width:100%;" />
                <div style="display:flex; gap:8px; margin-top:16px; justify-content:flex-end;">
                    <FluentButton Appearance="Appearance.Accent" @onclick="CloseCreate">Done</FluentButton>
                </div>
            }
        </FluentDialogBody>
    </FluentDialog>
}
```

(If the installed FluentUI version's `FluentDataGrid` / `FluentDialog` API differs, adapt to the patterns used elsewhere in `Relay/Screens/Overlay/` — e.g. `OverlayQueues` and `QueueEditor` — keeping the one-time-token behavior intact.)

- [ ] **Step 3: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Relay/Screens/Overlay/Personal/AccessTokenManager.razor Relay/Screens/Overlay/Personal/AccessTokenManager.razor.cs
git commit -m "feat: add access token management panel (list/create/revoke)"
```

---

### Task 8: End-to-end verification

**Files:** none (manual verification + notes).

- [ ] **Step 1: Full build and test**

Run:
```bash
dotnet build Relay/Relay.csproj
dotnet test Refund.Tests/Refund.Tests.csproj
```
Expected: build succeeds; all tests pass.

- [ ] **Step 2: Mint a token in the UI**

Start the app (`dotnet run --project Relay/Relay.csproj`), log in, click the new Personal toolbar button → Access tokens → New token → name it → Create. Copy the displayed `relay_pat_...` value. Confirm it appears in the list afterward and that the raw value is not shown again.

- [ ] **Step 3: Verify auth rejects no/invalid tokens**

```bash
# No token -> 401
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5000/api/mcp \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
# Bogus token -> 401
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5000/api/mcp \
  -H "Authorization: Bearer relay_pat_bogus" \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```
Expected: `401` for both.

- [ ] **Step 4: Drive the tools with MCP Inspector**

Run the official inspector and point it at the endpoint with your token:
```bash
npx @modelcontextprotocol/inspector
```
In the inspector UI: Transport = Streamable HTTP, URL = `http://localhost:5000/api/mcp`, add header `Authorization: Bearer <your token>`. Connect, then:
- `tools/list` shows `list_projects`, `list_spaces`, `list_jobs`, `get_job`, `list_job_types`.
- Call `list_projects` → returns only your projects with correct `role`.
- Call `list_job_types` → returns the catalog with parameters.

- [ ] **Step 5: Verify permission scoping**

With a token for user A, confirm `list_projects` does not return a project owned solely by user B; calling `list_spaces`/`list_jobs`/`get_job` with ids belonging to user B's private project returns empty/null rather than data.

- [ ] **Step 6: Final commit (docs/notes if any)**

```bash
git add -A
git commit -m "docs: record MCP PAT prototype end-to-end verification" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage:** PAT model/service (Tasks 1-2), dedicated-service storage = Option A (Task 2), bearer scheme + in-process MCP host at `/api/mcp` + Pat-required authorization (Task 4), read-only tools incl. job-type catalog from `UiField` metadata (Tasks 3, 5), new separate Personal overlay + entry point + PAT panel with one-time token display (Tasks 6-7), testing + security checks incl. permission scoping and "raw token never persisted" (Tasks 2, 8). All spec sections map to tasks.
- **Type consistency:** `PersonalAccessTokenService` signatures (`Generate(int,string,DateTime?)`, `Validate(string)->int?`, `ListForUser(int)`, `Revoke(int,int)`, `HashToken(string)`) are used identically across Tasks 2, 4, 7. DTO names are consistent across Tasks 3 and 5. `PatAuthenticationHandler.SchemeName == "Pat"` is reused in Task 4's policy.
- **Known adaptation points (flagged inline, not placeholders):** exact FluentUI grid/dialog API (Task 7) and the precise `FluentButton`/icon markup (Task 6) must match the installed FluentUI version; ReadOnly `FindSpace`/`FindJob` names are confirmed but re-checked at build (Task 5). These are real-codebase reconciliations, not deferred decisions.
