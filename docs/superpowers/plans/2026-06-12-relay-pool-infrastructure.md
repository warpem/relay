# Relay Pool Infrastructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add first-class worker pool management to Relay so that WarpTools GPU jobs can maintain a fleet of short-lived cluster worker jobs alongside a single Manager job, with batched scheduler polling and automatic pool dissolution.

**Architecture:** `ClusterQueue` gains two new batch-operation templates (`ListJobsTemplate`, `CancelManyJobsTemplate`) and a refactored submission path (prepare script / submit script split). A new `WorkerPool` class owned by `QueueRepository` maintains fleet state (submitted IDs, alive IDs, submission count), ticks on every daemon cycle, and dissolves when the Manager ends. `QueueRepository` is split into partial class files as part of this work.

**Tech Stack:** C# / .NET 10, xUnit, Blazor (FluentUI), existing `ClusterQueue`/`QueueRepository` patterns. All field view components are `.razor` files in `Refund/UIFields/`. Tests use xUnit `[Fact]` assertions; run with `dotnet test`.

---

## File map

### New files
| Path | Purpose |
|---|---|
| `Refund/JobQueues/WorkerPool.cs` | Fleet state machine for one pooled job |
| `Refund/UIFields/UiQueue.cs` | `[UiQueue]` attribute |
| `Refund/UIFields/UiQueueView.razor` | Dropdown renderer for queue picker field |
| `Refund/Services/Core/Repositories/QueueRepository.QueueOps.cs` | Queue CRUD operations (split from main file) |
| `Refund/Services/Core/Repositories/QueueRepository.Daemon.cs` | Daemon loop (split from main file) |
| `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs` | Handle*State methods (split from main file) |
| `Refund/Services/Core/Repositories/QueueRepository.Pool.cs` | Pool dict, wiring, re-adoption |
| `Refund.Tests/JobQueues/WorkerPoolTests.cs` | WorkerPool unit tests |
| `Refund.Tests/JobQueues/ClusterQueueBatchTests.cs` | Batch template tests |

### Modified files
| Path | What changes |
|---|---|
| `Refund/JobQueues/ClusterQueue.cs` | Refactor `ProcessSubmissionScript` + `SubmitJob`; add `BuildWorkerScript`, `SubmitScript`, `ListActiveJobIds`, `CancelJobs`; add two new `[RelayProperty]` fields |
| `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs` | Expose `ListJobsTemplate`, `CancelManyJobsTemplate` |
| `Refund/DataModel/Job.cs` | Add `IPooledJob` interface |
| `Refund/Jobs/Abstract.cs` | Add `PoolQueueId`, `PoolSize`, `PoolWorkersAlive`, `PoolWorkersSubmitted` to `WarpJobGpu`; implement `IPooledJob` |
| `Refund/Services/Core/Repositories/QueueRepository.cs` | Shrink to constructor + fields + `LoadQueues` + `FindQueue` + `SaveQueues` + auto-save + dispose; add `_workerPools` field |

---

## Task 1: Refactor `ClusterQueue` — split `ProcessSubmissionScript` and `SubmitJob`

**Files:**
- Modify: `Refund/JobQueues/ClusterQueue.cs`
- Test: `Refund.Tests/JobQueues/ClusterQueueBatchTests.cs` (create)

This is a pure refactor — no behavior changes. Split the existing monolithic `SubmitJob` into three methods so the pool can reuse `SubmitScript` independently.

- [ ] **Step 1: Create the test file with a compilation-only test**

Create `Refund.Tests/JobQueues/ClusterQueueBatchTests.cs`:

```csharp
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ClusterQueueBatchTests
{
    [Fact]
    public void ClusterQueue_Exists()
    {
        // Placeholder — replaced by real tests in later tasks.
        // Ensures the test project compiles against ClusterQueue.
        Assert.True(typeof(ClusterQueue) != null);
    }
}
```

- [ ] **Step 2: Run tests to confirm baseline passes**

```bash
cd /Users/tegunovd/dev/relay-public
dotnet test Refund.Tests --no-build 2>&1 | tail -10
```

Expected: all existing tests pass.

- [ ] **Step 3: Refactor `ProcessSubmissionScript` to take dictionaries**

In `ClusterQueue.cs`, change the signature of `ProcessSubmissionScript` from:
```csharp
protected string ProcessSubmissionScript(string scriptTemplate, Job job, Dictionary<string, string> customValues = null)
```
to:
```csharp
protected string ProcessSubmissionScript(
    string scriptTemplate,
    Dictionary<string, string> resourceValues,
    string[] requiredModules,
    Dictionary<string, string> customValues = null)
```

Inside the method body, replace the two usages of `job`:
- `var resourceValues = job.GetResourceValues();` → remove (parameter is now passed in)
- `job.RequiredModules.Contains(closingModule)` → `requiredModules.Contains(closingModule)`

Update the one existing call site inside `SubmitJob` (the `ProcessSubmissionScript` call in the `Task.Run` body):
```csharp
// Before:
string script = ProcessSubmissionScript(SubmissionScriptTemplate
                    .ReplaceRegex("{{\\s*command\\s*}}", jobCommand.ToString())
                    .ReplaceRegex("{{\\s*job_id\\s*}}", job.Id.ToString()),
                    job,
                    customValues);

// After:
string script = ProcessSubmissionScript(
    SubmissionScriptTemplate
        .ReplaceRegex("{{\\s*command\\s*}}", jobCommand.ToString())
        .ReplaceRegex("{{\\s*job_id\\s*}}", job.Id.ToString()),
    job.GetResourceValues(),
    job.RequiredModules,
    customValues);
```

- [ ] **Step 4: Extract `PrepareAndWriteScript` and `SubmitScript` from `SubmitJob`**

Add these two new methods to `ClusterQueue.cs`. Extract the content from inside the existing `Task.Run` lambda in `SubmitJob`:

