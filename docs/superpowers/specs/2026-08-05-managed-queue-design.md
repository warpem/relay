# Locally Managed Queue

**Status:** Approved
**Date:** 2026-08-05
**Scope:** A queue that runs jobs as local processes on the Relay host, admitting them only when CPU cores, memory and GPUs are free. Relay does the scheduling itself, with no external scheduler installed.

## Background

Relay has two execution paths today:

- **`LocalQueue`** — runs `ILocalJob.RunLocal()` in Relay's own process, throttled by a plain
  `SemaphoreSlim(Environment.ProcessorCount)` (`LocalQueue.cs:54`). No notion of memory or GPUs, so
  nothing stops several GPU jobs from thrashing one card.
- **`ClusterQueue`** — composes a submission script and hands it to an external scheduler through
  shell command templates. The scheduler does resource arbitration.

A single-workstation install therefore has no way to serialise GPU work. The workaround is to install
a scheduler (SLURM, or Flux as specced in the README) purely to arbitrate one machine's resources.
For teaching installs and single-workstation users that is a large dependency to take on for a small
need.

This spec adds a third path: Relay spawns the submission script itself, tracks the process, and
admits new jobs only when the resources they ask for are actually free.

### Non-goals

- **Enforcement.** Limits are *accounting*, used for admission decisions. A job that exceeds its
  declared memory is not killed; that needs cgroup v2 delegation and is out of scope. GPU assignment
  is the exception — it is enforced, because `CUDA_VISIBLE_DEVICES` genuinely hides other devices.
- **Surviving a Relay restart.** Managed jobs are children of the Relay process and die with it.
- **Replacing `LocalQueue`.** `ILocalJob` types have no command line to run (`Job.CommandName`
  defaults to `""`); they execute in-process. The managed queue serves the `IClusterJob` types, which
  is what `JobEditor.razor:124` already offers a queue dropdown for.
- **Multi-machine.** One managed queue schedules the Relay host only.

## Where "managed" lives

Add `Managed` to the `ClusterScheduler` enum introduced in `ba63ad50`, and branch at the top of
`ClusterQueue.SubmitJob`, `CheckStatus` and `AbortJob`.

`Managed` is not literally a scheduler — it means "no external scheduler; Relay schedules and
accounts for jobs itself" — and the XML docs must say so. The alternative, a `ManagedQueue :
ClusterQueue` subclass, is cleaner OO but requires polymorphic deserialization: `CreateClusterQueue`
hardcodes `new ClusterQueue(...)` (`QueueRepository.QueueOps.cs:26`), `LoadState` has no type
discriminator, and `ReadOnlyClusterQueue` would need a sibling. That is real work for no
user-visible benefit, and the single dropdown (Slurm / Lsf / Pbs / Sge / Flux / Managed / Custom)
reads naturally to an admin.

The job ID and status parser dictionaries get no `Managed` entry. Both parse methods are guarded so
a `Managed` queue never reaches them.

## Resource model

Three new `[RelayProperty]` integers on `ClusterQueue`, meaningful only when `SchedulerType ==
Managed`:

| Property | Meaning | Default |
|---|---|---|
| `ManagedCores` | Total CPU cores the queue may hand out | `Environment.ProcessorCount` |
| `ManagedMemoryGb` | Total memory the queue may hand out | `64` |
| `ManagedGpus` | Number of GPUs on the host | `1` |

A job's request is derived from the existing per-job properties (`Job.cs:341-365`), whose semantics
are **asymmetric** and must not be conflated:

| Resource | Expression | Why |
|---|---|---|
| Cores | `ProcessCount × CoreCount` | `CoreCount` is documented and used as cores *per process* (`Job.cs:347`); the SLURM template pairs it with `--ntasks-per-node {{ n_processes }}` |
| Memory | `MemoryGb` | Already a total in every override, e.g. `(NGpus * PerDevice) * MemoryPerWorker` (`WarpJobGpu.cs:27`) and `(NProcesses - 1) * MemoryPerWorker` (`Refine3D.cs:103`) |
| GPUs | `GpuCount` | Count of whole devices |

`GpuMemoryGb` is not tracked. Jobs on one host contend for whole devices, not fractions of VRAM.

## The ledger is derived, not incremental

The obvious implementation subtracts on admit and adds back on release. Every exit path — finished,
failed, aborted, killed at shutdown, job deleted mid-flight — must then remember to release. Missing
one wedges the queue permanently with phantom allocations, presenting to the user as "my jobs never
start" with nothing in the logs.

Instead, free resources are **computed on every query**:

```
free = total − Σ (allocations of currently-tracked live entries)
```

Releasing a resource is then not an action anyone can forget; it is what already happened when the
entry left the tracking table. Exactly one code path can leak — the reconciliation pass — and that
path is directly unit-testable.

GPU assignment follows the same rule: the free set is whichever indices no live entry claims.

### Liveness is reconciled against job status, not just process state

Resources are reserved by `TryAdmit` during `CanAdmit`, but the process does not exist until
`SubmitJob`'s staging task has written the script — and `ClusterQueue.SubmitJob` performs staging
inside a `Task.Run` (`ClusterQueue.cs:371`). An entry can therefore legitimately exist with no
`Process` yet, and if staging throws, it would exist with no process *forever*. A reconciliation pass
keyed only on `Process.HasExited` would never drop it, reintroducing exactly the leak this design
exists to prevent.

So an entry is live if and only if:

```
(job.Status.IsUnsettled() || job.Status == JobStatus.Waiting)
    && (entry has no process yet || !entry.Process.HasExited)
