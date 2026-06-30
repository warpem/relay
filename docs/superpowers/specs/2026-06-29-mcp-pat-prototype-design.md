# MCP Read-Only Prototype with Personal Access Tokens — Design

**Date:** 2026-06-29
**Status:** Approved (design), pending implementation plan
**Branch:** `feat/mcp-pat-prototype`

## Goal

Make Relay reachable by an LLM agent (e.g. Claude) over the Model Context
Protocol (MCP), authenticated with Personal Access Tokens (PATs) that users
mint from a new personal-settings surface. This first prototype is **read-only**:
it proves the auth path, the in-process MCP host, and the existing `ReadOnly*`
serialization end to end, with **no mutation** of Relay data.

### Success criterion

An agent configured with a user's PAT can connect to `/api/mcp` and:

- list the projects that user can see,
- list spaces in a project and jobs in a space (with status),
- read a job's details, and
- read the catalog of available job types.

Everything the agent sees is scoped to the PAT owner's existing permissions.

### Explicit non-goals (this prototype)

- No mutation tools (create/configure/queue/delete). Deferred to a follow-up.
- No OAuth 2.1 resource-server flow. PATs only. (OAuth is the planned v2.)
- No scopes/capability subsetting. A PAT acts as the full (read-only) user.
- No general personal-settings surface beyond PAT management.
- No factory tooling.

## Background / current state

- **Stack:** ASP.NET Core + Blazor Server, Autofac DI, FluentUI Blazor.
- **Auth today:** a single cookie scheme (`CookieAuthenticationDefaults`).
  Both login paths (local password via `/process-login`, SSO via OAuth2
  code+PKCE in `AuthenticationService`) converge on
  `httpContext.SignInAsync(...)` with a `ClaimsIdentity` carrying
  `ClaimTypes.Name = username`. The single user-binding boundary across the
  app is `DataManager.FindUser(username)`.
- **Program.cs middleware** redirects unauthenticated requests to `/login` but
  **exempts `/api`** — so an MCP endpoint under `/api/...` is not cookie-gated.
- **`DataManager`** is an in-process singleton with autosave + queue daemons.
  A second `DataManager` in another process would corrupt the data store, so
  the MCP host must run **in-process** and share the singleton.
- **`SecurityTokenService`** is an existing standalone, file-backed token store
  (singleton + `IHostedService`, `ConcurrentDictionary`, periodic cleanup,
  `RelayBase`-serialized records). It is the template for the PAT service.
- **`Job.PopulateStatic()`** populates the job-type registry (`Job.Types`),
  the source for the job-type catalog tool.
- **UI fields** (`/Refund/UIFields/`) are attribute-based parameter metadata
  (`UiDecimal`, `UiEnum`, `CliName`, `Label`, `HelpText`, ...) attached to job
  properties — the source for per-type parameter schema in the catalog.
- **Overlays:** `OverlayScreenType { None, Queues, Settings }` in `RelaySession`;
  `OverlaySettings` is a `FluentTabs` overlay with admin-flavored tabs (Users,
  Queue configuration). There is no personal-settings surface.

## Architecture

### Storage decision

PAT records live in a **dedicated `PersonalAccessTokenService`** (Option A),
mirroring `SecurityTokenService`. Rationale: per-request validation needs O(1)
lookup keyed by token hash; it keeps the `User` model and its
`[RelayProperty(Order=N)]` sequence untouched; and it follows an established
pattern. (Rejected: a `List<PersonalAccessToken>` on `User`, which forces a
scan or a side index on the hot path and grows the serialized user.)

### Components

#### 1. `PersonalAccessToken : RelayBase`

Fields (all `[RelayProperty]`): `Id`, `TokenHash`, `Name` (user label),
`OwnerUserId`, `CreationDate`, `LastUsedDate?`, `ExpirationDate?`.

#### 2. `PersonalAccessTokenService` (singleton + `IHostedService`)

- File-backed (new path on `RelayConfiguration`, e.g. `PatsPath`),
  `ConcurrentDictionary<string /*hash*/, PersonalAccessToken>`, `SemaphoreSlim`
  for writes, `PeriodicTimer` cleanup of expired records — same shape as
  `SecurityTokenService`.
- `Task<string> Generate(ReadOnlyUser owner, string name, DateTime? expiry)`:
  creates a raw token `relay_pat_<base64url(32 random bytes)>`, stores only its
  **SHA-256 hash**, returns the **raw token once**. Deterministic SHA-256 (not
  PBKDF2) so the dictionary is keyed by hash for O(1) lookup; safe because the
  token is 256-bit high-entropy (PBKDF2 exists to slow brute force on
  low-entropy passwords).
- `int? Validate(string rawToken)`: hash, dictionary lookup, reject if expired,
  stamp `LastUsedDate`, return `OwnerUserId` (or null).
- `IReadOnlyList<PersonalAccessToken> ListForUser(int userId)`.
- `Task Revoke(int userId, int tokenId)` (ownership-checked).

Raw tokens are never persisted; only hashes are. The `relay_pat_` prefix aids
identification and secret-scanning.

#### 3. PAT bearer authentication scheme

