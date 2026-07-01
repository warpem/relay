# MCP Job-Inspection Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add six MCP tools so an agent can read a job's stdout/stderr, list its downloadable results, get a download link for a result, and clone or clear a job.

**Architecture:** Pure, unit-testable helpers carry all real logic (`JobTools.ReadLogTail` for the log tail + `\r` trimming; `RelayMcpProjections` for iteration resolution and downloadable→DTO mapping/matching). The six tools on `RelayMcpTools` are thin wiring over those helpers plus the existing `CurrentUser`/`Can`/`Require`/`Invoke` conventions and `DataManager`/`FileService`.

**Tech Stack:** C# / .NET 10, ASP.NET Core, ModelContextProtocol server SDK, xUnit.

## Global Constraints

- Target framework: `net10.0` (all projects).
- `Nullable` is `disable` in `Refund`/`Relay`, but `?` annotations are used throughout existing MCP code — match that style; do not add `#nullable` directives.
- Follow the established `RelayMcpTools` pattern: `CurrentUser()` → permission check → `dataManager.GetUserProjects(user)...FindSpace(...)?.FindJob(...)` → DTO (reads) or `Invoke(() => dataManager...)` (mutations).
- Reads return empty/null when the token lacks access (`if (!Can(...)) return [] / null / empty DTO`); mutations call `Require(...)` which throws `McpException`.
- Permission tiers/levels: `PermTier { Project, Space, Job }` × `AccessLevel { None, Read, EditRun, Manage }`.
- Never hash or read an agent-supplied filesystem path. Log paths are derived from the job; download paths are validated against the job's own enumerated downloadables.
- Build: `dotnet build Relay.sln`. Test: `dotnet test Refund.Tests/Refund.Tests.csproj`.

---

### Task 1: `JobTools.ReadLogTail` — bounded log tail with `\r` trimming

**Files:**
- Modify: `Refund/Utils/JobTools.cs` (add method to the existing `JobTools` static class, near `CleanProgressBarLines` at line 54)
- Test: `Refund.Tests/Utils/JobToolsReadLogTailTests.cs` (create)

**Interfaces:**
- Consumes: existing `JobTools.CleanProgressBarLines(string[])`.
- Produces: `public static string[] JobTools.ReadLogTail(string path, int maxLines, int maxWindowBytes = 512 * 1024)` — returns the last `maxLines` non-empty log lines from the tail of `path`, with CRLF endings normalized and `\r` progress-bar lines collapsed to their final segment. Missing file → empty array.

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/Utils/JobToolsReadLogTailTests.cs`:

```csharp
using Refund.Utils;

namespace Refund.Tests.Utils;