```

The status test is the codebase's existing idiom for "active", used verbatim in `JobQueue.cs:129`
and `DataManager.cs:296`; `IsUnsettled()` (`Job.cs:1743`) covers Staging, Running, Finalizing,
Aborting and Clearing.

Job status is authoritative, is maintained by the daemon on every path including failures, and is
already persisted. Reconciling against it means a staging failure needs no explicit release — the
daemon marks the job `Failed`, and the entry stops being live on the next pass. There is still no
release call anyone can forget.

## GPU index assignment

The ledger assigns specific device indices, and `ManagedExecutor` exports them into the child's
environment as `CUDA_VISIBLE_DEVICES=2,5` — set on `ProcessStartInfo.Environment`, never injected
into the script, so the submission template stays scheduler-agnostic and there is no quoting hazard.

CUDA renumbers visible devices to `0..n-1`, which is exactly what Relay's jobs already assume:

- `WarpJobGpu` pool workers pass `--device {deviceIndex}` for `deviceIndex` in `0..n-1`
  (`WarpJobGpu.cs:183`).
- `AlignMiss` states it outright — *"GPUs as indices 0..NGpus-1"* (`AlignMiss.cs:294`) — and composes
  `--training-devices 0` with `--reconstruction-devices 1,1,2,2` on that basis.

So no job needs changing, and on a multi-GPU host `CUDA_VISIBLE_DEVICES` is not merely convenient but
*required*: without it two concurrent jobs would both drive device 0.

## Components

### `ResourceLedger` (`Refund/JobQueues/ResourceLedger.cs`)

Pure logic, no I/O, no `Process`, no `Job`. Fully unit-testable.

- Constructed with totals (cores, memoryGb, gpuCount).
- Holds entries keyed by an opaque token: `{ Cores, MemoryGb, int[] GpuIndices }`.
- `bool TryReserve(int cores, int memoryGb, int gpus, out int[] gpuIndices)` — computes free by
  summation, checks fit, picks the lowest free GPU indices, records the entry.
- `void Release(token)` — removes the entry.
- `bool CanEverFit(int cores, int memoryGb, int gpus)` — compares against *totals*, not free.
- `Free` / `InUse` snapshots for logging and UI.

### `ManagedExecutor` (`Refund/JobQueues/ManagedExecutor.cs`)

Owns process I/O and wraps one `ResourceLedger`.

- `Dictionary<Job, Entry>` where `Entry = { Process?, LedgerToken?, int? ExitCode }`.
- `bool TryAdmit(Job)` — asks the ledger; on success records an entry holding the token, no process
  yet.
- `void Launch(Job, string scriptPath)` — spawns and records the `Process`, sets `ClusterJobId` to
  the PID.
- `void Reap()` — the single reconciliation pass, run at the start of every executor query. Two
  distinct jobs:
  1. **Free resources on process exit.** For an entry whose process has exited, capture `ExitCode`
     and release its ledger token immediately, setting `LedgerToken` to null. Resources are freed
     the moment the process dies, not when finalisation completes — the job may still be in
     `Finalizing` writing output, which needs no cores or GPUs.
  2. **Drop entries that are no longer live**, per the liveness rule above. This is what handles
     staging failures and any other path that never produced a process.
- `ClusterJobStatus GetStatus(Job)` — admitted but not launched → Pending; process running → Running;
  exited 0 → Finished; exited non-zero → Failed; **untracked → Failed**.
- `void Kill(Job)` / `void KillAll()` — `Process.Kill(entireProcessTree: true)`.

An entry outlives its ledger token: once reaped it holds no resources but remains long enough to
report the exit code, and is dropped when its job reaches a terminal state.

The untracked → Failed rule is what makes restart behaviour fall out for free: after a restart the
table is empty, so any job the daemon still believes is Running is reported Failed on its first poll.

### Admission hook

`JobQueue.CanAdmit(Job job)` — `virtual`, returns `true`. `ClusterQueue` overrides it to consult the
executor when `SchedulerType == Managed`.

`HandleWaitingState` (`QueueRepository.StateHandlers.cs:25`) gains one guard, placed after
`IsReadyToStage()` (`Job.cs:596`) and the pool validation, immediately before the transition to
`Staging`:

```csharp
if (!queue.CanAdmit(job))
    return;   // resources busy; the daemon retries next tick