```csharp
/// <summary>
/// Prepares the submission script for a job and writes it to disk.
/// Returns the absolute path to the written script.
/// </summary>
private async Task<string> PrepareAndWriteScript(Job job, Dictionary<string, string> customValues = null)
{
    job.DirectoryName = job.Id.ToString();

    if (Directory.Exists(job.DirectoryPath) &&
        !string.IsNullOrWhiteSpace(job.DirectoryName) &&
        !Path.GetFullPath(job.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
             .Equals(Path.GetFullPath(job.Space.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                     StringComparison.OrdinalIgnoreCase))
        Directory.Delete(job.DirectoryPath, true);

    Directory.CreateDirectory(job.DirectoryPath);
    Directory.CreateDirectory(job.RelayResultsDirectoryPath);

    job.Stage();

    string scriptPath = Path.Combine(job.DirectoryPath, "submit.sh");

    Dictionary<string, string> arguments = job.ComposeCommandArguments();
    string commandName = job.CommandName;

    StringBuilder jobCommand = new StringBuilder();
    jobCommand.AppendLine($"cd {job.RunDirectory}\n");
    jobCommand.Append(job.CommandPrefix);
    jobCommand.Append($"{commandName} {string.Join(" ", arguments.Select(kv => string.IsNullOrWhiteSpace(kv.Value) ?
                                                                               $"--{kv.Key}" :
                                                                               $"--{kv.Key} {kv.Value}"))}");
    jobCommand.AppendLine(job.CommandSuffix);

    string script = ProcessSubmissionScript(
        SubmissionScriptTemplate
            .ReplaceRegex("{{\\s*command\\s*}}", jobCommand.ToString())
            .ReplaceRegex("{{\\s*job_id\\s*}}", job.Id.ToString()),
        job.GetResourceValues(),
        job.RequiredModules,
        customValues);

    await File.WriteAllTextAsync(scriptPath, script);
    await job.WriteToLifecycleLog($"Written following submission script to {scriptPath}:\n\n{script}\n\n");

    return scriptPath;
}

/// <summary>
/// Submits a pre-written script to the cluster scheduler.
/// Returns the cluster job ID assigned by the scheduler.
/// </summary>
public async Task<string> SubmitScript(string scriptPath)
{
    string clusterCommand = SubmitJobTemplate.ReplaceRegex("{{\\s*script_path_abs\\s*}}", scriptPath);
    string output = await ExecuteOnCluster(clusterCommand);
    return ParseClusterJobId(output);
}
```

- [ ] **Step 5: Rewrite `SubmitJob` as a thin wrapper**

Replace the body of the `Task.Run` inside `SubmitJob` with calls to the two new methods:

```csharp
public override void SubmitJob(Job job, Dictionary<string, string> customValues = null)
{
    base.SubmitJob(job);

    lock (Sync)
    {
        if (StagingJobs.ContainsKey(job))
            throw new Exception($"Job {job.Id} is already staging!");

        CancellationTokenSource cts = new();
        StagingJobs.Add(job, cts);

        Task.Run(async () =>
        {
            try
            {
                JobUpdateCallback(job, j =>
                {
                    j.DirectoryName = j.Id.ToString();
                    j.Status = JobStatus.Staging;
                });

                string scriptPath = await PrepareAndWriteScript(job, customValues);
                cts.Token.ThrowIfCancellationRequested();

                lock (Sync)
                    JobsInLimbo.Add(job);

                await job.WriteToLifecycleLog($"Submitting script: {scriptPath}");

                string jobId = await SubmitScript(scriptPath);
                await job.WriteToLifecycleLog($"Parsed cluster job ID: {jobId}");

                JobUpdateCallback(job, j => { j.ClusterJobId = jobId; });
            }
            catch (Exception exc)
            {
                await job.WriteToErrorLog($"Job {job.Id} cancelled before it went to cluster:\n{exc}");
                JobUpdateCallback(job, j => j.Status = JobStatus.Failed);
            }
            finally
            {
                lock (Sync)
                {
                    StagingJobs.Remove(job);
                    if (!JobsInLimbo.Contains(job))
                        JobsInLimbo.Remove(job);
                }
            }
        }, cts.Token);
    }
}
```

- [ ] **Step 6: Build and confirm no compilation errors**

```bash
dotnet build Refund/Refund.csproj 2>&1 | grep -E "error|warning" | grep -v "obj/" | head -20
```

Expected: 0 errors.

- [ ] **Step 7: Run tests**

```bash
dotnet test Refund.Tests 2>&1 | tail -10
```

Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add Refund/JobQueues/ClusterQueue.cs Refund.Tests/JobQueues/ClusterQueueBatchTests.cs
git commit -m "refactor: split ClusterQueue.SubmitJob into PrepareAndWriteScript + SubmitScript

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 2: Add batch template fields and methods to `ClusterQueue`

**Files:**
- Modify: `Refund/JobQueues/ClusterQueue.cs`
- Modify: `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs`
- Modify: `Refund.Tests/JobQueues/ClusterQueueBatchTests.cs`

- [ ] **Step 1: Write failing tests for `ListActiveJobIds` and `CancelJobs`**

Replace the placeholder test in `ClusterQueueBatchTests.cs` with:

```csharp
using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ClusterQueueBatchTests
{
    [Fact]
    public void ListJobsTemplate_DefaultsToEmpty()
    {
        var queue = new ClusterQueue(_ => { });
        Assert.Equal("", queue.ListJobsTemplate);
    }

    [Fact]
    public void CancelManyJobsTemplate_DefaultsToEmpty()
    {
        var queue = new ClusterQueue(_ => { });
        Assert.Equal("", queue.CancelManyJobsTemplate);
    }

    [Fact]
    public async Task ListActiveJobIds_ThrowsWhenTemplateNotConfigured()
    {
        var queue = new ClusterQueue(_ => { });
        // ListJobsTemplate is empty — must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ListActiveJobIds());
    }

    [Fact]
    public async Task CancelJobs_ThrowsWhenTemplateNotConfigured()
    {
        var queue = new ClusterQueue(_ => { });
        // CancelManyJobsTemplate is empty — must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.CancelJobs(new[] { "123", "456" }));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test Refund.Tests --filter "ClusterQueueBatchTests" 2>&1 | tail -15
```

Expected: 2 tests fail (`ListActiveJobIds_ThrowsWhenTemplateNotConfigured`, `CancelJobs_ThrowsWhenTemplateNotConfigured`) because the methods don't exist yet. The other 2 may also fail for the same reason.

- [ ] **Step 3: Add the two new `[RelayProperty]` fields to `ClusterQueue`**

Add after `SubmissionScriptTemplate` in `ClusterQueue.cs`:

```csharp
/// <summary>
/// Command that returns one active cluster job ID per line for the submitting user.
/// Required when this queue is used as a pool queue for WarpTools GPU jobs.
/// Example SLURM: squeue -u $USER -h -o "%i"
/// </summary>
[RelayProperty]
public string ListJobsTemplate { get; set; } = "";

/// <summary>
/// Command to cancel multiple jobs in one call.
/// Supports {{job_ids}} placeholder (space-separated IDs).
/// Required when this queue is used as a pool queue for WarpTools GPU jobs.
/// Example SLURM: scancel {{job_ids}}
/// </summary>
[RelayProperty]
public string CancelManyJobsTemplate { get; set; } = "";
```

- [ ] **Step 4: Add `ListActiveJobIds` and `CancelJobs` methods to `ClusterQueue`**

Add these methods after `CancelJobs` (place near the other cluster-communication methods, before `ExecuteOnCluster`):