A custom `AuthenticationHandler<PatAuthSchemeOptions>` registered as scheme
`"Pat"` alongside the existing cookie scheme in `AddAuthentication(...)`:

- Reads `Authorization: Bearer relay_pat_...`. If the header is absent or not
  our prefix, returns `AuthenticateResult.NoResult()` so it never interferes
  with cookie auth.
- On a valid token: `Validate` → `userId` → `DataManager.FindUser` → build a
  `ClaimsPrincipal` with `ClaimTypes.Name = username`, identical in shape to the
  cookie path, so all downstream user resolution is unchanged.
- Invalid/expired → `AuthenticateResult.Fail(...)`.

#### 4. In-process MCP host

- Official C# SDK `ModelContextProtocol.AspNetCore`, Streamable HTTP transport.
- Mapped at `/api/mcp` (already exempt from the login redirect), with
  `.RequireAuthorization` bound to the `"Pat"` scheme.
- Runs in the Relay process; shares the `DataManager` singleton via DI.
- Tool handlers resolve the current user from `IHttpContextAccessor`
  (`HttpContext.User`) → `FindUser(username)`.
- Adds `ModelContextProtocol` NuGet packages (assumes .NET 8+, already met).
  Exact version pinned during planning.

#### 5. MCP tools (read-only)

A `RelayMcpTools` class with `[McpServerTool]` methods, each returning small
serializable DTOs projected from `ReadOnly*` wrappers, each starting from the
authenticated user so permission scoping is automatic:

- `list_projects()` → `DataManager.GetUserProjects(user)` → (id, alias, role).
- `list_spaces(projectId)` → spaces in a visible project.
- `list_jobs(projectId, spaceId)` → jobs with (id, alias, type, status).
- `get_job(projectId, spaceId, jobId)` → details, parameter values, ports.
- `list_job_types()` → catalog from `Job.Types` + `UiField` metadata
  (type guid, category, label, parameter schema).

Tools (not MCP resources) for v1 — simplest and best client support. Resources
are a later refinement.

#### 6. Personal settings UI

A **new** overlay, separate from the admin `OverlaySettings`:

- Add `Personal` to `OverlayScreenType`; wire it through `RelaySession`
  navigation/URL and the overlay renderer the same way `Settings`/`Queues` are.
- `OverlayPersonal.razor` (wrapped in `OverlayBase`) hosting one
  `AccessTokenManager` panel:
  - token list: name, created, last used, expiry, **Revoke**;
  - **New token** dialog: collects a name (+ optional expiry), then shows the
    raw token **once** with a copy button and a "you won't see this again"
    warning.
- Entry point: a "Personal settings" item in the user/avatar menu (same place
  the existing Settings overlay is triggered).

### Data flow (happy path)

1. User opens Personal settings → New token → `PersonalAccessTokenService.Generate`
   → raw token shown once; hash persisted.
2. User pastes the token into their MCP client config as a bearer credential.
3. Client connects to `/api/mcp`; the `Pat` handler validates the token and
   attaches a `ClaimsPrincipal` (username).
4. Client calls e.g. `list_projects`; the handler resolves the user via
   `FindUser` and returns `GetUserProjects(user)` projected to DTOs.

## Error handling

- Missing/garbled/expired/revoked token → 401 from the `Pat` scheme; no tool
  runs.
- Tool referencing an id the user can't see → return not-found/empty rather than
  leaking existence (consistent with `DataManager` permission checks).
- PAT store file unreadable on load → log and start empty (matches
  `SecurityTokenService` behavior).
- Generation/revocation failures surface a toast in the UI; the dialog does not
  close until the raw token has been shown.

## Testing

- **`PersonalAccessTokenService`:** generate/validate/expiry/revoke; assert the
  raw token is never persisted (only the hash); ownership enforced on revoke.
- **`Pat` auth handler:** valid → principal with correct username; invalid /
  expired / wrong-prefix → no principal; absent header → `NoResult`.
- **Tool permission scoping:** user A cannot see user B's projects/spaces/jobs.
- **Manual E2E:** mint a PAT in the UI, point an MCP client at `/api/mcp` with
  the bearer, confirm `list_projects` / `list_jobs` / `list_job_types`.

## New dependencies

- `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` NuGet packages.

## Future work (out of scope here)

- Mutation tools (create → configure → queue), driven by `UiField`-derived
  patch schemas.
- OAuth 2.1 resource-server flow (Protected Resource Metadata pointing at the
  existing IdP) as the seamless-UX successor to PATs.
- PAT scopes / capability subsetting for least-privilege agents.
- MCP resources and server-push notifications via `DataManager` events.
- General personal-settings surface (profile, password) on the new overlay.
- **Automated tests for the `Pat` auth handler and tool-level permission
  scoping.** These live in the `Relay` project, which `Refund.Tests` does not
  reference, so the prototype covers them by build + manual verification only.
  The underlying logic is unit-tested (`PersonalAccessTokenService.Validate`)
  or is pre-existing behavior (`DataManager.GetUserProjects`). Closing this gap
  means adding a `Relay.Tests` project that can construct `DataManager`.