```

`ProcessQueueJobs` iterates one queue's jobs sequentially (`foreach … await ProcessJob`), and only
different queues run in parallel, so admission decisions within a managed queue are already
serialised. No extra locking is needed beyond the executor's own.

**Backfill, not FIFO.** Because the guard is evaluated per job as the daemon walks the queue, a job
that fits is admitted even if an earlier job does not. A CPU job therefore does not idle behind a
queued GPU job. Starvation is not a practical concern on a single workstation.

## Execution

Script composition is reused verbatim. `PrepareAndWriteScript` (`ClusterQueue.cs:418`) already
produces exactly what is needed — `cd` into the run directory, `CommandPrefix`, `CommandName` plus
composed arguments, `CommandSuffix`, all passed through `ProcessSubmissionScript` for module blocks
and `{{ n_cores }}`-style variables. A managed queue's template is the same template with the
`#SBATCH`/`#FLUX` header lines omitted:

```bash
#!/bin/bash
{{ warp }}
ml warptools/latest
{{ /warp }}

umask 007

{{ command }}
```

`SubmitJob` when `Managed` mirrors the existing structure — set `DirectoryName`, transition to
`Staging`, write the script — then instead of `ExecuteOnCluster(SubmitJobTemplate)`:

- `FileName = "/bin/bash"`, `ArgumentList = { scriptPath }`.
- `WorkingDirectory` = the job's run directory.
- `Environment["CUDA_VISIBLE_DEVICES"]` = assigned indices.
- Strip `ASPNETCORE_*` / `Kestrel__` as `ExecuteClusterCommandInternal` already does
  (`ClusterQueue.cs:876-881`).
- `ClusterJobId` = PID as a string.

**stdout/stderr** are redirected through .NET (`RedirectStandardOutput/Error = true`) and pumped to
`job.PathStdOut` / `job.PathStdErr` by a background task that writes and flushes **line by line**.
Shell redirection was rejected because job directory paths would need quoting; `CopyToAsync` was
rejected because its 80 KiB buffer would stall `TrackProgressLogs`, which tails those files for the
UI's live progress.

## Abort, shutdown, restart

- **Abort** — `AbortJob` kills the process tree. `HandleAbortingState` then polls `CheckStatus` as it
  does for cluster jobs and finalises to `Aborted`.
- **Shutdown** — `QueueRepository.Dispose(bool)` (`:316`) calls `KillAll()`. Without this, killing
  Relay would leave orphaned compute processes holding GPUs.
- **Restart** — nothing is re-adopted. Jobs still marked Running are reported Failed on the first
  poll via the untracked rule. This matches `LocalQueue`, which already declines to persist unsettled
  jobs for the same reason (`LocalQueue.cs:216-231`).

Managed queues persist their queued jobs through the base `JobQueue.WriteToJson`, unchanged. Waiting
jobs therefore resume correctly after a restart; Running ones fail fast.

## Rejection policies

- **Impossible requests fail loudly.** If `CanEverFit` is false — a student asking for 4 GPUs on a
  1-GPU box — the job is failed at admission with a message naming the request and the queue's
  totals, rather than waiting forever.
- **Managed queues cannot be worker-pool queues.** Pools submit bare scripts through
  `IPoolQueue.SubmitScript`, which carries no resource request and so cannot be admitted. The pool
  validation already in `HandleWaitingState` (`:36-52`) gains a check that fails the job with a clear
  message.

