# MCP Mutation Tools + Fine-Grained PAT Permissions — Design

**Date:** 2026-06-29
**Status:** Approved (design), pending implementation plan
**Branch:** `feat/mcp-mutations-permissions`
**Builds on:** `2026-06-29-mcp-pat-prototype-design.md` (the read-only prototype, now merged)

## Goal

Turn the read-only MCP prototype into a full read/write surface an LLM agent can
use to build and run cryo-EM workflows — create/configure/connect/queue/abort/
delete jobs, create/delete spaces, create/delete projects — while letting the
token's owner constrain what the agent may do. A Personal Access Token (PAT)
gains **three independent access levels** (one each for the Project, Space, and
Job tiers) so a user who doesn't fully trust their agent can, for example, allow
full job management but forbid deleting a project.

### Success criterion

An agent configured with a user's PAT can, subject to that PAT's per-tier levels:

- discover job types and queues,
- create a project/space, create and configure jobs, connect their ports, and
  queue them (locally or to a cluster),
- abort and delete jobs, delete spaces/projects,

and is **denied** any operation whose tier level it lacks (e.g. `delete_project`
with `ProjectAccess < Manage` returns a clean permission error and mutates
nothing).

### Explicit non-goals (this spec)

- **OAuth 2.1 resource-server flow.** PATs remain the only credential. OAuth is
  a separate later spec.
- **MCP resources + server-push** (live job-status streaming via DataManager
  events). Separate later spec.
- **Per-item permissions.** Levels apply per *tier* across all of the owner's
  visible projects — not to specific project/space/job ids. (Considered and
  deliberately dropped as over-granular.)
- **Project member management** via MCP. Adding/removing users is access-control
  and stays human-only in the Blazor UI.
- **Authorization in DataManager / the Blazor UI.** PAT levels constrain the MCP
  surface only. Relay's broader lack of mutation authorization is pre-existing
  and out of scope.

## Background / current state

From exploration of the codebase at design time:

- **No authorization is enforced on mutations.** Every `DataManager` mutation
  takes a `ReadOnlyUser user` but uses it only to (a) confirm the user exists
  (`ResolveUser`) and (b) stamp audit/event fields — never to check membership,
  ownership, or role. The single real access rule, `GetUserProjects` (admin OR
  owner OR member), filters reads and currently has **zero callers**. Therefore
  the PAT permission layer is **the first real authorization in the system**,
  and it lives entirely in the MCP tool layer.
- **All mutations funnel through one `ExecuteWithLock` pattern** in
  `DataManager`; the consistent first argument is `ReadOnlyUser user` (the
  exceptions — `CreateEdge`, `DeleteEdge`, `UpdateEdge`, `DeleteProject` — take
  no user param). Mutations operate on `ReadOnly*` wrappers / ids, never raw
  model objects, except `template` parameters and `string typeGuid`.
- **Job parameters are plain C# properties** on the concrete `Job` subclass,
  decorated with `UiField` attributes and registered in the static
  `Job.TypeParameters` / `Job.TypeUiFields` dictionaries. There is **no
  name/value setter**; the only way to change a parameter is the mutation lambda
  `UpdateJob(ReadOnlyUser, ReadOnlyJob, Action<Job>)`. Translating a declarative
  `{name: value}` patch into that lambda is the keystone of `configure_job`.
- **The prototype** already provides: `PersonalAccessToken : RelayBase`,
  `PersonalAccessTokenService` (file-backed, SHA-256-hashed, O(1) by hash),
  `PatAuthenticationHandler` (scheme `"Pat"`, `Bearer relay_pat_…`), an
  in-process MCP host at `/api/mcp` (`ModelContextProtocol.AspNetCore`,
  stateless HTTP), a `RelayMcpTools` class, DTOs/projections in `Refund.Mcp`,
  and a Personal-settings overlay with an Access-tokens tab. All five existing
  tools are read-only.

## Architecture

### The permission model

`AccessLevel` is an ordered enum:

```
None (0) < Read (1) < EditRun (2) < Manage (3)
```

Each PAT carries three independent levels, applied to **all** items of that tier
within the owner's visible projects (no ids, no hierarchy traversal):

- `ProjectAccess`
- `SpaceAccess`
- `JobAccess`

Level semantics, per tier:

| Level | Authorizes (on that tier) |
|-------|---------------------------|
| None | nothing — the tier is invisible/forbidden |
| Read | list / get |
| EditRun | Read + create items, and (jobs) configure, connect, queue, abort |
| Manage | EditRun + delete items of that tier |

A mutation tool is gated by the level of **the tier of the thing it acts on**
(creating a space is a Space-tier operation, not a Project-tier one):

