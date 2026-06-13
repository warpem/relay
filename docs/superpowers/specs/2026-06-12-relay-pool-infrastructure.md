# Relay Pool Infrastructure — Design Spec

**Status:** Draft  
**Date:** 2026-06-12  
**Scope:** Relay (`Refund/`) only. WarpTools filesystem work distribution (Phase 1 & 2) is a prerequisite but not modified here.

---

## 1. Motivation

WarpTools' filesystem-based work distribution (spec: `warp_fixnoise2map/docs/superpowers/specs/2026-06-03-filesystem-work-distribution.md`) separates a processing job into two cluster presences:

- **Manager** — a single CPU cluster job that populates the task queue, runs the scheduler loop, and exits when the queue is drained.
- **Worker pool** — N short-lived GPU cluster jobs that consume tasks from the shared filesystem queue.

Relay already submits the Manager as a regular cluster job. What is missing is everything for the pool: submitting and replenishing worker jobs, detecting when workers die, dissolving the pool when the Manager ends, and doing all of this without polling each of 200+ workers individually.

This spec defines the Relay-side infrastructure to support pooled WarpTools jobs.

---

## 2. Design principles

- **Pool is a fleet, not a job list.** Individual worker status doesn't matter; only "how many are alive right now?" does. Relay does not track workers as first-class `Job` objects.
- **Batch scheduler operations.** One `ListActiveJobIds()` call per tick regardless of pool size. One `CancelJobs()` call to dissolve. Individual per-worker polling is only a fallback for queues that don't configure batch templates.
- **Manager submission unchanged.** The Manager goes through the existing `ClusterQueue.SubmitJob` flow. Only the worker pool introduces new mechanics.
- **Re-adoption on restart.** Pool state is persisted to disk. A Relay restart re-adopts a running pool without disrupting workers or the Manager.
- **Pool belongs to `QueueRepository`.** Workers are queue infrastructure. No separate service; `QueueRepository` is split into partial class files to stay manageable.
- **Backward compatible.** Every new field is optional. Existing queues and non-pooled jobs require no changes.

---

## 3. Component overview

```
QueueRepository
  ├── QueueRepository.cs             — constructor, fields, LoadQueues, FindQueue, auto-save, dispose
  ├── QueueRepository.QueueOps.cs    — CreateClusterQueue, UpdateQueue, Delete, Queue/Dequeue, Reorder
  ├── QueueRepository.Daemon.cs      — daemon timer, RunDaemonAsync, ProcessQueueJobs, TrackJobProgress
  ├── QueueRepository.StateHandlers.cs — Handle*State methods
  └── QueueRepository.Pool.cs        — _workerPools dict, GetOrCreatePool, Tick/Dissolve wiring,
                                        re-adoption in LoadQueues

Refund/JobQueues/
  ├── ClusterQueue.cs                — + ListActiveJobIds(), CancelJobs(), BuildWorkerScript(),
  │                                      SubmitScript(); refactored SubmitJob, ProcessSubmissionScript
  └── WorkerPool.cs                  — fleet state machine for one pooled Relay job

Refund/DataModel/
  └── Job.cs                         — IPooledJob interface

Refund/Jobs/Abstract.cs             — WarpJobGpu gains PoolQueueId + PoolSize fields

Refund/UIFields/
  └── UiQueueAttribute.cs           — new [UiQueue] attribute + renderer
```

---

## 4. `ClusterQueue` refactoring and additions

### 4.1 Refactor `ProcessSubmissionScript`

Currently takes a `Job` and calls `job.GetResourceValues()` / `job.RequiredModules` internally. Change the signature to take explicit dictionaries:

```csharp
protected string ProcessSubmissionScript(
    string scriptTemplate,
    Dictionary<string, string> resourceValues,
    string[] requiredModules,
    Dictionary<string, string> customValues = null)
```

The existing `SubmitJob` call site passes `job.GetResourceValues()` and `job.RequiredModules` — identical behavior. Pool worker submission passes its own explicit values. No logic is duplicated between the two paths.

### 4.2 Refactor `SubmitJob` into prepare + submit

Split the current monolithic `SubmitJob` into two methods and a thin wrapper:

```csharp
// New: everything up to and including writing the .sh file to disk.
// Returns the absolute path to the written script.
private async Task<string> PrepareAndWriteScript(
    Job job,
    Dictionary<string, string> customValues = null)

// New: executes SubmitJobTemplate with the script path, parses and returns the cluster job ID.
// Used by both regular job submission and worker pool submissions.
public async Task<string> SubmitScript(string scriptPath)

// Existing signature preserved — now a two-line wrapper.
public override void SubmitJob(Job job, Dictionary<string, string> customValues = null)
{
    base.SubmitJob(job);
    // fire-and-forget Task.Run as today, but internally calls:
    //   string scriptPath = await PrepareAndWriteScript(job, customValues);
    //   string clusterId  = await SubmitScript(scriptPath);
}
```

### 4.3 New method: `BuildWorkerScript`

Builds and writes a worker submission script without a `Job` object. Called by `WorkerPool.Initialize()`.

```csharp
public string BuildWorkerScript(
    string command,
    Dictionary<string, string> resourceValues,
    string[] requiredModules,
    string scriptPath)
```

Calls `ProcessSubmissionScript(SubmissionScriptTemplate, resourceValues, requiredModules)` then writes the result to `scriptPath`. Returns `scriptPath`.

### 4.4 New template fields

Two new optional `[RelayProperty]` fields:

```csharp
/// <summary>
/// Command that returns one active cluster job ID per line for the submitting user.
/// Example SLURM: squeue -u $USER -h -o "%i"
/// If empty, pool falls back to individual CheckStatus calls per worker.
/// </summary>
[RelayProperty]
public string ListJobsTemplate { get; set; } = "";

/// <summary>
/// Command to cancel multiple jobs in one call.
/// Supports {{job_ids}} placeholder (space-separated IDs).
/// Example SLURM: scancel {{job_ids}}
/// If empty, pool falls back to individual AbortJob calls.
/// </summary>
[RelayProperty]
public string CancelManyJobsTemplate { get; set; } = "";
```

### 4.5 New methods: `ListActiveJobIds` and `CancelJobs`

```csharp
// Returns the set of currently active cluster job IDs for this queue's user.
// Uses ListJobsTemplate if set; otherwise returns null (caller falls back).
public async Task<HashSet<string>?> ListActiveJobIds()

// Cancels all provided IDs in one call (CancelManyJobsTemplate) or loops AbortJob.
public async Task CancelJobs(IEnumerable<string> jobIds)
```

`ListActiveJobIds` uses the existing `ExecuteOnCluster` and `_clusterCommandSemaphore`. It splits output by newline, trims whitespace, and returns the set. Returns `null` (not empty set) when `ListJobsTemplate` is unset — callers distinguish "unset" from "zero alive".

---

## 5. `IPooledJob` interface

Defined in `Refund/DataModel/Job.cs` alongside `ILocalJob` and `IClusterJob`:

```csharp
public interface IPooledJob
{
    /// <summary>ID of the ClusterQueue to use for worker pool submissions.</summary>
    int PoolQueueId { get; }

    /// <summary>Target number of simultaneously running worker jobs.</summary>
    int PoolSize { get; }

    /// <summary>
    /// Maximum total worker submissions across the job's lifetime.
    /// Caps the sick-worker replacement spiral. Default: 2 × PoolSize.
    /// </summary>
    int PoolSubmissionCap { get; }

    /// <summary>Memory in GB to request per worker cluster job.</summary>
    int WorkerMemoryGb { get; }

    /// <summary>CPU cores to request per worker cluster job.</summary>
    int WorkerCoreCount { get; }

    /// <summary>Required modules for worker cluster jobs (e.g. ["gpu", "warp"]).</summary>
    string[] WorkerRequiredModules { get; }

    /// <summary>
    /// Full command string for one worker assigned to the given GPU device index.
    /// Example: "WarpWorker2 --task_dir /data/tasks --device 2"
    /// </summary>
    string GetWorkerCommand(int deviceIndex);
}
```

---

## 6. `[UiQueue]` attribute