## UI

In `QueueEditor.razor`, when `SchedulerType == Managed`:

- **Hide** Send command, Submit job, Status job, Abort job, List jobs, Cancel many jobs, and the
  Advanced tab's custom parsing fields (already gated on `Custom`).
- **Show** Cores, Memory (GB), GPUs.
- **Keep** the submission script template — still needed for module blocks and `{{ command }}`.

This follows the `ShowCustomParsingFields` pattern added in `ba63ad50`. A read-only summary of
current utilisation is explicitly *not* in scope for this change.

`ReadOnlyClusterQueue` gains the three properties, hand-written as before — the ReadOnly source
generator only covers `Job` subclasses.

## Backward compatibility

`ClusterScheduler.Managed` is a new non-zero enum member, so no saved queue deserializes into it. The
three resource properties default as tabled above and are ignored unless `Managed` is selected.
`CanAdmit` returns `true` for every non-managed queue, so cluster queues are unaffected.

## Error handling

| Situation | Behaviour |
|---|---|
| Request exceeds queue totals | Job failed at admission, message names request and totals |
| Resources busy | Job stays `Waiting`; retried next daemon tick; no log spam |
| Process fails to spawn | Job failed, exception written to the job's error log |
| Process exits non-zero | Job failed, as with a cluster job |
| Managed queue picked as pool queue | Job failed with a clear message |
| Relay killed | All trees killed via `Dispose` |
| Job untracked but believed Running | Reported Failed |

## Testing

`ResourceLedger` carries the bulk of the tests, and needs no processes:

- Fit and no-fit against each of cores, memory, GPUs independently.
- Releasing an entry restores exactly its resources — asserted by reserving, releasing, and
  re-reserving the same shape.
- **Derived-release property:** dropping an entry without calling `Release` still frees its
  resources, which is the invariant the whole design rests on.
- GPU index assignment is disjoint across concurrent entries, and reuses freed indices.
- `CanEverFit` false for over-large requests even when the ledger is completely empty.
- Cores computed as `ProcessCount × CoreCount`, memory as `MemoryGb` — pinning the asymmetry.

`ManagedExecutor` gets a small number of tests against a trivial real process (`/bin/bash -c "exit
0"` / `"exit 3"` / `sleep`), covering:

- Status mapping for each case, plus the untracked → Failed rule.
- Reap-then-report ordering: an exited process must be reportable as Finished/Failed *after* its
  ledger token has been released.
- **Admission without launch does not leak.** Admit a job, never launch it, drive its status to
  `Failed` as a staging exception would, run `Reap()`, and assert the resources are free again. This
  is the regression test for the hole found in spec review; it must fail against an implementation
  that reconciles only on `Process.HasExited`.

`CanAdmit` returning false must be shown to leave the job in `Waiting` — i.e. that the guard sits
before the `Staging` transition, not after.

## Files touched

| File | Change |
|---|---|
| `Refund/DataModel/JobQueue.cs` | `Managed` enum member; `virtual CanAdmit` |
| `Refund/JobQueues/ResourceLedger.cs` | New |
| `Refund/JobQueues/ManagedExecutor.cs` | New |
| `Refund/JobQueues/ClusterQueue.cs` | Three properties; branch in `SubmitJob`/`CheckStatus`/`AbortJob`; `CanAdmit` override; parser guards |
| `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs` | Expose the three properties |
| `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs` | `CanAdmit` guard; pool-queue rejection |
| `Refund/Services/Core/Repositories/QueueRepository.cs` | `KillAll()` in `Dispose` |
| `Relay/Screens/Overlay/Settings/QueueEditor.razor{,.cs}` | Managed field group |
| `Refund.Tests/JobQueues/ResourceLedgerTests.cs` | New |
| `Refund.Tests/JobQueues/ManagedExecutorTests.cs` | New |
| `README.md` | Managed queue section |

## Risks

- **Memory is advisory.** Two jobs each declaring 32 GB on a 64 GB box will both be admitted and may
  still OOM if their real usage exceeds their declaration. Accepted; enforcement needs cgroups.
- **`Process.HasExited` is the liveness signal.** A process that hangs without exiting holds its
  allocation indefinitely, exactly as a hung cluster job holds its slot. No watchdog in this change.
- **Declared requests may be wrong.** Admission is only as good as each job's resource properties,
  which were written to size scheduler directives and have not previously gated execution. Expect to
  tune some of them once this is in real use.