| Tool | Tier · minimum level |
|------|----------------------|
| `list_projects` | Project · Read |
| `list_spaces` | Space · Read |
| `list_jobs`, `get_job` | Job · Read |
| `list_job_types`, `list_queues` | — (authenticated only) |
| `create_project` | Project · EditRun |
| `delete_project` | Project · **Manage** |
| `create_space` | Space · EditRun |
| `delete_space` | Space · **Manage** |
| `create_job`, `configure_job`, `connect_jobs`, `disconnect_jobs`, `queue_job`, `abort_job` | Job · EditRun |
| `delete_job` | Job · **Manage** |

Tiers are independent; the layer enforces only each tool's own check. A token
with `Project=None` cannot `list_projects`, so it cannot discover ids to reach
spaces/jobs — that is the owner's choice, not a coupling the layer enforces.

**Worked example (the motivating case):** `Project=Read, Space=EditRun,
Job=Manage` lets the agent navigate everything, create spaces, and fully manage
jobs (including deleting failed ones), but it can never delete a space or a
project.

### Components

#### 1. `AccessLevel` enum + PAT fields (`Refund.DataModel`)

Add `public enum AccessLevel { None = 0, Read = 1, EditRun = 2, Manage = 3 }`.

Add three `[RelayProperty]` fields to `PersonalAccessToken`: `ProjectAccess`,
`SpaceAccess`, `JobAccess` (all `AccessLevel`, default `None`).
`RelayBase` serializes enums already (the prototype relies on this); these
round-trip as their underlying int/string per existing convention.

#### 2. PAT service & validation changes (`Refund.Services`)

- `PersonalAccessTokenService.Generate` gains the three levels as parameters and
  persists them.
- `Validate(string rawToken)` returns the resolved **`PersonalAccessToken`**
  (owner id + the three levels) instead of just `int? ownerId`, so the auth
  layer can carry levels into the request. (Existing `LastUsedDate` stamping is
  unchanged.) A small return type or returning the record directly — decided in
  the plan; the record is simplest.
- **Migration on load:** any persisted PAT whose three levels are all `None`
  (only legacy prototype tokens, since the create UI forbids all-`None`) is
  upgraded in memory to all-`Manage`, preserving today's full-access behavior.

#### 3. Auth handler carries levels (`Relay.Services`)

`PatAuthenticationHandler` already resolves the token to a user. It additionally
stashes the three levels where tools can read them per request. Chosen
mechanism: write them into `HttpContext.Items` (e.g. one entry holding the
`PersonalAccessToken` or a small immutable `PatGrants` struct). The username
claim is unchanged, so `FindUser`-based user resolution in tools is untouched.

#### 4. `PatAuthorization` (pure, `Refund.Mcp`)

A pure helper with no ASP.NET dependency so it is unit-testable in `Refund.Tests`:

```csharp
public readonly record struct PatGrants(
    AccessLevel Project, AccessLevel Space, AccessLevel Job);

public enum PermTier { Project, Space, Job }

public static class PatAuthorization
{
    // true if grants meet `required` for `tier`
    public static bool Allows(PatGrants grants, PermTier tier, AccessLevel required);
}
```

`RelayMcpTools` calls `Allows`; on failure it throws an MCP error (read/list
tools instead return empty). The `PatGrants` value is read from
`HttpContext.Items` in the tool layer and passed in — keeping `PatAuthorization`
free of HTTP types.

#### 5. `configure_job` patch translation (the keystone)

Input: a `{ parameterName: jsonValue }` map. Steps:

1. Resolve the job's concrete `Job` subclass and look up its parameter set from
   the existing `Job.TypeParameters` / `Job.TypeUiFields` metadata (the same
   source `list_job_types` already projects).
2. For each entry: find the matching settable parameter; **reject unknown
   names** with an error listing the valid parameter names.
3. Coerce the JSON value to the property's CLR type: `int`, `decimal`, `bool`,
   `string`, **enum by name**, and the `Nullable<T>` variants. Coercion failure
   → error naming the parameter and expected type.
4. Accumulate all sets into a single `Action<Job>` and apply via
   `DataManager.UpdateJob(user, job, lambda)`.

Only declared parameters are settable — no arbitrary reflection onto other
fields. This mirrors `list_job_types` so the agent's loop is: discover names/
types → `create_job` → `configure_job` → `connect_jobs` → `queue_job`.

#### 6. Mutation tools (`Relay.Services.RelayMcpTools`)

Each resolves the current user (unchanged), checks its tier level via
`PatAuthorization`, resolves the target `ReadOnly*` objects through
`DataManager`/`GetUserProjects` (returning not-found rather than leaking
existence), then calls the corresponding `DataManager` method:

- `create_project(alias?)` → `CreateProject(user, template?)`
- `delete_project(projectId)` → `DeleteProject(project)`
- `create_space(projectId, alias?)` → `CreateSpace(user, project, template?)`
- `delete_space(projectId, spaceId)` → `DeleteSpace(user, space)`
- `create_job(projectId, spaceId, typeGuid)` → `CreateJob(user, view, typeGuid)`
  (uses the space's default/active view, consistent with the UI path)
- `configure_job(projectId, spaceId, jobId, params)` → patch translation → `UpdateJob`
- `connect_jobs(projectId, spaceId, fromJobId, fromPort, toJobId, toPort)` → `CreateEdge(space, fromPort, toPort)`
- `disconnect_jobs(projectId, spaceId, fromJobId, fromPort, toJobId, toPort)` → resolve the edge → `DeleteEdge(edge)`
- `queue_job(projectId, spaceId, jobId, queueId?)` → `QueueLocalJob(user, job)` when `queueId` is absent, else `QueueClusterJob(user, job, queue)`
- `abort_job(projectId, spaceId, jobId)` → `AbortJob(user, job)`
- `delete_job(projectId, spaceId, jobId)` → `DeleteJob(user, job)`

#### 7. New read tool: `list_queues`

`list_queues()` → authenticated-only (no tier check, like `list_job_types`),
returning each queue's id, name/alias, and type (local vs cluster) so the agent
can supply a valid `queueId` to `queue_job`. Sourced from the DataManager's
queue registry (exact accessor pinned in the plan).

#### 8. Personal-settings UI

In the existing Access-tokens tab (`AccessTokenManager`):

- The **New token** form gains three labelled dropdowns — Projects, Spaces, Jobs
  — each `None/Read/EditRun/Manage`. The form rejects an all-`None` selection
  (a token that can do nothing). Name + expiry are unchanged; the raw token is
  still revealed once with a copy button.
- The token **list** gains a compact levels column, e.g. `P:R S:E J:M`
  (None shown as `–`).

### Data flow (write happy path)

1. Owner mints a PAT with, say, `Project=Read, Space=EditRun, Job=Manage`.
2. Agent calls `list_job_types` / `list_queues` (auth only), then
   `create_job` (Job·EditRun ✓) → `configure_job` (✓) → `connect_jobs` (✓) →
   `queue_job` (✓).
3. Agent calls `delete_project` → `PatAuthorization.Allows(grants, Project,
   Manage)` is false → clean MCP permission error, nothing mutated.

## Error handling

- Missing/garbled/expired/revoked token → 401 from the `Pat` scheme (unchanged).
- Authenticated but insufficient tier level → MCP error for mutating tools;
  empty result for read/list tools (no existence leak).
- Target id the user can't see (outside `GetUserProjects`) → not-found/empty.
- `configure_job` unknown parameter or bad coercion → error naming the offending
  parameter and the valid set / expected type; **no partial application** (the
  lambda is built fully before `UpdateJob` runs).
- State-machine violations (e.g. queueing a non-queueable job) surface the
  `DataManager` exception message as an MCP error.

## Testing

Unit tests live in `Refund.Tests` (which references `Refund` only):

- **`AccessLevel` + PAT serialization:** the three levels round-trip through
  `RelayBase` write/read.
- **`PatAuthorization.Allows`:** the full tier × level boundary matrix
  (e.g. `Job=EditRun` allows `create_job` but denies `delete_job`).
- **`configure_job` patch translation:** name resolution, coercion for
  int/decimal/bool/string/enum-by-name/nullable, unknown-name rejection,
  all-or-nothing (no partial sets on failure). Tested against a real job type
  (e.g. a `Refine3D`/`Class3D` parameter) via `new T().AsReadOnly()` under the
  existing `[Collection("JobRegistry")]` + `EnsurePopulated()` guard.
- **Migration heuristic:** an all-`None` persisted token loads as all-`Manage`;
  a token with any non-`None` level is left unchanged.

**Manual E2E** (MCP Inspector, non-admin account so scoping is observable): mint
PATs at a few tier combinations; confirm allowed tools succeed and forbidden
ones return permission errors and mutate nothing.

The auth-handler wiring and tool-layer level checks remain build-plus-manual,
because `Refund.Tests` does not reference `Relay` (the `PatAuthorization` and
patch-translation logic they call *is* unit-tested). Closing this gap still
means a future `Relay.Tests` project — carried forward from the prototype's
future-work list.

## New dependencies

None beyond the prototype's `ModelContextProtocol` / `.AspNetCore` packages.

## Future work (out of scope here)

- OAuth 2.1 resource-server flow (Protected Resource Metadata → existing IdP).
- MCP resources + server-push notifications via `DataManager` events.
- A `Relay.Tests` project covering the `Pat` auth handler and tool-level checks.
- General personal-settings surface (profile, password) on the overlay.
- Per-item (id-scoped) permissions, if blanket per-tier ever proves too coarse.