public class JobToolsReadLogTailTests
{
    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "relay_logtail_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void CollapsesCarriageReturnProgressBars()
    {
        string path = WriteTemp("start\nprogress: 10%\rprogress: 50%\rprogress: 100%\ndone\n");
        try
        {
            var tail = JobTools.ReadLogTail(path, 100);
            Assert.Equal(new[] { "start", "progress: 100%", "done" }, tail);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnsOnlyLastNLines()
    {
        string path = WriteTemp(string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line{i}")) + "\n");
        try
        {
            var tail = JobTools.ReadLogTail(path, 3);
            Assert.Equal(new[] { "line8", "line9", "line10" }, tail);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DropsPartialFirstLine_WhenWindowTruncates()
    {
        string content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line{i:D3}")) + "\n";
        string path = WriteTemp(content);
        try
        {
            // Tiny window forces the read to start mid-file; the partial first line must be dropped.
            var tail = JobTools.ReadLogTail(path, 100, maxWindowBytes: 20);
            Assert.Equal("line100", tail[^1]);
            Assert.All(tail, l => Assert.Matches(@"^line\d{3}$", l)); // every returned line is complete
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        string missing = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N"));
        Assert.Empty(JobTools.ReadLogTail(missing, 100));
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        string path = WriteTemp("alpha\r\nbeta\r\ngamma\r\n");
        try
        {
            var tail = JobTools.ReadLogTail(path, 100);
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, tail);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobToolsReadLogTailTests"`
Expected: build/compile failure — `'JobTools' does not contain a definition for 'ReadLogTail'`.

- [ ] **Step 3: Implement `ReadLogTail`**

In `Refund/Utils/JobTools.cs`, add inside the `#region Log Processing Helpers` (after `CleanProgressBarLines`, before `#endregion`):

```csharp
        /// <summary>
        /// Reads the tail of a log file and returns its last <paramref name="maxLines"/> non-empty
        /// lines. Only the final <paramref name="maxWindowBytes"/> bytes are read (logs can be huge);
        /// if the file exceeds the window the first, partially-read line is dropped. CRLF endings are
        /// normalized and \r progress-bar lines are collapsed to their final segment.
        /// </summary>
        /// <param name="path">Path to the log file.</param>
        /// <param name="maxLines">Maximum number of lines to return.</param>
        /// <param name="maxWindowBytes">Maximum number of trailing bytes to read.</param>
        /// <returns>The cleaned trailing lines, or an empty array if the file does not exist.</returns>
        public static string[] ReadLogTail(string path, int maxLines, int maxWindowBytes = 512 * 1024)
        {
            if (!File.Exists(path))
                return Array.Empty<string>();

            string content;
            bool truncated;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, stream.Length - maxWindowBytes);
                truncated = start > 0;
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
            }

            string[] rawLines = content.Split('\n');
            IEnumerable<string> lines = truncated ? rawLines.Skip(1) : rawLines;

            // Strip the CR of CRLF endings first (leaving embedded \r for CleanProgressBarLines),
            // then collapse progress-bar lines, then drop blanks and take the last N.
            string[] stripped = lines
                .Select(l => l.EndsWith("\r") ? l.Substring(0, l.Length - 1) : l)
                .ToArray();

            return CleanProgressBarLines(stripped)
                .Where(l => l.Length > 0)
                .TakeLast(maxLines)
                .ToArray();
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobToolsReadLogTailTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add Refund/Utils/JobTools.cs Refund.Tests/Utils/JobToolsReadLogTailTests.cs
git commit -m "feat: JobTools.ReadLogTail for bounded log tail with progress-bar trimming"
```

---

### Task 2: Results projection helpers + `JobResultDto`

**Files:**
- Modify: `Refund/Mcp/McpDtos.cs` (add `JobResultDto`)
- Modify: `Refund/Mcp/RelayMcpProjections.cs` (add three static helpers)
- Test: `Refund.Tests/Mcp/RelayMcpResultsProjectionTests.cs` (create)

**Interfaces:**
- Consumes: `Refund.DataModel.Downloadable { string Name; string Description; string ServerPath; }`.
- Produces:
  - `public record JobResultDto(string Port, string Name, string Description, int Iteration);`
  - `public static int RelayMcpProjections.ResolveResultIteration(int? requested, int logsAvailableIteration, Func<int, bool> hasResultFilesForIteration)` — returns `requested` if set, else the greatest `i` in `logsAvailableIteration..0` with results, else `-1`.
  - `public static JobResultDto RelayMcpProjections.ToResultDto(string port, Downloadable d, int iteration)`
  - `public static Downloadable RelayMcpProjections.MatchDownloadable(IEnumerable<(string Port, Downloadable Downloadable)> items, string port, string name)` — first item whose port and downloadable name match, else `null`.

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/Mcp/RelayMcpResultsProjectionTests.cs`:

```csharp
using Refund.DataModel;
using Refund.Mcp;

namespace Refund.Tests.Mcp;

public class RelayMcpResultsProjectionTests
{
    [Fact]
    public void ResolveResultIteration_UsesRequested_WhenProvided()
    {
        Assert.Equal(3, RelayMcpProjections.ResolveResultIteration(3, 10, _ => false));
    }

    [Fact]
    public void ResolveResultIteration_PicksLatestWithResults_WhenNull()
    {
        var withResults = new HashSet<int> { 0, 2, 5 };
        Assert.Equal(5, RelayMcpProjections.ResolveResultIteration(null, 7, withResults.Contains));
    }

    [Fact]
    public void ResolveResultIteration_ReturnsMinusOne_WhenNoneHaveResults()
    {
        Assert.Equal(-1, RelayMcpProjections.ResolveResultIteration(null, 4, _ => false));
    }

    [Fact]
    public void ToResultDto_MapsFields()
    {
        var d = new Downloadable("Half-map 1", "the first half map", "/data/job/half1.mrc");
        var dto = RelayMcpProjections.ToResultDto("Volume", d, 5);
        Assert.Equal("Volume", dto.Port);
        Assert.Equal("Half-map 1", dto.Name);
        Assert.Equal("the first half map", dto.Description);
        Assert.Equal(5, dto.Iteration);
    }

    [Fact]
    public void MatchDownloadable_FindsByPortAndName()
    {
        var items = new (string, Downloadable)[]
        {
            ("Volume", new Downloadable("Half-map 1", "", "/d/h1.mrc")),
            ("Volume", new Downloadable("Mask", "", "/d/mask.mrc")),
        };
        var match = RelayMcpProjections.MatchDownloadable(items, "Volume", "Mask");
        Assert.NotNull(match);
        Assert.Equal("/d/mask.mrc", match.ServerPath);
    }

    [Fact]
    public void MatchDownloadable_ReturnsNull_WhenNoMatch()
    {
        var items = new (string, Downloadable)[] { ("Volume", new Downloadable("Half-map 1", "", "/d/h1.mrc")) };
        Assert.Null(RelayMcpProjections.MatchDownloadable(items, "Volume", "Nonexistent"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~RelayMcpResultsProjectionTests"`
Expected: compile failure — `'RelayMcpProjections' does not contain a definition for 'ResolveResultIteration'` (and `JobResultDto` missing).

- [ ] **Step 3: Add the `JobResultDto` record**

In `Refund/Mcp/McpDtos.cs`, add after the `JobDetailDto` record (line 25):

```csharp
/// <summary>A downloadable result artifact of a job at a given iteration.
/// (Port, Name, Iteration) is the key passed to get_job_result_link.</summary>
public record JobResultDto(string Port, string Name, string Description, int Iteration);
```

- [ ] **Step 4: Add the projection helpers**

In `Refund/Mcp/RelayMcpProjections.cs`, add inside the `RelayMcpProjections` class (after `ToDetailDto`):

```csharp
    /// <summary>
    /// Resolves which iteration's results to use: the caller's explicit choice if given, otherwise the
    /// greatest iteration in [0, logsAvailableIteration] that has result files, or -1 if none do.
    /// </summary>
    public static int ResolveResultIteration(int? requested, int logsAvailableIteration, Func<int, bool> hasResultFilesForIteration)
    {
        if (requested.HasValue)
            return requested.Value;
        for (int i = logsAvailableIteration; i >= 0; i--)
            if (hasResultFilesForIteration(i))
                return i;
        return -1;
    }

    public static JobResultDto ToResultDto(string port, Downloadable d, int iteration) =>
        new(port, d.Name, d.Description, iteration);

    /// <summary>
    /// Finds the downloadable matching (port, name) among the job's enumerated downloadables, or null.
    /// Used to validate a get_job_result_link request against real outputs before exposing a file URL.
    /// </summary>
    public static Downloadable MatchDownloadable(IEnumerable<(string Port, Downloadable Downloadable)> items, string port, string name)
    {
        foreach (var (p, d) in items)
            if (p == port && d.Name == name)
                return d;
        return null;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~RelayMcpResultsProjectionTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add Refund/Mcp/McpDtos.cs Refund/Mcp/RelayMcpProjections.cs Refund.Tests/Mcp/RelayMcpResultsProjectionTests.cs
git commit -m "feat: MCP results projection helpers (iteration resolve, DTO, match)"
```

---

### Task 3: Log tools — `get_job_stdout` / `get_job_stderr`

**Files:**
- Modify: `Refund/Mcp/McpDtos.cs` (add `JobLogDto`)
- Modify: `Relay/Services/RelayMcpTools.cs` (add usings, `ReadJobLog` helper, two tools)

**Interfaces:**
- Consumes: `JobTools.ReadLogTail(path, maxLines)` (Task 1); `ReadOnlyJob.DirectoryPath`, `ReadOnlyJob.NameStdOut`, `ReadOnlyJob.NameStdErr`.
- Produces: `public record JobLogDto(bool Exists, int Lines, string Text);`; MCP tools `get_job_stdout` and `get_job_stderr`.

Note: these tools are thin wiring over already-tested helpers, following the existing untested tool pattern in this file. Verification is a clean build, the existing test suite staying green, and an optional live smoke test — matching how the prior MCP tools were verified.

- [ ] **Step 1: Add the `JobLogDto` record**

In `Refund/Mcp/McpDtos.cs`, add after the `JobResultDto` record from Task 2:

```csharp
/// <summary>A snapshot of a job's log stream tail. Exists=false when the job has not produced
/// that stream yet (distinct from an empty file).</summary>
public record JobLogDto(bool Exists, int Lines, string Text);
```

- [ ] **Step 2: Add usings to `RelayMcpTools.cs`**

At the top of `Relay/Services/RelayMcpTools.cs`, add after line 8 (`using Refund.Services.Core.DataManager;`):

```csharp
using Refund.Utils;
```

(`System.IO` and `System.Linq` are available via ImplicitUsings.)

- [ ] **Step 3: Add the `ReadJobLog` helper and the two tools**

In `Relay/Services/RelayMcpTools.cs`, add after the `get_job` tool (after line 119), before `list_job_types`:

```csharp
    private JobLogDto ReadJobLog(int projectId, int spaceId, int jobId, int lines, bool stdout)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Job, AccessLevel.Read)) return new JobLogDto(false, 0, "");
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) return new JobLogDto(false, 0, "");

        int clamped = Math.Clamp(lines, 1, 1000);
        string path = Path.Combine(job.DirectoryPath, stdout ? job.NameStdOut : job.NameStdErr);
        if (!File.Exists(path)) return new JobLogDto(false, 0, "");

        string[] tail = JobTools.ReadLogTail(path, clamped);
        return new JobLogDto(true, tail.Length, string.Join("\n", tail));
    }

    [McpServerTool(Name = "get_job_stdout"), Description("Get the last N lines of a job's stdout (progress-bar lines collapsed to their final state).")]
    public JobLogDto GetJobStdout(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Max lines to return (default 100, max 1000).")] int lines = 100)
        => ReadJobLog(projectId, spaceId, jobId, lines, stdout: true);

    [McpServerTool(Name = "get_job_stderr"), Description("Get the last N lines of a job's stderr (progress-bar lines collapsed to their final state).")]
    public JobLogDto GetJobStderr(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Max lines to return (default 100, max 1000).")] int lines = 100)
        => ReadJobLog(projectId, spaceId, jobId, lines, stdout: false);
```

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build Relay.sln`
Expected: build succeeds.
Run: `dotnet test Refund.Tests/Refund.Tests.csproj`
Expected: all tests pass (existing + Tasks 1–2).

- [ ] **Step 5: Commit**

```bash
git add Refund/Mcp/McpDtos.cs Relay/Services/RelayMcpTools.cs
git commit -m "feat: MCP get_job_stdout / get_job_stderr tools"
```

---

### Task 4: Results tools — `list_job_results` / `get_job_result_link`

**Files:**
- Modify: `Refund/Mcp/McpDtos.cs` (add `ResultLinkDto`)
- Modify: `Relay/Services/RelayMcpTools.cs` (add `FileService` ctor param, `EnumerateDownloadables` helper, two tools)

**Interfaces:**
- Consumes: `RelayMcpProjections.ResolveResultIteration`, `ToResultDto`, `MatchDownloadable` (Task 2); `ReadOnlyJob.PortsOut` (`ReadOnlyDictionary<string, ReadOnlyPortOut>`), `ReadOnlyPortOut.Name`, `ReadOnlyPortOut.GetResource(int)`, `ReadOnlyJob.LogsAvailableIteration`, `ReadOnlyJob.HasResultFilesForIteration(int)`; `FileService.GetUrl(string)` → `/api/file/{hash}`.
- Produces: `public record ResultLinkDto(string Name, string FileName, string Url);`; MCP tools `list_job_results` and `get_job_result_link`.

- [ ] **Step 1: Add the `ResultLinkDto` record**

In `Refund/Mcp/McpDtos.cs`, add after `JobLogDto`:

```csharp
/// <summary>An absolute download URL for one named job result.</summary>
public record ResultLinkDto(string Name, string FileName, string Url);
```

- [ ] **Step 2: Inject `FileService` into `RelayMcpTools`**

In `Relay/Services/RelayMcpTools.cs`, change the primary constructor (line 18) from:

```csharp
public class RelayMcpTools(IHttpContextAccessor contextAccessor, DataManager dataManager)
```

to:

```csharp
public class RelayMcpTools(IHttpContextAccessor contextAccessor, DataManager dataManager, Refund.Services.FileService fileService)
```

(`FileService` is registered as a singleton in `Relay/Program.cs:75`, so DI resolves it automatically. Using the fully-qualified name avoids a new `using`.)

- [ ] **Step 3: Add the `EnumerateDownloadables` helper and the two tools**

In `Relay/Services/RelayMcpTools.cs`, add after the `get_job_stderr` tool from Task 3:

```csharp
    // Collects (portName, downloadable) pairs for a job at one iteration. A single failing port is
    // skipped rather than failing the whole listing (mirrors JobProperties.UpdateResults in the UI).
    private static List<(string Port, Downloadable Downloadable)> EnumerateDownloadables(ReadOnlyJob job, int iteration)
    {
        var result = new List<(string, Downloadable)>();
        foreach (var port in job.PortsOut.Values)
        {
            try
            {
                var downloadables = port.GetResource(iteration)?.GetDownloadables();
                if (downloadables == null) continue;
                foreach (var d in downloadables)
                    result.Add((port.Name, d));
            }
            catch { /* skip this port */ }
        }
        return result;
    }

    [McpServerTool(Name = "list_job_results"), Description("List a job's downloadable results for an iteration (default: the latest iteration with results).")]
    public IReadOnlyList<JobResultDto> ListJobResults(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Optional iteration; omit for the latest iteration with results.")] int? iteration = null)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Job, AccessLevel.Read)) return [];
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) return [];

        int iter = RelayMcpProjections.ResolveResultIteration(iteration, job.LogsAvailableIteration, job.HasResultFilesForIteration);
        if (iter < 0) return [];

        return EnumerateDownloadables(job, iter)
            .Select(x => RelayMcpProjections.ToResultDto(x.Port, x.Downloadable, iter))
            .ToList();
    }

    [McpServerTool(Name = "get_job_result_link"), Description("Get an absolute download URL for one named result of a job (see list_job_results).")]
    public ResultLinkDto? GetJobResultLink(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("The output port name (from list_job_results).")] string port,
        [Description("The result name (from list_job_results).")] string name,
        [Description("Optional iteration; omit for the latest iteration with results.")] int? iteration = null)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Job, AccessLevel.Read)) return null;
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");

        int iter = RelayMcpProjections.ResolveResultIteration(iteration, job.LogsAvailableIteration, job.HasResultFilesForIteration);
        if (iter < 0) throw new McpException("Job has no results.");

        var match = RelayMcpProjections.MatchDownloadable(EnumerateDownloadables(job, iter), port, name);
        if (match == null) throw new McpException($"No result named '{name}' on port '{port}' for iteration {iter}.");

        string relative = fileService.GetUrl(match.ServerPath); // "/api/file/{hash}"
        var request = contextAccessor.HttpContext?.Request;
        string url = request != null ? $"{request.Scheme}://{request.Host}{relative}" : relative;
        return new ResultLinkDto(match.Name, Path.GetFileName(match.ServerPath), url);
    }
```

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build Relay.sln`
Expected: build succeeds.
Run: `dotnet test Refund.Tests/Refund.Tests.csproj`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add Refund/Mcp/McpDtos.cs Relay/Services/RelayMcpTools.cs
git commit -m "feat: MCP list_job_results / get_job_result_link tools"
```

---

### Task 5: Lifecycle tools — `clone_job` / `clear_job`

**Files:**
- Modify: `Relay/Services/RelayMcpTools.cs` (add two tools)

**Interfaces:**
- Consumes: `DataManager.CloneJob(ReadOnlyUser, ReadOnlyJob, ReadOnlyView)` → `ReadOnlyJob`; `DataManager.ClearJob(ReadOnlyUser, ReadOnlyJob)` → `Task`; `ReadOnlySpace.Views` (`ReadOnlyCollection<ReadOnlyView>`), `ReadOnlySpace.FindView(int)`, `ReadOnlySpace.FindJob(int)`; `ReadOnlyJob.Id`, `ReadOnlyJob.AliasOrId`.
- Produces: MCP tools `clone_job` (returns `CreatedDto`) and `clear_job` (returns `OkDto`).

- [ ] **Step 1: Add the two tools**

In `Relay/Services/RelayMcpTools.cs`, add after `delete_job` (after line 282), within the `// ---- Job lifecycle tools ----` section:

```csharp
    [McpServerTool(Name = "clone_job"), Description("Clone a job (copies parameters and input connections) into a view.")]
    public async Task<CreatedDto> CloneJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id to clone.")] int jobId,
        [Description("Optional target view id (from list_views); omit for the space's first view.")] int? viewId = null)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) throw new McpException($"Space {spaceId} not found.");
        var job = space.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");
        var view = viewId.HasValue ? space.FindView(viewId.Value) : space.Views.FirstOrDefault();
        if (view == null)
            throw new McpException(viewId.HasValue ? $"View {viewId} not found in space {spaceId}." : $"Space {spaceId} has no views.");
        var clone = await Invoke(() => dataManager.CloneJob(user, job, view));
        return new CreatedDto(clone.Id, clone.AliasOrId);
    }

    [McpServerTool(Name = "clear_job"), Description("Clear a job's results and reset it to Building (keeps its parameters).")]
    public async Task<OkDto> ClearJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.Manage);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");
        await Invoke(() => dataManager.ClearJob(user, job));
        return new OkDto(true);
    }
```

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build Relay.sln`
Expected: build succeeds.
Run: `dotnet test Refund.Tests/Refund.Tests.csproj`
Expected: all tests pass.

- [ ] **Step 3: Commit**

```bash
git add Relay/Services/RelayMcpTools.cs
git commit -m "feat: MCP clone_job / clear_job tools"
```

---

### Task 6: Live smoke test (verification)

**Files:** none (manual verification against a running instance).

The `.mcp.json` at the repo root already defines `relay-full` (all tiers) and `relay-restricted` PAT connections at `http://localhost:5001/api/mcp`. Use these to confirm the wiring the unit tests can't cover.

- [ ] **Step 1: Run the app**

Run: `dotnet run --project Relay/Relay.csproj`
Expected: server listening on `http://localhost:5001`.

- [ ] **Step 2: Exercise the new tools via the MCP client (`relay-full`)**

For a job that has run and produced output, confirm:
- `get_job_stdout` / `get_job_stderr` return a `JobLogDto` with `Exists=true`, `Lines>0`, and no `\r` progress-bar spam in `Text`.
- `list_job_results` returns one or more `JobResultDto` for the latest iteration.
- `get_job_result_link` with a `(port, name)` from that list returns a `ResultLinkDto` whose `Url` is absolute (`http://localhost:5001/api/file/...`) and downloads the file when fetched.
- `get_job_result_link` with a bogus `name` returns a clean error, not a stack trace.
- `clone_job` creates a new job (returns its id); `clear_job` on a finished job resets it to Building.

- [ ] **Step 3: Confirm permission gating via `relay-restricted`**

With a PAT whose `JobAccess` is below the required level, confirm:
- `get_job_stdout` / `list_job_results` return empty (no data leak) when `JobAccess < Read`.
- `clone_job` is denied when `JobAccess < EditRun`; `clear_job` is denied when `JobAccess < Manage`, each with a clean permission error and no side effect.

- [ ] **Step 4: Stop the app.**

---

## Notes for the implementer

- Tasks 1–2 are pure TDD. Tasks 3–5 are thin wiring over those helpers and the existing, already-tested `Can`/`Require`/`Invoke`/`CurrentUser` conventions; they are verified by build + the full suite staying green + the Task 6 live smoke. This mirrors how the existing MCP tools in this file are structured and verified.
- Deliberate error-handling asymmetry in `get_job_result_link`: permission-denied returns `null` (consistent with other reads), but job-not-found / no-results / no-match `throw McpException` so the agent gets actionable feedback for a request it explicitly parameterized. This is intentional, not an oversight.
- Do not authenticate `/api/file` — out of scope (see spec's non-goals). The returned link is intentionally credential-free.
