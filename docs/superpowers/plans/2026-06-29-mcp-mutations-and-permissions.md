# MCP Mutation Tools + Per-Tier PAT Permissions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the merged read-only MCP prototype into a full read/write surface (create/configure/connect/queue/abort/delete jobs, spaces, projects; list views/queues) gated by three independent per-tier PAT access levels.

**Architecture:** A PAT gains three `AccessLevel` fields (Project/Space/Job). The `Pat` auth handler stashes those levels in `HttpContext.Items`; a pure `PatAuthorization` helper (in `Refund.Mcp`, unit-testable) answers "does this token meet level L for tier T?". `RelayMcpTools` calls it before every tool, resolves targets through `DataManager`, and (for `configure_job`) translates a declarative `{name: value}` patch into a `DataManager.UpdateJob` lambda via reflection over `Job.TypeParameters`. DataManager is untouched.

**Tech Stack:** C# / .NET 10, ASP.NET Core, Blazor Server, FluentUI Blazor, `ModelContextProtocol.AspNetCore` 1.4.0, xUnit (`Refund.Tests`).

## Global Constraints

- **Permission enforcement lives only in the MCP tool layer.** Do not add authorization checks to `DataManager` or the Blazor UI.
- **`AccessLevel` ordering:** `None(0) < Read(1) < EditRun(2) < Manage(3)`. A check "requires level L for tier T" passes iff the token's level for T `>=` L.
- **Tool → tier·level map (verbatim):**
  - `list_projects` → Project·Read; `list_spaces`/`list_views` → Space·Read; `list_jobs`/`get_job` → Job·Read.
  - `list_job_types`/`list_queues` → authenticated only (no tier).
  - `create_project` → Project·EditRun; `delete_project` → Project·Manage.
  - `create_space` → Space·EditRun; `delete_space` → Space·Manage.
  - `create_job`/`configure_job`/`connect_jobs`/`disconnect_jobs`/`queue_job`/`abort_job` → Job·EditRun; `delete_job` → Job·Manage.
- **Read/list tools return empty on insufficient level or unseen id (no existence leak); mutation tools throw an error.**
- **No partial application in `configure_job`:** validate every name + coerce every value before any `SetValue`.
- **Migration:** a persisted PAT whose three levels are all `None` is upgraded in memory to all-`Manage` on load.
- **`Refund.Tests` references `Refund` only** (not `Relay`). Job-registry tests use `[Collection("JobRegistry")]` + `EnsurePopulated()`.
- **Local queue id is the literal `-1`** (`JobQueueType.Local`). There is no named constant.
- Commit message trailer on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Create:**
- `Refund/DataModel/AccessLevel.cs` — the `AccessLevel` enum (Task 1).
- `Refund/Mcp/PatAuthorization.cs` — `PatGrants` struct, `PermTier` enum, `Allows` (Task 4).
- `Refund/Mcp/RelayMcpParameterPatch.cs` — declarative-patch → assignment resolution + JSON coercion (Task 5).
- `Refund.Tests/Mcp/AccessLevelSerializationTests.cs` (Task 1).
- `Refund.Tests/Mcp/PatAuthorizationTests.cs` (Task 4).
- `Refund.Tests/Mcp/RelayMcpParameterPatchTests.cs` (Task 5).

**Modify:**
- `Refund/DataModel/PersonalAccessToken.cs` — add 3 level fields (Task 1).
- `Refund/Services/PersonalAccessTokenService.cs` — `Generate` takes levels; `Validate` returns the PAT; migration on load (Tasks 2, 3).
- `Refund/Mcp/McpDtos.cs` — add `QueueDto`, `ViewDto`, and mutation result DTOs (Task 6).
- `Refund/Mcp/RelayMcpProjections.cs` — add queue/view projections (Task 6).
- `Relay/Services/PatAuthenticationHandler.cs` — stash grants in `HttpContext.Items` (Task 7).
- `Relay/Services/RelayMcpTools.cs` — grants accessor, `list_queues`, `list_views`, all mutation tools (Tasks 7–10).
- `Relay/Screens/Overlay/Personal/AccessTokenManager.razor` + `.razor.cs` — level dropdowns + levels column (Task 11).

**Reference only (do not edit):** `DataManager.*.cs`, `Refund/DataModel/ReadOnly/*`, `Refund/DataModel/Job.cs`, `Relay/Program.cs` (already wires `WithTools<RelayMcpTools>`; no change needed).

---

### Task 1: `AccessLevel` enum + PAT level fields (+ serialization test)

**Files:**
- Create: `Refund/DataModel/AccessLevel.cs`
- Modify: `Refund/DataModel/PersonalAccessToken.cs`
- Test: `Refund.Tests/Mcp/AccessLevelSerializationTests.cs`

**Interfaces:**
- Produces: `enum AccessLevel { None=0, Read=1, EditRun=2, Manage=3 }`; `PersonalAccessToken.ProjectAccess/SpaceAccess/JobAccess : AccessLevel`.

**Context:** `RelayBase` serializes a non-nullable enum `[RelayProperty]` as its **string name** (write: `RelayBase.cs:134`; read: `RelayBase.cs:279` via `Enum.Parse`). So plain `AccessLevel` fields round-trip; do not make them nullable.

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/AccessLevelSerializationTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Refund.DataModel;
using Xunit;

namespace Refund.Tests.Mcp;

public class AccessLevelSerializationTests
{
    [Fact]
    public void PersonalAccessToken_RoundTripsAccessLevels()
    {
        var original = new PersonalAccessToken
        {
            Id = 7,
            TokenHash = "abc",
            Name = "t",
            OwnerUserId = 3,
            ProjectAccess = AccessLevel.Read,
            SpaceAccess = AccessLevel.EditRun,
            JobAccess = AccessLevel.Manage
        };

        var node = new JsonObject();
        original.WriteToJson(node);

        var restored = new PersonalAccessToken();
        restored.ReadFromJson(node);

        Assert.Equal(AccessLevel.Read, restored.ProjectAccess);
        Assert.Equal(AccessLevel.EditRun, restored.SpaceAccess);
        Assert.Equal(AccessLevel.Manage, restored.JobAccess);
    }

