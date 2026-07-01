# MCP Job-Inspection Tools (logs, results, clone/clear) — Design

**Date:** 2026-07-01
**Status:** Approved (design), pending implementation plan
**Builds on:** `2026-06-29-mcp-mutations-and-permissions-design.md` (read/write MCP + per-tier PAT permissions, now merged)

## Goal

Expand the MCP surface so an agent can inspect a job's *progress and output*, not
just its configuration and status. Today the agent can create/configure/queue/
abort/delete jobs and read their metadata, but it is blind to what a job actually
printed and what artifacts it produced. This spec adds seven tools:

- **`get_job_stdout`** / **`get_job_stderr`** — the last N lines of a job's raw
  stdout/stderr, with `\r` progress-bar lines collapsed to their final state.
- **`get_job_log`** — the last N lines of a job's cleaned per-iteration log
  (Relay's processed log), for a chosen or the latest available iteration. Added
  after initial implementation: many jobs (local/import/note jobs) never write a
  raw `std.out`, but do have a processed log — so a stdout-only view is blind to
  them.
- **`list_job_results`** — the downloadable result artifacts of a job (per
  iteration).
- **`get_job_result_link`** — an absolute download URL for one named result.
- **`clone_job`** — duplicate a job (e.g. to retry with tweaked parameters).
- **`clear_job`** — reset a job's outputs back to Building, keeping its params.

### Success criterion

An agent with a PAT of sufficient per-tier level can, for a job it can see:

- fetch the tail of stdout/stderr and see clean lines (no progress-bar spam),
- fetch the tail of the cleaned per-iteration log even for jobs that never wrote
  a raw `std.out`,
- list the job's downloadable results for the latest (or a specified) iteration,
- obtain a working absolute download link for a specific result,
- clone the job into a view, and clear the job's results —

and is **denied** (clean permission error, no side effects) any tool whose tier
level the PAT lacks.

### Explicit non-goals (this spec)

- **Streaming / live tail.** Tools return a point-in-time snapshot; no server-push
  or polling. (The UI already polls client-side; agents can re-call the tool.)
- **Authenticating the file endpoint.** `/api/file/{hash}` remains unauthenticated
  (hash-obfuscated only). Returning a download link therefore hands out a
  credential-free URL — accepted here, called out under Security below.
- **Multi-job clone (`CloneJobTree`).** `clone_job` wraps single-job `CloneJob`.
  Cloning connected subgraphs is out of scope.
- **Merging the raw and processed logs into one tool.** `get_job_stdout` reads
  raw `std.out` and `get_job_log` reads the `.relay/log_it{NNNN}.txt` processed
  log; they stay separate rather than one tool that guesses/falls back. Relay has
  a genuine duality — cluster jobs produce raw stdout, while Relay separately
  prepares cleaned per-iteration logs (sometimes derived from stdout) — and the
  two tools expose each side explicitly. (`get_job_log` was added after the
  initial six-tool implementation once it was clear stdout-only left many jobs
  blind.)

## Background / current state

From exploration of the codebase at design time:

- **Log files.** Raw stdout/stderr are at `{job.DirectoryPath}/std.out` and
  `/std.err` (`Job.NameStdOut`/`NameStdErr`, `Refund/DataModel/Job.cs:387`).
  `ReadOnlyJob` exposes `DirectoryPath`, `NameStdOut`, `NameStdErr`
  (`Refund/DataModel/ReadOnly/ReadOnlyJob.cs`). A processed per-iteration log also
  exists (`LogFilePath(iteration)`), which the UI prefers for display — not used
  by this spec.
- **`\r` trimming already exists.** `JobTools.CleanProgressBarLines(string[])`
  (`Refund/Utils/JobTools.cs:54`) replaces each line containing `\r` with the
  substring after its last `\r`. The Blazor log-tail JS does the same client-side
  (`BasicJobCardContent.razor.js`), fetching the last 4096 bytes via HTTP Range
  and showing the last ~9 lines.
- **Downloadable results.** Each output port's resource overrides
  `GetDownloadables()` → `Downloadable { Name, Description, ServerPath,
  VisualizationPath }` (`Refund/DataModel/Resource.cs`; e.g. `Map`, `ParticleSet`,
  `MapList`). Results are iteration-scoped: `HasResultFilesForIteration(i)` and
  `LogsAvailableIteration` (`Refund/DataModel/Job.cs:317,443`). The UI enumerates
  them in `JobProperties.UpdateResults()` (`Relay/Panels/Right/JobProperties.razor.cs:148`):
  iterations `0..LogsAvailableIteration` filtered by `HasResultFilesForIteration`,
  then per output port `GetResource(iteration).GetDownloadables()`.
  `ReadOnlyPortOut.GetResource(iteration)` returns the resource
  (`Refund/DataModel/ReadOnly/ReadOnlyPort.cs:153`).
- **Download links.** `FileService.GetUrl(path)` maps a path to a SHA-1 hash and
  returns `/api/file/{hash}` (`Refund/Services/FileService.cs:70`). `FileServer`
  (`Relay/Controllers/FileServer.cs`) serves it with **no authentication** —
  security is by hash-obfuscation. `FileService` is a registered singleton
  (`Relay/Program.cs:75`).
- **Clone / clear.** `DataManager.CloneJob(user, job, view)` clones a single job
  into a view and returns the new `ReadOnlyJob` (`DataManager.Job.cs:219`).
  `DataManager.ClearJob(user, job)` transitions Clearing→Building and deletes
  outputs while keeping parameters (`DataManager.Job.cs:386`). Neither is exposed
  via MCP today.
- **Permissions.** `PermTier { Project, Space, Job }` × `AccessLevel { None, Read,
  EditRun, Manage }` (`Refund/Mcp/PatAuthorization.cs`). Tools resolve the user
  via `CurrentUser()`, gate reads with `Can(...)` (returning empty/null when
  denied) and mutations with `Require(...)` (throwing `McpException`), and wrap
  DataManager mutations in `Invoke(...)` to surface business-rule messages
  (`Relay/Services/RelayMcpTools.cs`).
- **Test style.** Existing MCP tests (`Refund.Tests/Mcp/`) are unit tests over the
  *pure* pieces (`RelayMcpProjections`, `RelayMcpParameterPatch`,
  `PatAuthorization`), not integration tests of the tool class. New logic should
  likewise live in pure, directly-testable helpers.

## Design

All six tools are added to `RelayMcpTools` and follow the established pattern:
`CurrentUser()` → permission check → `dataManager.GetUserProjects(user)...FindSpace/FindJob`
→ DTO projection (reads) or `Invoke(() => dataManager...)` (mutations).

### `RelayMcpTools` constructor change

Add `FileService` to the primary constructor:

```csharp
public class RelayMcpTools(
    IHttpContextAccessor contextAccessor,
    DataManager dataManager,
    FileService fileService)
```

It is a registered singleton, so DI resolves it without further wiring.

### 1. `get_job_stdout` / `get_job_stderr` (read — `PermTier.Job, Read`)

Params: `int projectId, int spaceId, int jobId, int lines = 100`.

- Clamp `lines` to `[1, 1000]`.
- Path is derived from the job itself — `Path.Combine(job.DirectoryPath,
  job.NameStdOut)` (or `NameStdErr`). **No agent-supplied path.**
- Read a **bounded tail window** (last ~512 KB) rather than the whole file
  (std.out can be very large). If the file is larger than the window, drop the
  first (partially-read) line, mirroring the UI's Range-fetch behavior.
- Apply `JobTools.CleanProgressBarLines`, drop empty lines, take the last `lines`.
- If `Can(PermTier.Job, Read)` is false → return `JobLogDto(false, 0, "")`
  (consistent with reads returning empty when denied). If the file does not exist
  (job hasn't produced that stream yet) → `Exists=false`, empty text.

Returns `JobLogDto(bool Exists, int Lines, string Text)` where `Text` is the
joined last-N lines.

New pure helper in `JobTools`:

```csharp
// Reads up to maxWindowBytes from the end of the file, splits into lines,
// drops a leading partial line if the window was truncated, cleans \r
// progress bars, drops empty lines, and returns the last maxLines.
public static string[] ReadLogTail(string path, int maxLines, int maxWindowBytes = 512 * 1024)
```

This is the unit-tested core (trim + tail + partial-first-line + missing file).

### 1b. `get_job_log` (read — `PermTier.Job, Read`)

Params: `int projectId, int spaceId, int jobId, int? iteration = null, int lines = 100`.

- Resolve the iteration: `iteration ?? job.LogsAvailableIteration`. If that is
  `< 0` (no logs available) → `Exists=false`, `Iteration=-1`.
- Path is the job's own processed log — `job.LogFilePath(iter)`
  (`.relay/log_it{iter:D4}.txt`). **No agent-supplied path.**
- Read via the same `JobTools.ReadLogTail` (bounded window, `\r`/blank cleanup,
  last-N). The processed log is already cleaned, so `ReadLogTail` is idempotent
  here; reusing it keeps the tail/last-N behavior identical to the stdout tools.
- Denied → `JobIterationLogDto(false, -1, 0, "")`. Missing file for a resolved
  iteration → `Exists=false` with the resolved `Iteration` echoed back.

Returns `JobIterationLogDto(bool Exists, int Iteration, int Lines, string Text)`.
`Iteration` reports which iteration was actually read (useful because "latest" is
resolved server-side).

This tool is the answer to Relay's log duality: local/import/note jobs never write
a raw `std.out`, so `get_job_stdout` returns `Exists=false` for them — but they do
have a processed per-iteration log that `get_job_log` surfaces.

### 2. `list_job_results` (read — `PermTier.Job, Read`)

Params: `int projectId, int spaceId, int jobId, int? iteration = null`.

- Resolve iteration: if `iteration` is null, use the **latest** `i` in
  `0..LogsAvailableIteration` with `HasResultFilesForIteration(i)`; if none,
  return `[]`. If `iteration` is given, use it as-is.
- For each `port` in `job.PortsOut`, call
  `port.GetResource(iteration)?.GetDownloadables()` (guarded per-port with
  try/catch like the UI, so one bad port doesn't fail the whole call) and project
  to DTOs.
- Denied → `[]`.

Returns `IReadOnlyList<JobResultDto(string Port, string Name, string Description, int Iteration)>`.
`(Port, Name, Iteration)` is the stable key for `get_job_result_link`.

New pure helper in `RelayMcpProjections`:

```csharp
public static IReadOnlyList<JobResultDto> ToResultDtos(ReadOnlyJob job, int iteration)
```

Unit-tested with a fake resource exposing downloadables.

### 3. `get_job_result_link` (read — `PermTier.Job, Read`)

Params: `int projectId, int spaceId, int jobId, string port, string name, int? iteration = null`.

- Resolve iteration as in `list_job_results`.
- **Re-enumerate the job's real downloadables** and find the one matching
  `(port, name, iteration)`. Only its `ServerPath` (never an agent-supplied path)
  is passed to `fileService.GetUrl(...)`. No match → `McpException`.
- Build an **absolute** URL from the incoming request:
  `$"{request.Scheme}://{request.Host}{fileService.GetUrl(serverPath)}"`
  (the relative part is `/api/file/{hash}`).
- Denied → `null`.

Returns `ResultLinkDto(string Name, string FileName, string Url)`
(`FileName = Path.GetFileName(serverPath)`).

### 4. `clone_job` (write — `PermTier.Job, EditRun`)

Params: `int projectId, int spaceId, int jobId, int? viewId = null`.

- `Require(PermTier.Job, EditRun)`.
- Resolve the job. Resolve the target view: if `viewId` given, `space.FindView(viewId)`
  (missing → `McpException`); if omitted, default to the space's **first view**
  (`space.Views` first; if the space somehow has none → `McpException`).
- `var clone = await Invoke(() => dataManager.CloneJob(user, job, view));`
- Returns `CreatedDto(clone.Id, clone.AliasOrId)`.

### 5. `clear_job` (write — `PermTier.Job, Manage`)

Params: `int projectId, int spaceId, int jobId`.

- `Require(PermTier.Job, Manage)` — same tier as `delete_job`, since clearing
  destroys result files on disk.
- `await Invoke(() => dataManager.ClearJob(user, job));`
- Returns `OkDto(true)`.

### New DTOs (`Refund/Mcp/McpDtos.cs`)

```csharp
public record JobLogDto(bool Exists, int Lines, string Text);
public record JobIterationLogDto(bool Exists, int Iteration, int Lines, string Text);
public record JobResultDto(string Port, string Name, string Description, int Iteration);
public record ResultLinkDto(string Name, string FileName, string Url);
```

## Security

- **Log reads** use only paths derived from the job's own `DirectoryPath` +
  fixed `NameStdOut`/`NameStdErr`. No path traversal surface.
- **`get_job_result_link`** never hashes an arbitrary path: it validates the
  requested `(port, name, iteration)` against the job's actual enumerated
  downloadables and only hashes the matched `ServerPath`. This prevents an agent
  from turning the tool into an arbitrary-file-read oracle.
- **Unauthenticated file endpoint (accepted risk).** The returned link is
  fetchable by anyone who has the hash, independent of the PAT's tiers. This
  matches how the Blazor UI already works and is acceptable for the intended
  localhost/agent use. If tighter control is wanted later, authenticating
  `/api/file` is a separate change.
- All six tools are gated by the existing per-tier PAT model; no new
  authorization logic is introduced.

## Files touched

- `Relay/Services/RelayMcpTools.cs` — 7 tool methods + `FileService` ctor param.
- `Refund/Mcp/McpDtos.cs` — `JobLogDto`, `JobIterationLogDto`, `JobResultDto`, `ResultLinkDto`.
- `Refund/Mcp/RelayMcpProjections.cs` — `ToResultDtos(job, iteration)`.
- `Refund/Utils/JobTools.cs` — `ReadLogTail(path, maxLines, maxWindowBytes)`.
- `Refund.Tests/Mcp/` — new test file(s):
  - `ReadLogTail`: `\r` trimming, last-N, partial-first-line drop, missing file,
    empty file.
  - `ToResultDtos`: enumerates a fake resource's downloadables; latest-iteration
    resolution; empty when no results.
  - link-key validation: unknown `(port, name)` rejected; absolute-URL shape.

## Testing approach

Keep the testable logic pure (`JobTools.ReadLogTail`,
`RelayMcpProjections.ToResultDtos`) and unit-test it directly, matching the
existing `RelayMcpProjections`/`JobTools`/`PatAuthorization` test style. The
tool methods themselves are thin wiring over these helpers plus the already-tested
`Can`/`Require`/`Invoke` conventions, so they need no new integration harness.