New `UiQueueAttribute` in `Refund/UIFields/UiQueueAttribute.cs`. Follows the same pattern as `UiBoolAttribute`, `UiIntAttribute`, etc.:

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class UiQueueAttribute : UiFieldBase
{
    public UiQueueAttribute(string label, string helpText = "") { ... }
}
```

Stores a `ClusterQueue` ID as an `int` `[RelayProperty]`. The UI renderer (wherever queue-typed fields appear in the job configurator) populates options from `DataManager.ClusterQueues`. Renders as a dropdown. A value of `-1` means "none / local mode".

---

## 7. `WarpJobGpu` additions

Two new fields on `WarpJobGpu` in `Refund/Jobs/Abstract.cs`, appended to the "Resources" `UiFieldGroup`:

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
```

`WarpJobGpu` implements `IPooledJob` when `PoolQueueId > 0`:

```csharp
// IPooledJob
int IPooledJob.PoolQueueId         => PoolQueueId;
int IPooledJob.PoolSize            => PoolSize;
int IPooledJob.PoolSubmissionCap   => PoolSize * 2;
int IPooledJob.WorkerMemoryGb      => MemoryPerWorker;
int IPooledJob.WorkerCoreCount     => 2;   // one per worker process; can be overridden
string[] IPooledJob.WorkerRequiredModules => RequiredModules;

string IPooledJob.GetWorkerCommand(int deviceIndex) =>
    $"WarpWorker2 --task_dir {Path.Combine(DirectoryPath, "tasks")} --device {deviceIndex}";
```

`ComposeCommandArguments()` in `WarpJobGpu` appends `--external_provisioner` when `PoolQueueId > 0`, which tells WarpTools to use the `ExternalProvisioner` (no-op) instead of the `LocalProvisioner`. When `PoolQueueId == -1` the flag is absent and WarpTools spawns local workers as today.

---

## 8. `WorkerPool`

Lives in `Refund/JobQueues/WorkerPool.cs`. One instance per active pooled Relay job, owned by `QueueRepository.Pool.cs`.

### 8.1 State

```csharp
private readonly ClusterQueue    _poolQueue;
private readonly IPooledJob      _job;           // the Relay job (also a Job for DirectoryPath)
private readonly HashSet<string> _submittedIds = new();  // all IDs ever submitted this run
private          HashSet<string> _aliveIds     = new();  // last reconciled alive set
private int    _totalSubmissions;
private bool   _initialized;
private string _workerScriptPath;               // set by Initialize(), used by Tick()
```

### 8.2 `Initialize()`

Called once before the first `Tick()`. Idempotent — safe to call again on re-adoption.

1. Creates `job.DirectoryPath/worker_logs/` directory.
2. Sets `_workerScriptPath = job.DirectoryPath/worker_submit.sh`. Calls `_poolQueue.BuildWorkerScript(...)` to write the script there. The command comes from `_job.GetWorkerCommand(0)` — pool jobs are always single-GPU; the device index is embedded in the command, not the script filename.
3. Loads persisted state from `job.DirectoryPath/pool_state.json` if present (re-adoption path), restoring `_submittedIds` and `_totalSubmissions`.
4. Sets `_initialized = true`.

### 8.3 `Tick()`

Called once per daemon tick while the Manager's `ClusterJobStatus == Running`. Returns `(aliveCount, totalSubmissions)` so `QueueRepository.Pool.cs` can push updated counters to `_jobUpdateCallback`.

```
1. active = await _poolQueue.ListActiveJobIds()
   if active == null:
       // ListJobsTemplate not configured — fall back to individual checks.
       // ReconcileIndividually calls CheckStatus concurrently on each known
       // submitted ID and returns the subset still Pending or Running.
       active = await ReconcileIndividually(_submittedIds, _poolQueue)

2. _aliveIds = _submittedIds ∩ active   // filter to this pool's workers only

3. deficit   = max(0, _job.PoolSize − _aliveIds.Count)
   canSubmit = max(0, _job.PoolSubmissionCap − _totalSubmissions)
   toSubmit  = min(deficit, canSubmit)

4. For i in 0..toSubmit−1:
       id = await _poolQueue.SubmitScript(_workerScriptPath)
       _submittedIds.Add(id)
       _aliveIds.Add(id)
       _totalSubmissions++

5. await PersistState()   // atomic temp-write + rename; alive set NOT persisted

6. return (_aliveIds.Count, _totalSubmissions)
```