    [Fact]
    public void DefaultAccessLevels_AreNone()
    {
        var pat = new PersonalAccessToken();
        Assert.Equal(AccessLevel.None, pat.ProjectAccess);
        Assert.Equal(AccessLevel.None, pat.SpaceAccess);
        Assert.Equal(AccessLevel.None, pat.JobAccess);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter AccessLevelSerializationTests`
Expected: FAIL — compile error, `AccessLevel` and the three properties do not exist.

- [ ] **Step 3: Create the enum**

Create `Refund/DataModel/AccessLevel.cs`:

```csharp
namespace Refund.DataModel;

/// <summary>
/// Per-tier access a personal access token grants over MCP. Ordered: a check requiring
/// level L passes iff the token's level for that tier is >= L.
/// </summary>
public enum AccessLevel
{
    None = 0,
    Read = 1,
    EditRun = 2,
    Manage = 3
}
```

- [ ] **Step 4: Add the three fields to `PersonalAccessToken`**

In `Refund/DataModel/PersonalAccessToken.cs`, after the `ExpirationDate` property (line 26) and before `IsExpired`:

```csharp
    /// <summary>Access level for project-tier operations (list/create/delete projects).</summary>
    [RelayProperty] public AccessLevel ProjectAccess { get; set; } = AccessLevel.None;

    /// <summary>Access level for space-tier operations (list/create/delete spaces, list views).</summary>
    [RelayProperty] public AccessLevel SpaceAccess { get; set; } = AccessLevel.None;

    /// <summary>Access level for job-tier operations (list/get/create/configure/connect/queue/abort/delete jobs).</summary>
    [RelayProperty] public AccessLevel JobAccess { get; set; } = AccessLevel.None;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter AccessLevelSerializationTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add Refund/DataModel/AccessLevel.cs Refund/DataModel/PersonalAccessToken.cs Refund.Tests/Mcp/AccessLevelSerializationTests.cs
git commit -m "feat: add per-tier AccessLevel fields to PersonalAccessToken

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `PersonalAccessTokenService.Generate` accepts levels

**Files:**
- Modify: `Refund/Services/PersonalAccessTokenService.cs:57-81`
- Test: `Refund.Tests/Mcp/PersonalAccessTokenServiceLevelsTests.cs` (create)

**Interfaces:**
- Consumes: `AccessLevel` (Task 1).
- Produces: `Task<string> Generate(int ownerUserId, string name, AccessLevel projectAccess, AccessLevel spaceAccess, AccessLevel jobAccess, DateTime? expiry = null)`.

**Context:** existing `Generate(int, string, DateTime?)` is at lines 57-81 and is called from `AccessTokenManager` (Task 11 updates that caller). The service stores tokens keyed by hash and persists via `Save()`.

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/PersonalAccessTokenServiceLevelsTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Services;
using Xunit;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenServiceLevelsTests
{
    private static PersonalAccessTokenService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"pats-{Guid.NewGuid():N}.relay");
        var config = new RelayConfiguration { PatsPath = path };
        return new PersonalAccessTokenService(NullLogger<PersonalAccessTokenService>.Instance, config);
    }