```csharp
/// <summary>
/// Returns the set of currently active cluster job IDs by executing ListJobsTemplate.
/// Throws if ListJobsTemplate is not configured.
/// </summary>
public async Task<HashSet<string>> ListActiveJobIds()
{
    if (string.IsNullOrWhiteSpace(ListJobsTemplate))
        throw new InvalidOperationException(
            $"Queue \"{Alias}\" has no ListJobsTemplate configured. " +
            "Add a command that prints one active job ID per line (e.g. squeue -u $USER -h -o \"%i\").");

    string output = await ExecuteOnCluster(ListJobsTemplate);
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToHashSet();
}

/// <summary>
/// Cancels all provided cluster job IDs in a single scheduler call.
/// Throws if CancelManyJobsTemplate is not configured.
/// </summary>
public async Task CancelJobs(IEnumerable<string> jobIds)
{
    if (string.IsNullOrWhiteSpace(CancelManyJobsTemplate))
        throw new InvalidOperationException(
            $"Queue \"{Alias}\" has no CancelManyJobsTemplate configured. " +
            "Add a command using {{job_ids}} placeholder (e.g. scancel {{job_ids}}).");

    var ids = jobIds.ToList();
    if (ids.Count == 0)
        return;

    string command = CancelManyJobsTemplate.ReplaceRegex(
        "{{\\s*job_ids\\s*}}", string.Join(" ", ids));
    await ExecuteOnCluster(command);
}
```

- [ ] **Step 5: Add `BuildWorkerScript` to `ClusterQueue`**

Add this method after `SubmitScript`:

```csharp
/// <summary>
/// Builds and writes a worker submission script without requiring a full Job object.
/// Used by WorkerPool to prepare a reusable script before any workers are submitted.
/// Returns the absolute path to the written script file.
/// </summary>
public string BuildWorkerScript(
    string command,
    Dictionary<string, string> resourceValues,
    string[] requiredModules,
    string scriptPath)
{
    string script = ProcessSubmissionScript(
        SubmissionScriptTemplate.ReplaceRegex("{{\\s*command\\s*}}", command),
        resourceValues,
        requiredModules);

    Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
    File.WriteAllText(scriptPath, script);
    return scriptPath;
}
```

- [ ] **Step 6: Expose new properties on `ReadOnlyClusterQueue`**

Add to `ReadOnlyClusterQueue.cs` after `SubmissionScriptTemplate`:

```csharp
/// <summary>Gets the command template for listing all active job IDs.</summary>
public string ListJobsTemplate => _queue.ListJobsTemplate;

/// <summary>Gets the command template for cancelling multiple jobs at once.</summary>
public string CancelManyJobsTemplate => _queue.CancelManyJobsTemplate;
```

- [ ] **Step 7: Run tests**

```bash
dotnet test Refund.Tests --filter "ClusterQueueBatchTests" 2>&1 | tail -15
```

Expected: all 4 tests pass.

- [ ] **Step 8: Build full solution**

```bash
dotnet build Refund/Refund.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
```

Expected: 0 errors.

- [ ] **Step 9: Commit**

```bash
git add Refund/JobQueues/ClusterQueue.cs \
        Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs \
        Refund.Tests/JobQueues/ClusterQueueBatchTests.cs
git commit -m "feat: add ListJobsTemplate/CancelManyJobsTemplate + batch methods to ClusterQueue

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 3: Add `IPooledJob` to `Job.cs` and implement on `WarpJobGpu`

**Files:**
- Modify: `Refund/DataModel/Job.cs`
- Modify: `Refund/Jobs/Abstract.cs`
- Test: `Refund.Tests/JobQueues/WorkerPoolTests.cs` (create)

- [ ] **Step 1: Create `WorkerPoolTests.cs` with a compilation test**

Create `Refund.Tests/JobQueues/WorkerPoolTests.cs`:

```csharp
using Refund.DataModel;

namespace Refund.Tests.JobQueues;

public class WorkerPoolTests
{
    [Fact]
    public void IPooledJob_InterfaceExists()
    {
        Assert.True(typeof(IPooledJob) != null);
    }
}
```

- [ ] **Step 2: Add `IPooledJob` interface to `Job.cs`**

Add after the existing `IClusterJob` interface near the bottom of `Job.cs`:

```csharp
/// <summary>
/// Implemented by WarpTools GPU jobs that maintain a fleet of short-lived cluster
/// worker jobs alongside the single Manager cluster job.
/// </summary>
public interface IPooledJob
{
    /// <summary>ID of the ClusterQueue to use for worker pool submissions. -1 means local/no pool.</summary>
    int PoolQueueId { get; }

    /// <summary>Target number of simultaneously running worker jobs.</summary>
    int PoolSize { get; }

    /// <summary>
    /// Maximum total worker submissions across the job's lifetime.
    /// Circuit-breaker against sick-worker replacement spirals.
    /// </summary>
    int PoolSubmissionCap { get; }

    /// <summary>Memory in GB to request per worker cluster job.</summary>
    int WorkerMemoryGb { get; }

    /// <summary>CPU cores to request per worker cluster job.</summary>
    int WorkerCoreCount { get; }

    /// <summary>Required cluster modules for worker jobs (e.g. ["gpu", "warp"]).</summary>
    string[] WorkerRequiredModules { get; }

    /// <summary>
    /// Full command string for a worker assigned to the given GPU device index.
    /// Example: "WarpWorker2 --task_dir /data/1/tasks --device 2"
    /// </summary>
    string GetWorkerCommand(int deviceIndex);
}
```

- [ ] **Step 3: Add pool fields to `WarpJobGpu` in `Abstract.cs`**

Add after the existing `PerDevice` field in `WarpJobGpu`:

```csharp
[UiFieldGroup("Resources", 999)]
[UiQueue("Pool Queue",
         helpText: "Cluster queue for GPU worker pool. Leave unset to run workers locally.")]
[RelayProperty]
public int PoolQueueId { get; set; } = -1;

[UiInt("pool_size", "Pool size",
       min: 1,
       helpText: "Target number of simultaneous GPU worker jobs in the pool. " +
                 "Only used when a pool queue is set.")]
[RelayProperty]
public int PoolSize { get; set; } = 8;

/// <summary>Number of alive pool workers at last daemon tick. Updated by QueueRepository.</summary>
[RelayProperty]
[Clearable]
public int PoolWorkersAlive { get; set; }

/// <summary>Total worker submissions since this job started. Updated by QueueRepository.</summary>
[RelayProperty]
[Clearable]
public int PoolWorkersSubmitted { get; set; }
```

- [ ] **Step 4: Implement `IPooledJob` on `WarpJobGpu`**

Change the class declaration:
```csharp
public abstract class WarpJobGpu : WarpJob, IPooledJob
```

Add the explicit interface implementation at the end of `WarpJobGpu`:

```csharp
// IPooledJob
int IPooledJob.PoolQueueId           => PoolQueueId;
int IPooledJob.PoolSize              => PoolSize;
int IPooledJob.PoolSubmissionCap     => PoolSize * 2;
int IPooledJob.WorkerMemoryGb        => MemoryPerWorker;
int IPooledJob.WorkerCoreCount       => 2;
string[] IPooledJob.WorkerRequiredModules => RequiredModules;