### 8.4 `Dissolve()`

Called when the Manager job ends for any reason (Finished, Failed, Aborted).

```
1. await _poolQueue.CancelJobs(_aliveIds)
2. _aliveIds.Clear()
3. _submittedIds.Clear()
4. Delete pool_state.json if it exists
```

### 8.5 Persistence format

`job.DirectoryPath/pool_state.json`:

```json
{
  "pool_queue_id":      3,
  "submitted_ids":      ["11234", "11235", "11236"],
  "total_submissions":  12
}
```

On re-adoption (Relay restart): `_submittedIds` is restored from `submitted_ids`. `_aliveIds` is left empty — the first `Tick()` reconciles it against the live scheduler. `_totalSubmissions` is restored so the submission cap remains meaningful.

### 8.6 Sick-worker accounting

The WarpTools Manager reports sick-worker counts on stdout, which Relay already parses via `TrackProgressLogs`. No additional Relay-side logic is needed here — the submission cap (`PoolSubmissionCap = 2 × PoolSize`) is the circuit breaker on the Relay side, and the Manager's own blacklist logic is the circuit breaker on the Warp side.

---

## 10. `QueueRepository` partial class split

`QueueRepository.cs` is split into five partial class files. All fields and state remain in the primary file.

| File | Contents |
|---|---|
| `QueueRepository.cs` | Constructor, all fields, `LoadQueues`, `FindQueue`, `SaveQueues`, auto-save timer, `Dispose` |
| `QueueRepository.QueueOps.cs` | `CreateClusterQueue`, `UpdateQueue`, `DeleteClusterQueue`, `QueueLocalJob`, `QueueClusterJob`, `DequeueLocalJob`, `DequeueClusterJob`, `ReorderClusterQueue` |
| `QueueRepository.Daemon.cs` | `StartDaemon`, `StopDaemon`, `RunDaemon`, `RunDaemonAsync`, `ProcessQueueJobsThrottled`, `ProcessQueueJobs`, `ProcessJob`, `TrackJobProgress`, `TrackProgressWithThrottling` |
| `QueueRepository.StateHandlers.cs` | `HandleWaitingState`, `HandleStagingState`, `HandleRunningState`, `HandleJobCompletion`, `HandleAbortingState`, `HandleFinalizingState` |
| `QueueRepository.Pool.cs` | `_workerPools` dictionary, `GetOrCreatePool`, pool re-adoption logic, `Tick`/`Dissolve` call sites |

---

## 11. `QueueRepository.Pool.cs`

### Fields (declared in `QueueRepository.cs`)

```csharp
private readonly ConcurrentDictionary<Job, WorkerPool> _workerPools = new();
```

### `GetOrCreatePool(Job job)`

```csharp
private WorkerPool GetOrCreatePool(Job job)
{
    return _workerPools.GetOrAdd(job, j =>
    {
        var pooledJob = (IPooledJob)j;
        var poolQueue = (ClusterQueue)FindQueue(pooledJob.PoolQueueId)
                        ?? throw new Exception($"Pool queue {pooledJob.PoolQueueId} not found");
        var pool = new WorkerPool(poolQueue, pooledJob);
        pool.Initialize();
        return pool;
    });
}
```

### Re-adoption in `LoadQueues`

After the existing job restoration loop, for each job that is both `IPooledJob` and has status `Running` or `Staging`:

```csharp
if (job is IPooledJob && job.Status is JobStatus.Running or JobStatus.Staging)
{
    var poolQueue = FindQueue(((IPooledJob)job).PoolQueueId) as ClusterQueue;
    if (poolQueue != null)
    {
        var pool = new WorkerPool(poolQueue, (IPooledJob)job);
        pool.Initialize();   // loads pool_state.json if present
        _workerPools[job] = pool;
    }
}
```

### Wiring into state handlers

In `HandleRunningState` (in `QueueRepository.StateHandlers.cs`), after the existing `CheckStatus` call returns `Running`:

```csharp
if (job is IPooledJob pooledJob && pooledJob.PoolQueueId > 0
    && clusterStatus == ClusterJobStatus.Running)
{
    var (alive, submitted) = await GetOrCreatePool(job).Tick();
    _jobUpdateCallback(job, j =>
    {
        ((WarpJobGpu)j).PoolWorkersAlive     = alive;
        ((WarpJobGpu)j).PoolWorkersSubmitted = submitted;
    });
}
```