    [Fact]
    public async Task Generate_PersistsLevels()
    {
        var svc = NewService(out var path);
        try
        {
            await svc.Generate(42, "agent", AccessLevel.Read, AccessLevel.EditRun, AccessLevel.Manage);
            var stored = svc.ListForUser(42);
            Assert.Single(stored);
            Assert.Equal(AccessLevel.Read, stored[0].ProjectAccess);
            Assert.Equal(AccessLevel.EditRun, stored[0].SpaceAccess);
            Assert.Equal(AccessLevel.Manage, stored[0].JobAccess);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter PersonalAccessTokenServiceLevelsTests`
Expected: FAIL — `Generate` has no 6-arg overload (compile error).

- [ ] **Step 3: Update `Generate`**

Replace the signature and the `PersonalAccessToken` initializer (lines 57-72) in `PersonalAccessTokenService.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter PersonalAccessTokenServiceLevelsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Refund/Services/PersonalAccessTokenService.cs Refund.Tests/Mcp/PersonalAccessTokenServiceLevelsTests.cs
git commit -m "feat: PersonalAccessTokenService.Generate accepts per-tier levels

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `Validate` returns the PAT; all-None → all-Manage migration

**Files:**
- Modify: `Refund/Services/PersonalAccessTokenService.cs:83-93` (`Validate`), `:113-132` (`Load`)
- Test: `Refund.Tests/Mcp/PersonalAccessTokenServiceValidateTests.cs` (create)

**Interfaces:**
- Consumes: `AccessLevel`, `PersonalAccessToken`.
- Produces: `PersonalAccessToken? Validate(string rawToken)` (was `int?`); migration applied in `Load()`.

**Context:** `Validate` currently returns `int?` (owner id). The auth handler (Task 7) needs the levels, so `Validate` now returns the whole record (or `null`). It still stamps `LastUsedDate`. The migration runs in `Load()` after each token is read: if all three levels are `None`, set all three to `Manage`.

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/PersonalAccessTokenServiceValidateTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Services;
using Xunit;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenServiceValidateTests
{
    private static PersonalAccessTokenService NewService(string path) =>
        new(NullLogger<PersonalAccessTokenService>.Instance, new RelayConfiguration { PatsPath = path });

    [Fact]
    public async Task Validate_ReturnsTokenWithLevels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pats-{Guid.NewGuid():N}.relay");
        try
        {
            var svc = NewService(path);
            var raw = await svc.Generate(9, "a", AccessLevel.None, AccessLevel.EditRun, AccessLevel.Read);
            var pat = svc.Validate(raw);
            Assert.NotNull(pat);
            Assert.Equal(9, pat!.OwnerUserId);
            Assert.Equal(AccessLevel.EditRun, pat.SpaceAccess);
            Assert.Null(svc.Validate("relay_pat_nope"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Load_MigratesAllNoneTokenToManage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pats-{Guid.NewGuid():N}.relay");
        try
        {
            // First service writes a legacy token whose levels are all None.
            var first = NewService(path);
            var raw = await first.Generate(5, "legacy", AccessLevel.None, AccessLevel.None, AccessLevel.None);

            // Second service loads it from disk and should migrate.
            var second = NewService(path);
            var pat = second.Validate(raw);
            Assert.NotNull(pat);
            Assert.Equal(AccessLevel.Manage, pat!.ProjectAccess);
            Assert.Equal(AccessLevel.Manage, pat.SpaceAccess);
            Assert.Equal(AccessLevel.Manage, pat.JobAccess);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter PersonalAccessTokenServiceValidateTests`
Expected: FAIL — `Validate` returns `int?`, so `pat!.OwnerUserId` / `.SpaceAccess` don't compile.

- [ ] **Step 3: Change `Validate` return type**

Replace `Validate` (lines 83-93) in `PersonalAccessTokenService.cs`:

```csharp
    public PersonalAccessToken? Validate(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        if (!_tokens.TryGetValue(HashToken(rawToken), out var pat)) return null;
        if (pat.IsExpired) return null;
        // Best-effort last-used stamp. Written without _lock: the DateTime? write is atomic on 64-bit
        // and _tokens enumeration in Save() is safe (ConcurrentDictionary); flushed by the cleanup loop.
        pat.LastUsedDate = DateTime.UtcNow;
        _dirty = true;
        return pat;
    }
```

- [ ] **Step 4: Add migration to `Load`**

In `Load()`, replace the loop body (lines 121-126) so each token is migrated before being stored:

```csharp
            foreach (var node in arr)
            {
                var pat = new PersonalAccessToken();
                pat.ReadFromJson(node);
                // Migrate legacy (pre-permissions) tokens: all-None means full access today.
                if (pat.ProjectAccess == AccessLevel.None
                    && pat.SpaceAccess == AccessLevel.None
                    && pat.JobAccess == AccessLevel.None)
                {
                    pat.ProjectAccess = AccessLevel.Manage;
                    pat.SpaceAccess = AccessLevel.Manage;
                    pat.JobAccess = AccessLevel.Manage;
                }
                _tokens[pat.TokenHash] = pat;
            }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter PersonalAccessTokenServiceValidateTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add Refund/Services/PersonalAccessTokenService.cs Refund.Tests/Mcp/PersonalAccessTokenServiceValidateTests.cs
git commit -m "feat: Validate returns PAT record; migrate all-None tokens to Manage

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `PatAuthorization` helper (pure)

**Files:**
- Create: `Refund/Mcp/PatAuthorization.cs`
- Test: `Refund.Tests/Mcp/PatAuthorizationTests.cs`

**Interfaces:**
- Consumes: `AccessLevel`.
- Produces:
  - `readonly record struct PatGrants(AccessLevel Project, AccessLevel Space, AccessLevel Job)`
  - `enum PermTier { Project, Space, Job }`
  - `static bool PatAuthorization.Allows(PatGrants grants, PermTier tier, AccessLevel required)`
  - `static PatGrants PatAuthorization.From(PersonalAccessToken pat)` (convenience used by Task 7)

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/PatAuthorizationTests.cs`:

```csharp
using Refund.DataModel;
using Refund.Mcp;
using Xunit;

namespace Refund.Tests.Mcp;

public class PatAuthorizationTests
{
    [Theory]
    [InlineData(AccessLevel.EditRun, AccessLevel.EditRun, true)]  // exact
    [InlineData(AccessLevel.Manage, AccessLevel.EditRun, true)]   // higher allows lower
    [InlineData(AccessLevel.Read, AccessLevel.EditRun, false)]    // lower denies
    [InlineData(AccessLevel.None, AccessLevel.Read, false)]
    [InlineData(AccessLevel.EditRun, AccessLevel.Manage, false)]  // delete needs Manage
    public void Allows_RespectsOrdering(AccessLevel held, AccessLevel required, bool expected)
    {
        var grants = new PatGrants(held, AccessLevel.None, AccessLevel.None);
        Assert.Equal(expected, PatAuthorization.Allows(grants, PermTier.Project, required));
    }

    [Fact]
    public void Allows_ChecksTheRequestedTierOnly()
    {
        var grants = new PatGrants(AccessLevel.None, AccessLevel.None, AccessLevel.Manage);
        Assert.True(PatAuthorization.Allows(grants, PermTier.Job, AccessLevel.Manage));
        Assert.False(PatAuthorization.Allows(grants, PermTier.Project, AccessLevel.Read));
        Assert.False(PatAuthorization.Allows(grants, PermTier.Space, AccessLevel.Read));
    }

    [Fact]
    public void From_MapsPatFields()
    {
        var pat = new PersonalAccessToken
        {
            ProjectAccess = AccessLevel.Read,
            SpaceAccess = AccessLevel.EditRun,
            JobAccess = AccessLevel.Manage
        };
        var grants = PatAuthorization.From(pat);
        Assert.Equal(AccessLevel.Read, grants.Project);
        Assert.Equal(AccessLevel.EditRun, grants.Space);
        Assert.Equal(AccessLevel.Manage, grants.Job);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter PatAuthorizationTests`
Expected: FAIL — `PatGrants`/`PermTier`/`PatAuthorization` do not exist.

- [ ] **Step 3: Implement the helper**

Create `Refund/Mcp/PatAuthorization.cs`:

```csharp
using Refund.DataModel;

namespace Refund.Mcp;

/// <summary>The three per-tier access levels a token carries, decoupled from HTTP/DataManager.</summary>
public readonly record struct PatGrants(AccessLevel Project, AccessLevel Space, AccessLevel Job);

public enum PermTier { Project, Space, Job }

/// <summary>
/// Pure permission checks for MCP tools. <see cref="Allows"/> returns true iff the token's level
/// for <paramref name="tier"/> is at least <paramref name="required"/>. AccessLevel is ordered.
/// </summary>
public static class PatAuthorization
{
    public static PatGrants From(PersonalAccessToken pat) =>
        new(pat.ProjectAccess, pat.SpaceAccess, pat.JobAccess);

    public static bool Allows(PatGrants grants, PermTier tier, AccessLevel required)
    {
        var held = tier switch
        {
            PermTier.Project => grants.Project,
            PermTier.Space => grants.Space,
            PermTier.Job => grants.Job,
            _ => AccessLevel.None
        };
        return held >= required;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter PatAuthorizationTests`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add Refund/Mcp/PatAuthorization.cs Refund.Tests/Mcp/PatAuthorizationTests.cs
git commit -m "feat: add pure PatAuthorization tier/level helper

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: `configure_job` patch translation (the keystone)

**Files:**
- Create: `Refund/Mcp/RelayMcpParameterPatch.cs`
- Test: `Refund.Tests/Mcp/RelayMcpParameterPatchTests.cs`

**Interfaces:**
- Consumes: `Job.TypeParameters` (`Dictionary<Type, HashSet<PropertyInfo>>`).
- Produces:
  - `static object? RelayMcpParameterPatch.CoerceJsonValue(JsonElement value, Type targetType)`
  - `static IReadOnlyList<(PropertyInfo Prop, object? Value)> RelayMcpParameterPatch.Resolve(Type jobType, IReadOnlyDictionary<string, JsonElement> patch)`
  - Both throw `ArgumentException` (message names the offending parameter / valid set) on unknown name or bad coercion. `Resolve` validates and coerces **all** entries before returning (no partial state).

**Context:** parameters are settable CLR properties registered in `Job.TypeParameters[jobType]` (a `HashSet<PropertyInfo>`). The tool layer (Task 9) wraps `Resolve` in `UpdateJob`: `foreach (var (p,v) in assignments) p.SetValue(job, v);`. Coercion must handle `int`, `decimal`, `bool`, `string`, enum-by-name, and `Nullable<T>` of those. Use the real `Class3D` type in tests (a concrete `Job`); its `NClasses` (int) parameter is a stable target. Confirm a decimal/enum parameter name with `Job.TypeParameters[typeof(Class3D)]` if `NClasses` is insufficient — the test below only relies on `NClasses` (int) plus an unknown-name case, which are type-agnostic.

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/RelayMcpParameterPatchTests.cs`:

```csharp
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Refund.DataModel;
using Refund.Mcp;
using Xunit;

namespace Refund.Tests.Mcp;

[Collection("JobRegistry")]
public class RelayMcpParameterPatchTests
{
    public RelayMcpParameterPatchTests(JobRegistryFixture fixture) => fixture.EnsurePopulated();

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Resolve_CoercesIntParameter()
    {
        var patch = new Dictionary<string, JsonElement> { ["NClasses"] = J("5") };
        var assignments = RelayMcpParameterPatch.Resolve(typeof(Class3D), patch);
        var (prop, value) = Assert.Single(assignments);
        Assert.Equal("NClasses", prop.Name);
        Assert.Equal(5, Assert.IsType<int>(value));
    }

    [Fact]
    public void Resolve_AppliesViaSetValue()
    {
        var job = new Class3D();
        var patch = new Dictionary<string, JsonElement> { ["NClasses"] = J("8") };
        foreach (var (p, v) in RelayMcpParameterPatch.Resolve(typeof(Class3D), patch))
            p.SetValue(job, v);
        Assert.Equal(8, job.NClasses);
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        var patch = new Dictionary<string, JsonElement> { ["NotAParam"] = J("1") };
        var ex = Assert.Throws<ArgumentException>(() => RelayMcpParameterPatch.Resolve(typeof(Class3D), patch));
        Assert.Contains("NotAParam", ex.Message);
    }

    [Fact]
    public void Resolve_IsAllOrNothing_OnBadEntry()
    {
        // One good, one unknown -> throws, and (by contract) returns nothing applied.
        var patch = new Dictionary<string, JsonElement>
        {
            ["NClasses"] = J("3"),
            ["Bogus"] = J("9")
        };
        Assert.Throws<ArgumentException>(() => RelayMcpParameterPatch.Resolve(typeof(Class3D), patch));
    }

    [Fact]
    public void CoerceJsonValue_HandlesCommonTypes()
    {
        Assert.Equal(4, RelayMcpParameterPatch.CoerceJsonValue(J("4"), typeof(int)));
        Assert.Equal(2.5m, RelayMcpParameterPatch.CoerceJsonValue(J("2.5"), typeof(decimal)));
        Assert.Equal(true, RelayMcpParameterPatch.CoerceJsonValue(J("true"), typeof(bool)));
        Assert.Equal("hi", RelayMcpParameterPatch.CoerceJsonValue(J("\"hi\""), typeof(string)));
        Assert.Equal(7, RelayMcpParameterPatch.CoerceJsonValue(J("7"), typeof(int?)));
        Assert.Null(RelayMcpParameterPatch.CoerceJsonValue(J("null"), typeof(int?)));
        Assert.Equal(AccessLevel.EditRun,
            RelayMcpParameterPatch.CoerceJsonValue(J("\"EditRun\""), typeof(AccessLevel)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter RelayMcpParameterPatchTests`
Expected: FAIL — `RelayMcpParameterPatch` does not exist.

- [ ] **Step 3: Implement the patch resolver**

Create `Refund/Mcp/RelayMcpParameterPatch.cs`:

```csharp
using System.Reflection;
using System.Text.Json;
using Refund.DataModel;

namespace Refund.Mcp;

/// <summary>
/// Translates a declarative { parameterName: jsonValue } patch into validated property assignments
/// against a concrete Job type. Validation and coercion happen for every entry before any value is
/// returned, so callers can apply the result atomically (no partial mutation on error).
/// </summary>
public static class RelayMcpParameterPatch
{
    public static IReadOnlyList<(PropertyInfo Prop, object? Value)> Resolve(
        Type jobType, IReadOnlyDictionary<string, JsonElement> patch)
    {
        if (!Job.TypeParameters.TryGetValue(jobType, out var props))
            throw new ArgumentException($"Type '{jobType.Name}' has no settable parameters.");

        var byName = props.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
        var result = new List<(PropertyInfo, object?)>(patch.Count);

        foreach (var (name, raw) in patch)
        {
            if (!byName.TryGetValue(name, out var prop))
                throw new ArgumentException(
                    $"Unknown parameter '{name}' for {jobType.Name}. Valid parameters: {string.Join(", ", byName.Keys.OrderBy(k => k))}.");

            object? value;
            try { value = CoerceJsonValue(raw, prop.PropertyType); }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    $"Cannot set '{name}' ({prop.PropertyType.Name}): {ex.Message}");
            }
            result.Add((prop, value));
        }

        return result;
    }

    public static object? CoerceJsonValue(JsonElement value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            return CoerceJsonValue(value, underlying);
        }

        if (targetType.IsEnum)
            return Enum.Parse(targetType, value.GetString()
                ?? throw new ArgumentException("expected an enum name string"), ignoreCase: true);

        if (targetType == typeof(string)) return value.GetString();
        if (targetType == typeof(bool)) return value.GetBoolean();
        if (targetType == typeof(int)) return value.GetInt32();
        if (targetType == typeof(long)) return value.GetInt64();
        if (targetType == typeof(float)) return value.GetSingle();
        if (targetType == typeof(double)) return value.GetDouble();
        if (targetType == typeof(decimal)) return value.GetDecimal();

        throw new ArgumentException($"unsupported parameter type {targetType.Name}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter RelayMcpParameterPatchTests`
Expected: PASS (5 tests). If `Class3D.NClasses` is not an `int` parameter in this build, adjust the test's parameter name to any `int` member of `Job.TypeParameters[typeof(Class3D)]` (inspect via a scratch test) — do not change the production code.

- [ ] **Step 5: Commit**

```bash
git add Refund/Mcp/RelayMcpParameterPatch.cs Refund.Tests/Mcp/RelayMcpParameterPatchTests.cs
git commit -m "feat: declarative job-parameter patch resolver with JSON coercion

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: New read DTOs + projections (`QueueDto`, `ViewDto`) and mutation result DTOs

**Files:**
- Modify: `Refund/Mcp/McpDtos.cs`
- Modify: `Refund/Mcp/RelayMcpProjections.cs`
- Test: `Refund.Tests/Mcp/RelayMcpProjectionsQueueViewTests.cs` (create)

**Interfaces:**
- Produces:
  - `record QueueDto(int Id, string Alias, string Type)` — `Type` is `"local"` or `"cluster"`.
  - `record ViewDto(int Id, string Alias)`
  - `record CreatedDto(int Id, string Alias)` — generic create result for project/space/job.
  - `record OkDto(bool Ok)` — result for delete/queue/abort/connect/configure.
  - `static QueueDto RelayMcpProjections.ToDto(ReadOnlyJobQueue q)`
  - `static ViewDto RelayMcpProjections.ToDto(ReadOnlyView v)`

**Context:** `ReadOnlyJobQueue` has `Id`, `Alias`, and `QueueType` (`[Flags] JobQueueType`; local queue has the `Local` flag and `Id == -1`). `ReadOnlyView` has `Id`, `Alias`. Existing `ToDto` overloads for project/space/job already exist; add two more (overloading is fine — they take distinct read-only types).

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/Mcp/RelayMcpProjectionsQueueViewTests.cs`:

```csharp
using Refund.DataModel;
using Refund.Mcp;
using Xunit;

namespace Refund.Tests.Mcp;

public class RelayMcpProjectionsQueueViewTests
{
    [Fact]
    public void ToDto_LocalQueue_TypeIsLocal()
    {
        var local = new JobQueue { Id = -1, Alias = "Local", QueueType = JobQueueType.Local };
        var dto = RelayMcpProjections.ToDto(local.AsReadOnly());
        Assert.Equal(-1, dto.Id);
        Assert.Equal("local", dto.Type);
    }

    [Fact]
    public void ToDto_GpuQueue_TypeIsCluster()
    {
        var cluster = new JobQueue { Id = 3, Alias = "gpu-a100", QueueType = JobQueueType.GPU };
        var dto = RelayMcpProjections.ToDto(cluster.AsReadOnly());
        Assert.Equal(3, dto.Id);
        Assert.Equal("cluster", dto.Type);
    }
}
```

> Note: confirm `JobQueue` is constructible with `Id`/`Alias`/`QueueType` settable and has `AsReadOnly()`. If the public surface differs, build the `ReadOnlyJobQueue` the same way other tests in `Refund.Tests` build read-only objects; keep the two assertions identical.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter RelayMcpProjectionsQueueViewTests`
Expected: FAIL — `QueueDto` / `ToDto(ReadOnlyJobQueue)` missing.

- [ ] **Step 3: Add DTOs**

Append to `Refund/Mcp/McpDtos.cs`:

```csharp
/// <summary>A job queue the agent may target with queue_job. Type is "local" or "cluster".</summary>
public record QueueDto(int Id, string Alias, string Type);

/// <summary>A view within a space; create_job targets a view by id.</summary>
public record ViewDto(int Id, string Alias);

/// <summary>Result of a create_* tool.</summary>
public record CreatedDto(int Id, string Alias);

/// <summary>Generic success result for mutating tools without a created entity.</summary>
public record OkDto(bool Ok);
```

- [ ] **Step 4: Add projections**

Append to the `RelayMcpProjections` class in `Refund/Mcp/RelayMcpProjections.cs` (before the closing brace):

```csharp
    public static QueueDto ToDto(ReadOnlyJobQueue q) =>
        new(q.Id, q.Alias, q.QueueType.HasFlag(JobQueueType.Local) ? "local" : "cluster");

    public static ViewDto ToDto(ReadOnlyView v) => new(v.Id, v.Alias);
```

Add `using Refund.DataModel.ReadOnly;` is already present; `JobQueueType` lives in `Refund.DataModel` which is also already imported.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter RelayMcpProjectionsQueueViewTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add Refund/Mcp/McpDtos.cs Refund/Mcp/RelayMcpProjections.cs Refund.Tests/Mcp/RelayMcpProjectionsQueueViewTests.cs
git commit -m "feat: add queue/view/result DTOs and projections

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Auth handler carries grants; tools read them + enforce read tiers + `list_queues`/`list_views`

**Files:**
- Modify: `Relay/Services/PatAuthenticationHandler.cs`
- Modify: `Relay/Services/RelayMcpTools.cs`

**Interfaces:**
- Consumes: `PersonalAccessTokenService.Validate` (returns `PersonalAccessToken?`, Task 3), `PatAuthorization`/`PatGrants`/`PermTier` (Task 4), `QueueDto`/`ViewDto` projections (Task 6).
- Produces (within `RelayMcpTools`):
  - `private PatGrants Grants()` — reads `HttpContext.Items["PatGrants"]`.
  - `private void Require(PermTier tier, AccessLevel level)` — throws `McpException` if not allowed.
  - read tools now gated; new `ListQueues()`, `ListViews(int projectId, int spaceId)`.

**Context:** the handler currently calls `_pats.Validate(raw)` expecting `int?`. It now gets a `PersonalAccessToken?`; use `pat.OwnerUserId` for the existing user lookup and additionally stash `PatAuthorization.From(pat)` in `Context.Items`. Tools resolve grants from `IHttpContextAccessor`. `McpException` is `ModelContextProtocol.McpException`.

- [ ] **Step 1: Update the auth handler**

In `Relay/Services/PatAuthenticationHandler.cs`, replace the validate/owner block (currently lines 42-48):

```csharp
        var raw = header["Bearer ".Length..].Trim();
        var pat = _pats.Validate(raw);
        if (pat == null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired personal access token"));

        var user = _dataManager.FindUser(pat.OwnerUserId);
        if (user == null)
            return Task.FromResult(AuthenticateResult.Fail("Token owner no longer exists"));

        Context.Items["PatGrants"] = Refund.Mcp.PatAuthorization.From(pat);
```

(Keep the rest — the claims/ticket construction below it — unchanged.)

- [ ] **Step 2: Add grants accessor + Require + gate read tools in `RelayMcpTools`**

In `Relay/Services/RelayMcpTools.cs`, add `using Refund.DataModel;` and `using ModelContextProtocol;` at the top, then add these members to the class (after `CurrentUser()`):

```csharp
    private Refund.Mcp.PatGrants Grants() =>
        contextAccessor.HttpContext?.Items["PatGrants"] is Refund.Mcp.PatGrants g
            ? g
            : new Refund.Mcp.PatGrants(AccessLevel.None, AccessLevel.None, AccessLevel.None);

    private void Require(Refund.Mcp.PermTier tier, AccessLevel level)
    {
        if (!Refund.Mcp.PatAuthorization.Allows(Grants(), tier, level))
            throw new McpException($"This token lacks {level} access for {tier} operations.");
    }
```

Then gate the existing read tools. In `ListProjects()` add at the top `if (!Refund.Mcp.PatAuthorization.Allows(Grants(), Refund.Mcp.PermTier.Project, AccessLevel.Read)) return [];`. In `ListSpaces` add the same with `PermTier.Space`. In `ListJobs` and `GetJob` add the same with `PermTier.Job` (for `GetJob`, `return null;` instead of `[]`). Leave `ListJobTypes` unchanged (auth-only).

- [ ] **Step 3: Add `list_queues` and `list_views` tools**

Add to `RelayMcpTools` (after `ListJobTypes`):

```csharp
    [McpServerTool(Name = "list_queues"), Description("List job queues available for queue_job (local and cluster).")]
    public IReadOnlyList<QueueDto> ListQueues()
    {
        _ = CurrentUser(); // require authentication
        var result = new List<QueueDto> { RelayMcpProjections.ToDto(dataManager.LocalQueue) };
        result.AddRange(dataManager.ClusterQueues.Select(RelayMcpProjections.ToDto));
        return result;
    }

    [McpServerTool(Name = "list_views"), Description("List the views in a space; create_job targets a view by id.")]
    public IReadOnlyList<ViewDto> ListViews(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        if (!Refund.Mcp.PatAuthorization.Allows(Grants(), Refund.Mcp.PermTier.Space, AccessLevel.Read)) return [];
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) return [];
        return space.Views.Select(RelayMcpProjections.ToDto).ToList();
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: SUCCESS. (If `McpException` is not found, confirm its namespace is `ModelContextProtocol` for the pinned 1.4.0 package.)

- [ ] **Step 5: Commit**

```bash
git add Relay/Services/PatAuthenticationHandler.cs Relay/Services/RelayMcpTools.cs
git commit -m "feat: carry PAT grants into requests; gate read tools; add list_queues/list_views

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Project & space mutation tools

**Files:**
- Modify: `Relay/Services/RelayMcpTools.cs`

**Interfaces:**
- Consumes: `Require` (Task 7), `CreatedDto`/`OkDto` (Task 6), DataManager `CreateProject`/`DeleteProject`/`CreateSpace`/`DeleteSpace`.
- Produces: tools `create_project`, `delete_project`, `create_space`, `delete_space`.

**Context:** `CreateProject(ReadOnlyUser, Project template=null)` returns `ReadOnlyProject`; the new project's owner is `user`. `DeleteProject(ReadOnlyProject)` takes no user. `CreateSpace(ReadOnlyUser, ReadOnlyProject, Space template=null)` returns `ReadOnlySpace`. `DeleteSpace(ReadOnlyUser, ReadOnlySpace)`. To set an alias on creation, set it via the existing update path is out of scope — pass a `template` with `Alias` set. `Project`/`Space` are mutable model types in `Refund.DataModel`.

- [ ] **Step 1: Add the four tools**

Add to `RelayMcpTools` (after `ListViews`):

```csharp
    [McpServerTool(Name = "create_project"), Description("Create a new project owned by the current user.")]
    public async Task<CreatedDto> CreateProject(
        [Description("Optional project name/alias.")] string? alias = null)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Project, AccessLevel.EditRun);
        var template = string.IsNullOrWhiteSpace(alias) ? null : new Project { Alias = alias };
        var project = await dataManager.CreateProject(user, template);
        return new CreatedDto(project.Id, project.Alias);
    }

    [McpServerTool(Name = "delete_project"), Description("Delete a project and everything in it.")]
    public async Task<OkDto> DeleteProject(
        [Description("The project id.")] int projectId)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Project, AccessLevel.Manage);
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) throw new McpException($"Project {projectId} not found.");
        await dataManager.DeleteProject(project);
        return new OkDto(true);
    }

    [McpServerTool(Name = "create_space"), Description("Create a new space in a project.")]
    public async Task<CreatedDto> CreateSpace(
        [Description("The project id.")] int projectId,
        [Description("Optional space name/alias.")] string? alias = null)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Space, AccessLevel.EditRun);
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) throw new McpException($"Project {projectId} not found.");
        var template = string.IsNullOrWhiteSpace(alias) ? null : new Space { Alias = alias };
        var space = await dataManager.CreateSpace(user, project, template);
        return new CreatedDto(space.Id, space.Alias);
    }

    [McpServerTool(Name = "delete_space"), Description("Delete a space and everything in it.")]
    public async Task<OkDto> DeleteSpace(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Space, AccessLevel.Manage);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) throw new McpException($"Space {spaceId} not found.");
        await dataManager.DeleteSpace(user, space);
        return new OkDto(true);
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: SUCCESS. (If `Project`/`Space` have no public settable `Alias` or no parameterless ctor, drop the `template` and pass `null`, then note that alias-on-create is unsupported — do not invent an API.)

- [ ] **Step 3: Commit**

```bash
git add Relay/Services/RelayMcpTools.cs
git commit -m "feat: add project/space create+delete MCP tools

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Job lifecycle tools — create, configure, delete, abort

**Files:**
- Modify: `Relay/Services/RelayMcpTools.cs`

**Interfaces:**
- Consumes: `Require`, `RelayMcpParameterPatch.Resolve` (Task 5), DataManager `CreateJob`/`UpdateJob`/`DeleteJob`/`AbortJob`, `ReadOnlySpace.FindView`/`FindJob`, `ReadOnlyJob.GetOriginalType()`.
- Produces: tools `create_job`, `configure_job`, `delete_job`, `abort_job`.

**Context:** `CreateJob(ReadOnlyUser, ReadOnlyView, string typeGuid, Job template=null, ReadOnlyFolder targetFolder=null)`. Get the view via `project.FindSpace(spaceId).FindView(viewId)`. `configure_job` takes `Dictionary<string, JsonElement>`; resolve assignments first (throws on bad input → becomes an `McpException` via the catch), then apply inside `UpdateJob`. Use `job.GetOriginalType()` for the patch's `jobType`.

- [ ] **Step 1: Add `create_job`**

Add to `RelayMcpTools`. Add `using System.Text.Json;` at the top of the file.

```csharp
    [McpServerTool(Name = "create_job"), Description("Create a job of the given type in a space's view.")]
    public async Task<CreatedDto> CreateJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The view id (from list_views).")] int viewId,
        [Description("The job type guid (from list_job_types).")] string typeGuid)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        var view = space?.FindView(viewId);
        if (view == null) throw new McpException($"View {viewId} not found in space {spaceId}.");
        var job = await dataManager.CreateJob(user, view, typeGuid);
        return new CreatedDto(job.Id, job.AliasOrId);
    }
```

- [ ] **Step 2: Add `configure_job`**

```csharp
    [McpServerTool(Name = "configure_job"), Description("Set one or more parameter values on a job (see list_job_types for names).")]
    public async Task<OkDto> ConfigureJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Map of parameter name to value.")] Dictionary<string, JsonElement> parameters)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.EditRun);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");