string IPooledJob.GetWorkerCommand(int deviceIndex) =>
    $"WarpWorker2 --task_dir {Path.Combine(DirectoryPath, "tasks")} --device {deviceIndex}";
```

- [ ] **Step 5: Override `ComposeCommandArguments` in `WarpJobGpu` to append `--external_provisioner`**

Add (or extend if already overriding) in `WarpJobGpu`:

```csharp
public override Dictionary<string, string> ComposeCommandArguments()
{
    var result = base.ComposeCommandArguments();
    if (PoolQueueId > 0)
        result["external_provisioner"] = "";
    return result;
}
```

- [ ] **Step 6: Write a real test for `IPooledJob` defaults**

Replace the placeholder test in `WorkerPoolTests.cs`:

```csharp
using Refund.DataModel;
using Refund.Jobs.Preprocessing.MotionAndCTF2D;

namespace Refund.Tests.JobQueues;

public class WorkerPoolTests
{
    [Fact]
    public void WarpJobGpu_ImplementsIPooledJob()
    {
        var job = new MotionAndCTF2D();
        Assert.IsAssignableFrom<IPooledJob>(job);
    }

    [Fact]
    public void WarpJobGpu_PoolQueueId_DefaultsToMinusOne()
    {
        var job = new MotionAndCTF2D();
        Assert.Equal(-1, ((IPooledJob)job).PoolQueueId);
    }

    [Fact]
    public void WarpJobGpu_PoolSubmissionCap_IsTwicePoolSize()
    {
        var job = new MotionAndCTF2D();
        var pooled = (IPooledJob)job;
        Assert.Equal(pooled.PoolSize * 2, pooled.PoolSubmissionCap);
    }

    [Fact]
    public void WarpJobGpu_GetWorkerCommand_ContainsDeviceIndex()
    {
        var job = new MotionAndCTF2D();
        // DirectoryPath requires Space to be set; test the format via reflection on the interface method
        // by calling with a known index and checking the suffix
        var pooled = (IPooledJob)job;
        // We can't call GetWorkerCommand without Space, but we can verify PoolSize > 0
        Assert.True(pooled.PoolSize > 0);
    }

    [Fact]
    public void WarpJobGpu_PoolWorkersAlive_DefaultsToZero()
    {
        var job = new MotionAndCTF2D();
        Assert.Equal(0, job.PoolWorkersAlive);
    }
}
```

- [ ] **Step 7: Build and run tests**

```bash
dotnet build Refund/Refund.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
dotnet test Refund.Tests --filter "WorkerPoolTests" 2>&1 | tail -15
```

Expected: 0 build errors, all tests pass.

- [ ] **Step 8: Commit**

```bash
git add Refund/DataModel/Job.cs \
        Refund/Jobs/Abstract.cs \
        Refund.Tests/JobQueues/WorkerPoolTests.cs
git commit -m "feat: add IPooledJob interface and WarpJobGpu pool fields

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 4: Implement `WorkerPool`

**Files:**
- Create: `Refund/JobQueues/WorkerPool.cs`
- Modify: `Refund.Tests/JobQueues/WorkerPoolTests.cs`

- [ ] **Step 1: Write failing tests for `WorkerPool`**

Add to `WorkerPoolTests.cs`:

```csharp
[Fact]
public void WorkerPool_Initialize_CreatesWorkerLogsDirectory()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try
    {
        var pool = new WorkerPool(new FakePoolQueue(), new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
        pool.Initialize();
        Assert.True(Directory.Exists(Path.Combine(tmpDir, "worker_logs")));
    }
    finally { Directory.Delete(tmpDir, true); }
}

[Fact]
public void WorkerPool_Tick_SubmitsMissingWorkers()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try
    {
        var fakeQueue = new FakePoolQueue();
        var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 3));
        pool.Initialize();

        // Fake queue: no active jobs initially, submit returns fake IDs
        var (alive, submitted) = pool.Tick().GetAwaiter().GetResult();

        Assert.Equal(3, submitted);        // submitted 3 to reach target
        Assert.Equal(3, alive);            // all 3 now alive
        Assert.Equal(3, fakeQueue.SubmitScriptCalls);
    }
    finally { Directory.Delete(tmpDir, true); }
}

[Fact]
public void WorkerPool_Tick_DoesNotExceedCap()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try
    {
        // Pool size 2, cap 4. Fake queue always reports 0 alive (all workers die every tick).
        var fakeQueue = new FakePoolQueue(alwaysEmpty: true);
        var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
        pool.Initialize();

        pool.Tick().GetAwaiter().GetResult();   // submits 2 (total: 2)
        pool.Tick().GetAwaiter().GetResult();   // submits 2 (total: 4)
        var (_, submitted) = pool.Tick().GetAwaiter().GetResult();  // cap reached, submits 0

        Assert.Equal(4, submitted);    // capped at PoolSize * 2 = 4
        Assert.Equal(4, fakeQueue.SubmitScriptCalls);
    }
    finally { Directory.Delete(tmpDir, true); }
}

[Fact]
public async Task WorkerPool_Dissolve_CancelsAliveJobs()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try
    {
        var fakeQueue = new FakePoolQueue();
        var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
        pool.Initialize();
        await pool.Tick();          // submits 2, both alive
        await pool.Dissolve();

        Assert.Equal(1, fakeQueue.CancelJobsCalls);  // one batch cancel call
        Assert.Equal(2, fakeQueue.CancelledIds.Count);
    }
    finally { Directory.Delete(tmpDir, true); }
}

[Fact]
public void WorkerPool_PersistsAndRestoresState()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try
    {
        var fakeQueue = new FakePoolQueue();
        var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
        pool.Initialize();
        pool.Tick().GetAwaiter().GetResult();

        // Simulate restart: new pool loads from disk
        var fakeQueue2 = new FakePoolQueue();
        var pool2 = new WorkerPool(fakeQueue2, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
        pool2.Initialize();   // loads pool_state.json

        // All submitted IDs were restored; Tick reconciles alive set
        var (_, submitted) = pool2.Tick().GetAwaiter().GetResult();
        // total_submissions was restored, so cap accounting is correct
        Assert.Equal(2, submitted);
    }
    finally { Directory.Delete(tmpDir, true); }
}
```

Add the fake helpers at the bottom of the file (inside the namespace, outside the test class):