In `HandleJobCompletion`, before dequeuing:

```csharp
if (_workerPools.TryRemove(job, out var pool))
    await pool.Dissolve();
```

In `HandleAbortingState`, before the abort finalizes:

```csharp
if (_workerPools.TryRemove(job, out var pool))
    await pool.Dissolve();
```

---

## 12. UI — pool status display

The Manager job card shows two additional counters when the job is pooled and running:

- **Workers running** — `PoolWorkersAlive`: how many pool workers the last `Tick()` confirmed alive.
- **Workers submitted** — `PoolWorkersSubmitted`: cumulative submissions since the job started (useful for spotting a sick-worker churn scenario approaching the cap).

Two new `[RelayProperty]` `[Clearable]` fields on `WarpJobGpu`:

```csharp
[RelayProperty] [Clearable] public int PoolWorkersAlive     { get; set; }
[RelayProperty] [Clearable] public int PoolWorkersSubmitted { get; set; }
```

Updated via `_jobUpdateCallback` after every `Tick()` in `QueueRepository.Pool.cs` (see wiring in §11) — same pattern as `NItemsProcessed`.

The queue picker field (`PoolQueueId`) renders in the job configurator using the `[UiQueue]` renderer, which populates options from the available `ClusterQueue` objects. When set to `-1` (default), the field shows "Local (no pool)". The `PoolSize` field is greyed out when `PoolQueueId == -1` since it has no effect.

---

## 13. Failure modes

| Failure | Handling |
|---|---|
| Worker exits normally (queue empty / heartbeat stall) | Removed from `_aliveIds` on next `Tick()` reconciliation; replaced if deficit exists and cap not reached. |
| Worker preempted / walltime | Same as above — cluster removes it from the active list. |
| All workers on bad node go sick | `_aliveIds` shrinks each tick; replacement submissions eventually hit `PoolSubmissionCap`. Pool runs under-strength but doesn't spiral. Manager's own blacklist prevents tasks from being assigned to that host. |
| Manager job ends normally | `HandleJobCompletion` → `pool.Dissolve()` → `CancelJobs` on all alive workers. |
| Manager job fails | Same path — `HandleJobCompletion` is called for both Finished and Failed. |
| Relay restarts mid-run | `LoadQueues` re-adopts from `pool_state.json`. First `Tick()` reconciles live workers. Pool continues without interruption. |
| Relay restart + Manager already dead | Manager's `ClusterJobStatus` is `Finished`/`Failed` on first daemon tick → `HandleJobCompletion` → `pool.Dissolve()`. Workers already orphaned by manager heartbeat stall (30 s) will have self-exited before this runs. |
| `pool_state.json` missing on restart | `WorkerPool.Initialize()` starts fresh — no `_submittedIds`, no cap history. Workers that are still running are invisible to the pool; they self-terminate when Manager heartbeat resumes (or stalls). New workers are submitted as needed. Worst case: brief over-provisioning until orphaned workers exit. |
| Pool queue deleted while job is running | `GetOrCreatePool` would fail to `FindQueue`. Guard: validate `PoolQueueId` is still present in `DeleteClusterQueue` and refuse deletion if any running job references that queue. |
| `ListJobsTemplate` not configured on queue | `ListActiveJobIds()` returns `null`; `WorkerPool.Tick()` falls back to `ReconcileIndividually()` which checks each known alive ID via the existing `CheckStatus`. No batching, but correct. |

---

## 14. What this spec does NOT include

- **Tilt-series or other non-frame-series job types** — `IPooledJob` is defined generically; adding it to tilt-series job types is straightforward once their WarpTools commands are ported.
- **Per-worker log viewing in the UI** — logs land in `worker_logs/` on disk; Relay does not tail them. User browses them manually if needed.
- **Dynamic pool resizing** — `PoolSize` is fixed for the life of the job. Changing it mid-run is out of scope.
- **`[UiQueue]` renderer implementation** — the attribute and data model are specified here; the Blazor renderer is a UI implementation detail.