        IReadOnlyList<(System.Reflection.PropertyInfo Prop, object? Value)> assignments;
        try { assignments = Refund.Mcp.RelayMcpParameterPatch.Resolve(job.GetOriginalType(), parameters); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }

        await dataManager.UpdateJob(user, job, j =>
        {
            foreach (var (prop, value) in assignments) prop.SetValue(j, value);
        });
        return new OkDto(true);
    }
```

- [ ] **Step 3: Add `abort_job` and `delete_job`**

```csharp
    [McpServerTool(Name = "abort_job"), Description("Abort a running or queued job.")]
    public async Task<OkDto> AbortJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.EditRun);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");
        await dataManager.AbortJob(user, job);
        return new OkDto(true);
    }

    [McpServerTool(Name = "delete_job"), Description("Delete a job.")]
    public async Task<OkDto> DeleteJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.Manage);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");
        await dataManager.DeleteJob(user, job);
        return new OkDto(true);
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: SUCCESS.

- [ ] **Step 5: Commit**

```bash
git add Relay/Services/RelayMcpTools.cs
git commit -m "feat: add job create/configure/abort/delete MCP tools

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Edge + queue tools — connect, disconnect, queue

**Files:**
- Modify: `Relay/Services/RelayMcpTools.cs`

**Interfaces:**
- Consumes: `Require`, DataManager `CreateEdge`/`DeleteEdge`/`QueueLocalJob`/`QueueClusterJob`/`FindClusterQueue`, `ReadOnlyJob.PortsOut`/`PortsIn`, `ReadOnlySpace.Edges`.
- Produces: tools `connect_jobs`, `disconnect_jobs`, `queue_job`.

**Context:** `CreateEdge(ReadOnlySpace space, ReadOnlyPort from, ReadOnlyPort to)` — `from` is the source job's `PortsOut[fromPort]`, `to` is the target job's `PortsIn[toPort]`. `DeleteEdge(ReadOnlyEdge)`. For disconnect, find the edge in `space.Edges` where `e.Source.Job.Id == fromJobId && e.Source.Name == fromPort && e.Target.Job.Id == toJobId && e.Target.Name == toPort`. `QueueLocalJob(user, job)` when no queue id or id `-1`; else `QueueClusterJob(user, job, FindClusterQueue(queueId))`.

- [ ] **Step 1: Add `connect_jobs` and `disconnect_jobs`**

```csharp
    [McpServerTool(Name = "connect_jobs"), Description("Connect an output port of one job to an input port of another.")]
    public async Task<OkDto> ConnectJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("Source (upstream) job id.")] int fromJobId,
        [Description("Source output port name.")] string fromPort,
        [Description("Target (downstream) job id.")] int toJobId,
        [Description("Target input port name.")] string toPort)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        var fromJob = space?.FindJob(fromJobId);
        var toJob = space?.FindJob(toJobId);
        if (space == null || fromJob == null || toJob == null) throw new McpException("Space or job not found.");
        if (!fromJob.PortsOut.TryGetValue(fromPort, out var outPort)) throw new McpException($"Output port '{fromPort}' not found on job {fromJobId}.");
        if (!toJob.PortsIn.TryGetValue(toPort, out var inPort)) throw new McpException($"Input port '{toPort}' not found on job {toJobId}.");
        await dataManager.CreateEdge(space, outPort, inPort);
        return new OkDto(true);
    }

    [McpServerTool(Name = "disconnect_jobs"), Description("Remove the edge between two job ports.")]
    public async Task<OkDto> DisconnectJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("Source (upstream) job id.")] int fromJobId,
        [Description("Source output port name.")] string fromPort,
        [Description("Target (downstream) job id.")] int toJobId,
        [Description("Target input port name.")] string toPort)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) throw new McpException($"Space {spaceId} not found.");
        var edge = space.Edges.FirstOrDefault(e =>
            e.Source.Job.Id == fromJobId && e.Source.Name == fromPort &&
            e.Target.Job.Id == toJobId && e.Target.Name == toPort);
        if (edge == null) throw new McpException("No such edge.");
        await dataManager.DeleteEdge(edge);
        return new OkDto(true);
    }