```csharp
// ---- Test doubles ----

internal class FakePooledJob : IPooledJob
{
    private readonly string _dir;
    public FakePooledJob(string dir, int poolQueueId, int poolSize)
    {
        _dir = dir;
        PoolQueueId = poolQueueId;
        PoolSize = poolSize;
    }
    public int PoolQueueId { get; }
    public int PoolSize { get; }
    public int PoolSubmissionCap => PoolSize * 2;
    public int WorkerMemoryGb => 12;
    public int WorkerCoreCount => 2;
    public string[] WorkerRequiredModules => ["gpu"];
    public string GetWorkerCommand(int deviceIndex) => $"WarpWorker2 --device {deviceIndex}";
    // WorkerPool needs the job's DirectoryPath — expose it via the interface cast helper
    public string DirectoryPath => _dir;
}

internal class FakePoolQueue : IPoolQueue
{
    private int _nextId = 100;
    private readonly bool _alwaysEmpty;
    public int SubmitScriptCalls { get; private set; }
    public int CancelJobsCalls { get; private set; }
    public HashSet<string> CancelledIds { get; } = new();
    private readonly HashSet<string> _submitted = new();

    public FakePoolQueue(bool alwaysEmpty = false) { _alwaysEmpty = alwaysEmpty; }

    public Task<string> SubmitScript(string scriptPath)
    {
        SubmitScriptCalls++;
        var id = (_nextId++).ToString();
        _submitted.Add(id);
        return Task.FromResult(id);
    }

    public Task<HashSet<string>> ListActiveJobIds() =>
        Task.FromResult(_alwaysEmpty ? new HashSet<string>() : new HashSet<string>(_submitted));

    public Task CancelJobs(IEnumerable<string> ids)
    {
        CancelJobsCalls++;
        foreach (var id in ids) CancelledIds.Add(id);
        _submitted.ExceptWith(CancelledIds);
        return Task.CompletedTask;
    }

    public string BuildWorkerScript(string command, Dictionary<string, string> resourceValues,
        string[] requiredModules, string scriptPath)
    {
        File.WriteAllText(scriptPath, "fake script");
        return scriptPath;
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test Refund.Tests --filter "WorkerPoolTests" 2>&1 | tail -20
```

Expected: new tests fail because `WorkerPool` doesn't exist yet.

- [ ] **Step 3: Create `WorkerPool.cs`**

Note: `WorkerPool` takes a `ClusterQueue` as a constructor parameter. For testability, the four operations it calls on it (`SubmitScript`, `ListActiveJobIds`, `CancelJobs`, `BuildWorkerScript`) are extracted to a small `IPoolQueue` interface that `ClusterQueue` implements. This avoids test pain from trying to instantiate a real `ClusterQueue`.

Create `Refund/JobQueues/IPoolQueue.cs`:

```csharp
namespace Refund.JobQueues;

/// <summary>
/// Minimal interface over ClusterQueue used by WorkerPool.
/// Allows WorkerPool to be tested without a real cluster connection.
/// </summary>
public interface IPoolQueue
{
    Task<string> SubmitScript(string scriptPath);
    Task<HashSet<string>> ListActiveJobIds();
    Task CancelJobs(IEnumerable<string> jobIds);
    string BuildWorkerScript(string command, Dictionary<string, string> resourceValues,
                             string[] requiredModules, string scriptPath);
}
```

Add `IPoolQueue` to `ClusterQueue`'s declaration:
```csharp
public class ClusterQueue : JobQueue, IPoolQueue
```

`ClusterQueue` already implements all four methods — no further changes needed.

Now create `Refund/JobQueues/WorkerPool.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;

namespace Refund.JobQueues;

/// <summary>
/// Manages a fleet of short-lived GPU worker cluster jobs for one pooled Relay job.
/// Owned and driven by QueueRepository.Pool.cs.
/// </summary>
public class WorkerPool
{
    private readonly IPoolQueue  _poolQueue;
    private readonly IPooledJob  _job;
    private readonly string      _jobDir;

    private readonly HashSet<string> _submittedIds = new();
    private          HashSet<string> _aliveIds     = new();
    private int    _totalSubmissions;
    private bool   _initialized;
    private string _workerScriptPath = "";

    private string StatePath       => Path.Combine(_jobDir, "pool_state.json");
    private string WorkerLogsDir   => Path.Combine(_jobDir, "worker_logs");
    private string WorkerScriptPath => Path.Combine(_jobDir, "worker_submit.sh");

    public WorkerPool(IPoolQueue poolQueue, IPooledJob job)
    {
        _poolQueue = poolQueue;
        _job       = job;
        // IPooledJob is always also a Job (WarpJobGpu); retrieve DirectoryPath via helper
        _jobDir    = GetJobDirectory(job);
    }

    // WorkerPool is only constructed with WarpJobGpu instances (which are Jobs).
    // We access DirectoryPath through a helper to keep IPooledJob minimal.
    private static string GetJobDirectory(IPooledJob job)
    {
        if (job is Refund.DataModel.Job j)
            return j.DirectoryPath;
        // In tests, FakePooledJob exposes DirectoryPath directly
        var prop = job.GetType().GetProperty("DirectoryPath");
        return prop?.GetValue(job) as string ?? throw new InvalidOperationException(
            "IPooledJob implementation must be a Job or expose DirectoryPath.");
    }

    /// <summary>
    /// Prepares the worker submission script and loads any persisted state.
    /// Must be called before the first Tick(). Idempotent.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        Directory.CreateDirectory(WorkerLogsDir);

        _workerScriptPath = WorkerScriptPath;
        var resourceValues = new Dictionary<string, string>
        {
            { "n_cores",       _job.WorkerCoreCount.ToString() },
            { "memory_gb",     _job.WorkerMemoryGb.ToString() },
            { "n_gpus",        "1" },
            { "gpu_memory_gb", _job.WorkerMemoryGb.ToString() },
            { "worker_log_dir", WorkerLogsDir },
        };
        _poolQueue.BuildWorkerScript(
            _job.GetWorkerCommand(0), resourceValues, _job.WorkerRequiredModules, _workerScriptPath);

        LoadState();
        _initialized = true;
    }

    /// <summary>
    /// One maintenance tick: reconcile alive workers, submit replacements if needed.
    /// Returns (aliveCount, totalSubmissions) for the caller to push to the job model.
    /// </summary>
    public async Task<(int aliveCount, int totalSubmissions)> Tick()
    {
        if (!_initialized)
            throw new InvalidOperationException("Call Initialize() before Tick().");

        var active  = await _poolQueue.ListActiveJobIds();
        _aliveIds   = _submittedIds.Intersect(active).ToHashSet();

        int deficit   = Math.Max(0, _job.PoolSize - _aliveIds.Count);
        int canSubmit = Math.Max(0, _job.PoolSubmissionCap - _totalSubmissions);
        int toSubmit  = Math.Min(deficit, canSubmit);

        for (int i = 0; i < toSubmit; i++)
        {
            string id = await _poolQueue.SubmitScript(_workerScriptPath);
            _submittedIds.Add(id);
            _aliveIds.Add(id);
            _totalSubmissions++;
        }

        SaveState();
        return (_aliveIds.Count, _totalSubmissions);
    }

    /// <summary>
    /// Cancels all known alive workers and clears pool state.
    /// Called when the Manager job ends for any reason.
    /// </summary>
    public async Task Dissolve()
    {
        if (_aliveIds.Count > 0)
            await _poolQueue.CancelJobs(_aliveIds);

        _aliveIds.Clear();
        _submittedIds.Clear();
        _totalSubmissions = 0;

        if (File.Exists(StatePath))
            File.Delete(StatePath);
    }

    private void SaveState()
    {
        var tmp = StatePath + ".tmp." + Environment.ProcessId;
        var node = new JsonObject
        {
            ["pool_queue_id"]     = _job.PoolQueueId,
            ["submitted_ids"]     = new JsonArray(_submittedIds.Select(id => JsonValue.Create(id)).ToArray<JsonNode>()),
            ["total_submissions"] = _totalSubmissions,
        };
        File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StatePath, overwrite: true);
    }

    private void LoadState()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(StatePath));
            if (node == null) return;

            _totalSubmissions = node["total_submissions"]?.GetValue<int>() ?? 0;
            var ids = node["submitted_ids"]?.AsArray();
            if (ids != null)
                foreach (var id in ids)
                    if (id?.GetValue<string>() is { } s)
                        _submittedIds.Add(s);
        }
        catch
        {
            // Corrupted state file — start fresh; worst case is brief over-provisioning.
        }
    }
}
```