```

- [ ] **Step 2: Add `queue_job`**

```csharp
    [McpServerTool(Name = "queue_job"), Description("Queue a job to run. Omit queueId for the local queue; pass a cluster queue id from list_queues.")]
    public async Task<OkDto> QueueJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Optional cluster queue id (from list_queues). Omit or -1 for local.")] int? queueId = null)
    {
        var user = CurrentUser();
        Require(Refund.Mcp.PermTier.Job, AccessLevel.EditRun);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");

        if (queueId is null or -1)
        {
            await dataManager.QueueLocalJob(user, job);
        }
        else
        {
            var queue = dataManager.FindClusterQueue(queueId.Value);
            if (queue == null) throw new McpException($"Cluster queue {queueId} not found.");
            await dataManager.QueueClusterJob(user, job, queue);
        }
        return new OkDto(true);
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add Relay/Services/RelayMcpTools.cs
git commit -m "feat: add connect/disconnect/queue MCP tools

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: Token-management UI — level dropdowns + levels column

**Files:**
- Modify: `Relay/Screens/Overlay/Personal/AccessTokenManager.razor`
- Modify: `Relay/Screens/Overlay/Personal/AccessTokenManager.razor.cs`

**Interfaces:**
- Consumes: `PersonalAccessTokenService.Generate(int, string, AccessLevel, AccessLevel, AccessLevel, DateTime?)` (Task 2), `AccessLevel`.

**Context:** the create form currently collects only a name and calls `Generate(Session.User.Id, _newName.Trim())`. Add three `FluentSelect<AccessLevel>` dropdowns and reject an all-`None` selection. The token list gets a compact levels column. The `using Refund.DataModel;` is already present in the `.razor`.

- [ ] **Step 1: Add level state + updated create logic (`.razor.cs`)**

In `AccessTokenManager.razor.cs`, add fields after `_newName` (line 19):

```csharp
    private AccessLevel _newProjectAccess = AccessLevel.Read;
    private AccessLevel _newSpaceAccess = AccessLevel.EditRun;
    private AccessLevel _newJobAccess = AccessLevel.EditRun;
```

In `OpenCreate()`, reset them (after `_createdRawToken = null;`):

```csharp
        _newProjectAccess = AccessLevel.Read;
        _newSpaceAccess = AccessLevel.EditRun;
        _newJobAccess = AccessLevel.EditRun;
```

Replace `CreateToken()` body's validation + generate call:

```csharp
    private async Task CreateToken()
    {
        if (string.IsNullOrWhiteSpace(_newName))
        {
            ToastService.ShowError("Please enter a name for the token.");
            return;
        }
        if (_newProjectAccess == AccessLevel.None
            && _newSpaceAccess == AccessLevel.None
            && _newJobAccess == AccessLevel.None)
        {
            ToastService.ShowError("Grant at least one access level, or the token can do nothing.");
            return;
        }
        try
        {
            _createdRawToken = await Pats.Generate(
                Session.User.Id, _newName.Trim(),
                _newProjectAccess, _newSpaceAccess, _newJobAccess);
            Refresh();
        }
        catch (Exception exc)
        {
            ToastService.ShowError("Couldn't create token: " + exc.Message);
        }
    }
```

Add a formatter for the list column (after `FormatCreated`):

```csharp
    private static string FormatLevels(PersonalAccessToken t) =>
        $"P:{Abbrev(t.ProjectAccess)} S:{Abbrev(t.SpaceAccess)} J:{Abbrev(t.JobAccess)}";

    private static string Abbrev(AccessLevel l) => l switch
    {
        AccessLevel.Read => "R",
        AccessLevel.EditRun => "E",
        AccessLevel.Manage => "M",
        _ => "–"
    };
```

- [ ] **Step 2: Add dropdowns + column (`.razor`)**

In `AccessTokenManager.razor`, inside the create form, after the name `FluentTextField` (line 22) and before the buttons div:

```razor
                <div style="display:flex; gap:12px; flex-wrap:wrap;">
                    <FluentSelect Label="Projects" @bind-SelectedOption="_newProjectAccess"
                                  TOption="AccessLevel" Items="_levels" OptionText="@(l => l.ToString())" />
                    <FluentSelect Label="Spaces" @bind-SelectedOption="_newSpaceAccess"
                                  TOption="AccessLevel" Items="_levels" OptionText="@(l => l.ToString())" />
                    <FluentSelect Label="Jobs" @bind-SelectedOption="_newJobAccess"
                                  TOption="AccessLevel" Items="_levels" OptionText="@(l => l.ToString())" />
                </div>
```

Update the intro copy (line 12-14) to drop "read-only in this release": replace `as you. Tokens act with your permissions and are read-only in this release.` with `as you, limited to the access levels you grant below.`.

Add the levels source to the `.razor.cs` (after the three `_newXAccess` fields):

```csharp
    private static readonly AccessLevel[] _levels =
        { AccessLevel.None, AccessLevel.Read, AccessLevel.EditRun, AccessLevel.Manage };
```

Add a column to the `FluentDataGrid`, after the "Name" `PropertyColumn`:

```razor
            <TemplateColumn Title="Access">@FormatLevels(context)</TemplateColumn>
```

- [ ] **Step 3: Build**

Run: `dotnet build Relay/Relay.csproj`
Expected: SUCCESS. (If `FluentSelect`'s `@bind-SelectedOption`/`OptionText` API differs in FluentUI 4.14.0, use the project's existing `FluentSelect` usage as the reference pattern — grep `FluentSelect` under `Relay/` — and keep the three-dropdown + all-None-guard behavior.)

- [ ] **Step 4: Manual verification**

Run the app, open Personal settings → Access tokens → New token. Confirm: three dropdowns appear; creating with all three = None shows the error toast; creating with valid levels reveals the raw token once; the list shows an "Access" column like `P:R S:E J:E`.

- [ ] **Step 5: Commit**

```bash
git add Relay/Screens/Overlay/Personal/AccessTokenManager.razor Relay/Screens/Overlay/Personal/AccessTokenManager.razor.cs
git commit -m "feat: per-tier level dropdowns and access column in token UI

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: Full build + test sweep, spec future-work update, manual E2E

**Files:**
- Modify: `docs/superpowers/specs/2026-06-29-mcp-mutations-and-permissions-design.md` (tick delivered items if desired)

- [ ] **Step 1: Full unit test run**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj`
Expected: PASS — all prior tests plus the new `Mcp/*` tests (AccessLevel, service levels, validate/migration, PatAuthorization, parameter patch, queue/view projections).

- [ ] **Step 2: Full app build**

Run: `dotnet build Relay/Relay.csproj`
Expected: SUCCESS, no warnings introduced by these files.

- [ ] **Step 3: Manual E2E with MCP Inspector (non-admin account)**

Using a **non-admin** user (admins see all projects, masking scoping), mint three PATs:
1. `Project=Read, Space=Read, Job=Read` — confirm read tools work, every mutation returns a permission error.
2. `Project=Read, Space=EditRun, Job=Manage` — confirm `create_space`, `create_job`, `configure_job`, `connect_jobs`, `queue_job`, `delete_job` succeed; `delete_space` and `delete_project` are denied.
3. `Project=Manage, Space=Manage, Job=Manage` — confirm full lifecycle incl. `create_project`/`delete_project`.

Verify `configure_job` with an unknown parameter returns an error naming valid parameters, and a good patch changes the value (re-read via `get_job`).

- [ ] **Step 4: Finish the branch**

Announce and use **superpowers:finishing-a-development-branch** to verify tests, present options, and complete the work.

---

## Self-Review

**1. Spec coverage:**
- `AccessLevel` + three PAT fields → Task 1. ✓
- Service `Generate` levels + `Validate` returns record + migration → Tasks 2, 3. ✓
- Auth handler carries grants → Task 7. ✓
- `PatAuthorization` pure helper → Task 4. ✓
- `configure_job` patch translation → Task 5 (logic) + Task 9 (tool). ✓
- All read tools gated; `list_queues`, `list_views` → Tasks 6, 7. ✓
- Mutation tools (project/space/job/edge/queue) → Tasks 8, 9, 10. ✓
- Tool→tier·level map → enforced per tool, matches Global Constraints. ✓
- UI: three dropdowns, all-None guard, levels column → Task 11. ✓
- Testing (serialization, authorization matrix, patch translation, migration, queue/view projection) → Tasks 1–6, 12. ✓
- Member management excluded; OAuth/resources excluded → not present. ✓

**2. Placeholder scan:** No TBD/TODO. The few "if the API differs, use the project's existing pattern" notes are guarded fallbacks with a concrete default already written, not placeholders — they exist only where a third-party (FluentUI) or model ctor surface can't be 100% confirmed without the compiler.

**3. Type consistency:** `Validate` → `PersonalAccessToken?` (Task 3) consumed by handler (Task 7). `Generate` 6-arg (Task 2) consumed by UI (Task 11). `PatGrants`/`PermTier`/`Allows`/`From` (Task 4) consumed by handler + tools (Tasks 7–10). `Resolve`/`CoerceJsonValue` (Task 5) consumed by `configure_job` (Task 9). DTOs `QueueDto`/`ViewDto`/`CreatedDto`/`OkDto` (Task 6) consumed by Tasks 7–10. All names match.