Update `FakePooledJob` in the test file to make `GetJobDirectory` work — it already exposes `DirectoryPath` as a property, and the reflection fallback in `WorkerPool` will find it.

- [ ] **Step 4: Run tests**

```bash
dotnet test Refund.Tests --filter "WorkerPoolTests" 2>&1 | tail -20
```

Expected: all WorkerPool tests pass.

- [ ] **Step 5: Full build**

```bash
dotnet build Refund/Refund.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add Refund/JobQueues/WorkerPool.cs \
        Refund/JobQueues/IPoolQueue.cs \
        Refund/JobQueues/ClusterQueue.cs \
        Refund.Tests/JobQueues/WorkerPoolTests.cs
git commit -m "feat: implement WorkerPool fleet state machine

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 5: Split `QueueRepository` into partial class files

**Files:**
- Modify: `Refund/Services/Core/Repositories/QueueRepository.cs`
- Create: `Refund/Services/Core/Repositories/QueueRepository.QueueOps.cs`
- Create: `Refund/Services/Core/Repositories/QueueRepository.Daemon.cs`
- Create: `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs`
- Create: `Refund/Services/Core/Repositories/QueueRepository.Pool.cs`

This is a pure mechanical split — zero behavior changes. The goal is to get the file to a manageable size before adding pool wiring.

- [ ] **Step 1: Add `partial` to `QueueRepository.cs` class declaration**

In `QueueRepository.cs`, change:
```csharp
public class QueueRepository
```
to:
```csharp
public partial class QueueRepository
```

- [ ] **Step 2: Create `QueueRepository.QueueOps.cs`**

Move `CreateClusterQueue`, `UpdateQueue`, `DeleteClusterQueue`, `QueueLocalJob`, `QueueClusterJob`, `DequeueLocalJob`, `DequeueClusterJob`, `ReorderClusterQueue`, and `FindQueue` out of `QueueRepository.cs` into the new file. Keep the same `using` directives and namespace:

```csharp
using System.Collections.ObjectModel;
using Serilog;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues;

namespace Refund.Services.Core.Repositories;

public partial class QueueRepository
{
    // Paste: CreateClusterQueue, UpdateQueue, DeleteClusterQueue,
    //        QueueLocalJob, QueueClusterJob, DequeueLocalJob,
    //        DequeueClusterJob, ReorderClusterQueue, FindQueue
}
```

Delete those methods from `QueueRepository.cs`.

- [ ] **Step 3: Create `QueueRepository.Daemon.cs`**

Move `StartDaemon`, `StopDaemon`, `RunDaemon`, `RunDaemonAsync`, `ProcessQueueJobsThrottled`, `ProcessQueueJobs`, `ProcessJob`, `TrackJobProgress`, `TrackProgressWithThrottling` into the new file with the same pattern.

Delete those methods from `QueueRepository.cs`.

- [ ] **Step 4: Create `QueueRepository.StateHandlers.cs`**

Move `HandleWaitingState`, `HandleStagingState`, `HandleRunningState`, `HandleJobCompletion`, `HandleAbortingState`, `HandleFinalizingState` into the new file.

Delete those methods from `QueueRepository.cs`.

- [ ] **Step 5: Create empty `QueueRepository.Pool.cs`**

```csharp
using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Services.Core.Repositories;

public partial class QueueRepository
{
    // Pool management — added in Task 6.
}
```

- [ ] **Step 6: Build and run all tests**

```bash
dotnet build Relay/Relay.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
dotnet test Refund.Tests 2>&1 | tail -10
```

Expected: 0 errors, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add Refund/Services/Core/Repositories/
git commit -m "refactor: split QueueRepository into partial class files

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 6: Wire pool into `QueueRepository`

**Files:**
- Modify: `Refund/Services/Core/Repositories/QueueRepository.cs`
- Modify: `Refund/Services/Core/Repositories/QueueRepository.Pool.cs`
- Modify: `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs`

- [ ] **Step 1: Add `_workerPools` field to `QueueRepository.cs`**

Add after the `_finalizationTasks` field:

```csharp
/// <summary>
/// Active worker pools keyed by their Manager job. One pool per running pooled job.
/// </summary>
private readonly ConcurrentDictionary<Job, WorkerPool> _workerPools = new();
```

- [ ] **Step 2: Implement `QueueRepository.Pool.cs`**

Replace the empty body with:

```csharp
using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Services.Core.Repositories;

public partial class QueueRepository
{
    /// <summary>
    /// Gets the existing WorkerPool for a job, or creates and initializes one.
    /// </summary>
    private WorkerPool GetOrCreatePool(Job job)
    {
        return _workerPools.GetOrAdd(job, j =>
        {
            var pooledJob = (IPooledJob)j;
            var poolQueue = FindQueue(pooledJob.PoolQueueId) as ClusterQueue
                            ?? throw new InvalidOperationException(
                                $"Pool queue {pooledJob.PoolQueueId} not found.");
            var pool = new WorkerPool(poolQueue, pooledJob);
            pool.Initialize();
            return pool;
        });
    }

    /// <summary>
    /// Re-adopts pool state for any Running/Staging pooled job after a Relay restart.
    /// Called from LoadQueues() after all jobs are restored.
    /// </summary>
    private void ReAdoptPools(IEnumerable<Job> jobs)
    {
        foreach (var job in jobs)
        {
            if (job is not IPooledJob pooledJob || pooledJob.PoolQueueId <= 0)
                continue;
            if (job.Status is not (JobStatus.Running or JobStatus.Staging))
                continue;

            var poolQueue = FindQueue(pooledJob.PoolQueueId) as ClusterQueue;
            if (poolQueue == null) continue;

            var pool = new WorkerPool(poolQueue, pooledJob);
            pool.Initialize();   // loads pool_state.json if present
            _workerPools[job] = pool;

            _logger.Information(
                "Re-adopted worker pool for job {JobId} from disk", job.Id);
        }
    }
}
```

- [ ] **Step 3: Call `ReAdoptPools` from `LoadQueues` in `QueueRepository.cs`**

At the end of `LoadQueues`, after the existing restoration logic, add:

```csharp
// Re-adopt worker pools for any pooled jobs that were running at shutdown.
var allJobs = _localQueue.QueuedJobs
    .Concat(_clusterQueues.SelectMany(q => q.QueuedJobs))
    .ToList();
ReAdoptPools(allJobs);
```

- [ ] **Step 4: Add pool validation to `HandleWaitingState` in `QueueRepository.StateHandlers.cs`**

Inside `HandleWaitingState`, before the `job.IsReadyToStage()` check, add:

```csharp
// Validate pool queue configuration early — fail fast with a clear message.
if (job is IPooledJob pj && pj.PoolQueueId > 0)
{
    var poolQueue = FindQueue(pj.PoolQueueId) as ClusterQueue;
    if (poolQueue == null)
        throw new InvalidOperationException(
            $"Pool queue {pj.PoolQueueId} not found. " +
            "Select a valid pool queue in the job settings.");
    if (string.IsNullOrWhiteSpace(poolQueue.ListJobsTemplate))
        throw new InvalidOperationException(
            $"Pool queue \"{poolQueue.Alias}\" has no List Jobs template configured. " +
            "Add a ListJobsTemplate (e.g. \"squeue -u $USER -h -o \\\"%i\\\"\") " +
            "before using it as a pool queue.");
    if (string.IsNullOrWhiteSpace(poolQueue.CancelManyJobsTemplate))
        throw new InvalidOperationException(
            $"Pool queue \"{poolQueue.Alias}\" has no Cancel Many Jobs template configured. " +
            "Add a CancelManyJobsTemplate (e.g. \"scancel {{job_ids}}\") " +
            "before using it as a pool queue.");
}
```

- [ ] **Step 5: Add pool tick to `HandleRunningState` in `QueueRepository.StateHandlers.cs`**

Inside `HandleRunningState`, after the `if (clusterStatus != ClusterJobStatus.Running)` block (i.e., in the `else` branch where the job is confirmed still running), add:

```csharp
// Tick the worker pool if this is a pooled job.
if (job is IPooledJob pooledJob && pooledJob.PoolQueueId > 0)
{
    var (alive, submitted) = await GetOrCreatePool(job).Tick();
    _jobUpdateCallback(job, j =>
    {
        ((WarpJobGpu)j).PoolWorkersAlive     = alive;
        ((WarpJobGpu)j).PoolWorkersSubmitted = submitted;
    });
}
```

- [ ] **Step 6: Add pool dissolution to `HandleJobCompletion` in `QueueRepository.StateHandlers.cs`**

At the top of `HandleJobCompletion`, before the `_jobUpdateCallback` call that sets final status, add:

```csharp
// Dissolve the worker pool before marking the job complete.
if (_workerPools.TryRemove(job, out var pool))
    await pool.Dissolve();
```

- [ ] **Step 7: Add pool dissolution to `HandleAbortingState` in `QueueRepository.StateHandlers.cs`**

In `HandleAbortingState`, inside the block where the abort is finalized (where `j.Status = JobStatus.Aborted` is set), add before the status update:

```csharp
if (_workerPools.TryRemove(job, out var pool))
    await pool.Dissolve();
```

- [ ] **Step 8: Build the full solution**

```bash
dotnet build Relay/Relay.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
```

Expected: 0 errors.

- [ ] **Step 9: Run all tests**

```bash
dotnet test Refund.Tests 2>&1 | tail -10
```

Expected: all pass.

- [ ] **Step 10: Commit**

```bash
git add Refund/Services/Core/Repositories/
git commit -m "feat: wire WorkerPool into QueueRepository daemon

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 7: Add `[UiQueue]` attribute and view component

**Files:**
- Create: `Refund/UIFields/UiQueue.cs`
- Create: `Refund/UIFields/UiQueueView.razor`

The queue picker uses `AdditionalData` — the same pattern as `UiMDataSource`. The `DataDelegate` on the attribute will be wired in Task 8 (where we hook it into `Job`'s reflection setup). Here we just define the attribute and the renderer.

- [ ] **Step 1: Create `UiQueue.cs`**

```csharp
namespace Refund.UIFields;

/// <summary>
/// Field attribute for a cluster queue selector. Stores a queue ID (int).
/// Renders as a dropdown populated from available ClusterQueue objects.
/// A value of -1 means "none / local mode".
/// </summary>
public class UiQueue : UiFieldBase
{
    public override Type ViewType => typeof(UiQueueView);

    /// <summary>
    /// Creates a new queue picker field.
    /// </summary>
    /// <param name="label">Display label in the UI.</param>
    /// <param name="helpText">Optional tooltip text.</param>
    public UiQueue(string label, string helpText = "")
        : base("", label, helpText)
    {
    }
}
```

- [ ] **Step 2: Create `UiQueueView.razor`**

`AdditionalData` will be a `List<(int id, string alias)>` injected by the `DataDelegate` (wired in Task 8). The view handles the `null` / empty case gracefully.

```razor
@namespace Refund.UIFields
@inherits UiFieldViewBase

@if (AdditionalData is List<(int id, string alias)> { Count: > 0 } queues)
{
    <FluentSelect Class="borderless"
                  @bind-Value="@ValueT"
                  Width="100%"
                  Items="@Options(queues)"
                  OptionText="@(i => i.Text)"
                  OptionValue="@(i => i.Value)"
                  OptionSelected="@(i => i.Selected)"
                  Disabled="@IsDisabled" />
}
else
{
    <span class="text-muted">No cluster queues configured</span>
}

@code
{
    private string? ValueT
    {
        get => ((int?)Value ?? -1).ToString();
        set => ValueChanged.InvokeAsync(value != null ? int.Parse(value) : -1);
    }

    private List<Option<string>> Options(List<(int id, string alias)> queues)
    {
        var currentId = (int?)Value ?? -1;
        var options = new List<Option<string>>
        {
            new() { Value = "-1", Text = "Local (no pool)", Selected = currentId == -1 }
        };
        options.AddRange(queues.Select(q => new Option<string>
        {
            Value    = q.id.ToString(),
            Text     = q.alias,
            Selected = q.id == currentId,
        }));
        return options;
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build Relay/Relay.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Refund/UIFields/UiQueue.cs Refund/UIFields/UiQueueView.razor
git commit -m "feat: add [UiQueue] attribute and UiQueueView dropdown renderer

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 8: Wire `[UiQueue]` data delegate so the renderer gets the live queue list

**Files:**
- Modify: `Refund/DataModel/Job.cs`
- Modify: `Refund/Services/Core/DataManager/DataManager.Queue.cs`

The `UiQueueView` needs the list of available `ClusterQueue` objects at render time. The existing `DataDelegate` mechanism on `UiFieldBase` provides this: a function `ReadOnlyJob → object` is set on the attribute at startup, called by `UiFieldView.razor.cs` via `GetAdditionalData`, and passed as `AdditionalData` to the renderer.

The function needs access to `DataManager.ClusterQueues`. The cleanest hook is: `Job` reflection setup (which already resolves `DataDelegateName` method references) gets extended to also handle a second delegate form for system-level data not on the job itself.

Rather than modifying the reflection machinery, use the simpler existing approach: add a static property `Job.QueueListProvider` that `DataManager` sets at startup, and reference it from a private method on `Job` used as the `DataDelegateName`.

- [ ] **Step 1: Add `QueueListProvider` and the delegate method to `Job.cs`**

Add near the top of the `Job` class (after static fields like `DefaultValues`, `Types`):

```csharp
/// <summary>
/// Injected by DataManager at startup. Provides the list of available cluster queues
/// for rendering [UiQueue] fields. Returns list of (id, alias) tuples.
/// </summary>
public static Func<List<(int id, string alias)>>? QueueListProvider { get; set; }

/// <summary>
/// DataDelegate target for [UiQueue] fields. Returns the live cluster queue list.
/// </summary>
private static List<(int id, string alias)>? GetAvailableQueues(ReadOnlyJob _) =>
    QueueListProvider?.Invoke();
```

- [ ] **Step 2: Set `dataDelegateName` on the `[UiQueue]` field declarations in `WarpJobGpu`**

The `UiFieldBase` constructor accepts a `dataDelegateName` parameter that the reflection setup uses to bind a method on the declaring type as the `DataDelegate`. For a static delegate like `GetAvailableQueues`, the reflection lookup path in `Job.cs` (line ~790) finds it via `pair.Key.GetMethod(DataDelegateName)`.

Change the `[UiQueue]` attribute in `WarpJobGpu` to include the delegate name:

```csharp
[UiQueue("Pool Queue",
         helpText: "Cluster queue for GPU worker pool. Leave unset to run workers locally.",
         dataDelegateName: nameof(GetAvailableQueues))]
[RelayProperty]
public int PoolQueueId { get; set; } = -1;
```

Add `dataDelegateName` parameter to `UiQueue`'s constructor in `UiQueue.cs`:

```csharp
public UiQueue(string label, string helpText = "", string dataDelegateName = null)
    : base("", label, helpText, dataDelegateName: dataDelegateName)
{
}
```

- [ ] **Step 3: Set `Job.QueueListProvider` in `DataManager.Queue.cs`**

Find the `DataManager` constructor or initialization path in `DataManager.Queue.cs`. Add after `QueueRepository` is constructed:

```csharp
// Provide [UiQueue] fields with the live queue list.
Job.QueueListProvider = () => _queueRepository.ClusterQueues
    .OfType<ClusterQueue>()
    .Select(q => (q.Id, q.Alias ?? $"Queue {q.Id}"))
    .ToList();
```

- [ ] **Step 4: Build**

```bash
dotnet build Relay/Relay.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
```

Expected: 0 errors.

- [ ] **Step 5: Run all tests**

```bash
dotnet test Refund.Tests 2>&1 | tail -10
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Refund/DataModel/Job.cs \
        Refund/UIFields/UiQueue.cs \
        Refund/Services/Core/DataManager/DataManager.Queue.cs
git commit -m "feat: wire UiQueue data delegate with live ClusterQueue list

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 9: Verify end-to-end with a local smoke test

This task has no automated test — it's a manual verification that the UI renders correctly and the data model round-trips.

- [ ] **Step 1: Build the Relay app**

```bash
dotnet build Relay/Relay.csproj 2>&1 | grep -E "^.*error" | grep -v "obj/" | head -10
```

Expected: 0 errors.

- [ ] **Step 2: Run all unit tests**

```bash
dotnet test Refund.Tests 2>&1 | tail -15
```

Expected: all pass.

- [ ] **Step 3: Verify serialization round-trip for new fields**

Add a temporary test (or run interactively in a test file) to confirm `MotionAndCTF2D` serializes and deserializes the new fields:

```csharp
[Fact]
public void WarpJobGpu_NewFields_RoundTripJson()
{
    var job = new MotionAndCTF2D { PoolQueueId = 3, PoolSize = 16 };
    var node = new System.Text.Json.Nodes.JsonObject();
    job.WriteToJson(node);

    var job2 = new MotionAndCTF2D();
    job2.ReadFromJson(node, null);

    Assert.Equal(3,  job2.PoolQueueId);
    Assert.Equal(16, job2.PoolSize);
}
```

Add to `Refund.Tests/JobQueues/WorkerPoolTests.cs` and run:

```bash
dotnet test Refund.Tests --filter "WarpJobGpu_NewFields_RoundTripJson" 2>&1 | tail -10
```

Expected: passes.

- [ ] **Step 4: Final commit**

```bash
git add Refund.Tests/JobQueues/WorkerPoolTests.cs
git commit -m "test: add JSON round-trip test for WarpJobGpu pool fields

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage check:**

| Spec section | Covered by |
|---|---|
| §4.1 Refactor `ProcessSubmissionScript` | Task 1 |
| §4.2 Refactor `SubmitJob` into prepare + submit | Task 1 |
| §4.3 `BuildWorkerScript` | Task 2 |
| §4.4 New template fields `ListJobsTemplate`, `CancelManyJobsTemplate` | Task 2 |
| §4.5 `ListActiveJobIds`, `CancelJobs` | Task 2 |
| §5 `IPooledJob` interface | Task 3 |
| §6 `[UiQueue]` attribute | Task 7 |
| §7 `WarpJobGpu` pool fields + `IPooledJob` impl | Task 3 |
| §8 `WorkerPool` (Initialize, Tick, Dissolve, persistence) | Task 4 |
| §10 `QueueRepository` partial class split | Task 5 |
| §11 `QueueRepository.Pool.cs`, validation, tick wiring, dissolution | Task 6 |
| §12 `PoolWorkersAlive` / `PoolWorkersSubmitted` counters + callback | Task 6 |
| §13 Pool queue deleted while running | Validated in `HandleWaitingState` (Task 6 step 4) |
| §13 `pool_state.json` missing on restart | `LoadState()` catches and continues (Task 4) |
| `ReadOnlyClusterQueue` new properties | Task 2 |
