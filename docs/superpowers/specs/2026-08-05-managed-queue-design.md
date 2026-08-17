# Locally Managed Queue

**Status:** Approved (revised 2026-08-17 after design review)
**Date:** 2026-08-05
**Review:** `2026-08-05-managed-queue-design-review.md` — all ten findings resolved below; see *Review resolutions*.
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

### `Job.GpuCount` must be corrected first

`Job.GpuCount` returns **1** (`Job.cs:359`) while its own docstring says the default is 0. Only nine
job types override it; everything else — `CreateMask`, `ImportDataSetTs`, `PostProcess`,
`ImportAlignments` and the rest of the CPU-only tools — silently inherits a request for one GPU. On a
managed queue those jobs would either occupy the only GPU or be rejected as impossible on a 0-GPU
queue. Admission is only as trustworthy as this property.

Correcting it must not disturb existing SLURM/Flux queues, whose templates already interpolate
`{{ n_gpus }}`. So the change is sequenced to be behaviour-preserving:

1. **Audit every `IClusterJob` type** for actual GPU usage.
2. **Make the implicit explicit** — write an `override int GpuCount` on every type that currently
   inherits, carrying the value it effectively has today (0 for CPU tools, or the real count where a
   job genuinely uses a GPU without saying so).
3. **Only then flip the base to `0`**, matching the documented contract.

After step 2 no job's effective value depends on the base, so step 3 changes nothing observable.
Step 1 is the safety net: it is what catches a GPU job that has been relying on the implicit 1, which
would otherwise silently lose its GPU request. Any job whose effective count step 1 shows to be
*wrong today* is corrected as its own change, called out separately, not folded in silently.

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

An entry is live if and only if:

```
entry.Process is running
    || ((job.Status.IsUnsettled() || job.Status == JobStatus.Waiting) && entry has no process yet)
```

**A running process always keeps its allocation, whatever the job's status says.** This ordering is
load-bearing. A job can reach a terminal status while its process is still alive:
`HandleAbortingState` force-marks a job `Aborted` after 30 seconds regardless of reported status
(`StateHandlers.cs:327-328`), and daemon error paths can mark a job `Failed` independently. Freeing
the allocation then would hand the GPU to another job while the first is still computing on it.

Reconciliation therefore handles a terminal job with a live process by **killing the tree and
awaiting exit**, releasing only once exit is confirmed. Job status alone can retire only an entry
that has no process — the abandoned-reservation case.

The status test is the codebase's existing idiom for "active", used verbatim in `JobQueue.cs:129`
and `DataManager.cs:296`; `IsUnsettled()` (`Job.cs:1743`) covers Staging, Running, Finalizing,
Aborting and Clearing.

Job status is authoritative for the no-process case, is maintained by the daemon on every path
including failures, and is already persisted. A staging failure therefore needs no explicit release —
the daemon marks the job `Failed`, and the reservation stops being live on the next pass.

### One source of truth

`ManagedExecutor` owns the single entry table. `ResourceLedger` does not keep its own removable
records — it is a calculator over the entries the executor supplies:

```csharp
LedgerSnapshot Compute(Totals totals, IEnumerable<Allocation> liveEntries)
```

An earlier draft gave the ledger its own token-keyed table alongside the executor's dictionary, so
dropping an executor entry without calling `Release` freed nothing and the derived-release property
was unachievable. Two independently removable tables cannot express "derived". With one table,
removal *is* the release.

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

Pure logic, no I/O, no `Process`, no `Job`, and **no mutable state of its own**. Fully unit-testable.

- `Allocation` = `{ int Cores, int MemoryGb, int[] GpuIndices }`.
- `Totals` = `{ int Cores, int MemoryGb, int Gpus }`.
- `LedgerSnapshot Compute(Totals, IEnumerable<Allocation> live)` — free cores/memory and the free
  GPU index set, by summation over what the caller says is live.
- `bool TryFit(Totals, IEnumerable<Allocation> live, Request, out Allocation)` — checks fit against
  the snapshot and picks the lowest free GPU indices.
- `bool CanEverFit(Totals, Request)` — compares against *totals*, not free. Distinguishes "busy now"
  from "impossible".

Totals are passed per call rather than captured at construction, so the ledger has no configuration
lifecycle to get wrong (see *Configuration lifecycle*).

### `ManagedExecutor` (`Refund/JobQueues/ManagedExecutor.cs`)

Owns process I/O and the single entry table. **Exactly one instance exists per Relay host**, created
and owned by `QueueRepository`, not by individual queues.

Per-queue executors would let two managed queues each reserve the whole host and both assign CUDA
device 0 — and the queue editor's one-click "Copy current queue" button makes that a plausible
misconfiguration, not a theoretical one. A host has one set of GPUs, so there is one ledger. In
addition, creating or switching a second queue to `Managed` is rejected in `DataManager.UpdateQueue`
and at load, with a message naming the existing managed queue.

- `Dictionary<Job, Entry>` where `Entry = { Allocation, Process?, int? ExitCode, Task[] Pumps }`.
- `bool TryAdmit(Job)` — asks the ledger over the current live set; on success records an entry with
  an allocation and no process yet.
- `void Launch(Job, string scriptPath)` — spawns and records the `Process`, sets `ClusterJobId` to
  the PID.
- `void Reap()` — the single reconciliation pass, run at the start of every executor query:
  1. **Process exited** → capture `ExitCode`, mark the entry non-live. Resources free the moment the
     process dies, not when finalisation completes — a job in `Finalizing` is writing output and
     needs no cores or GPUs.
  2. **Job terminal but process alive** → kill the tree; the entry stays live and keeps its
     allocation until exit is confirmed on a later pass.
  3. **Job terminal, no process** → abandoned reservation, drop the entry. This is the staging-failure
     path.
- `ClusterJobStatus GetStatus(Job)` — admitted but not launched → Pending; process running → Running;
  **exited but pumps not drained → Running**; exited 0 → Finished; exited non-zero → Failed;
  untracked → Failed.
- `void Kill(Job)` / `Task KillAllAsync()` — `Process.Kill(entireProcessTree: true)`, then await exit.

Terminal status is withheld until both output pumps have completed and their writers are disposed.
`Process.HasExited` can go true while the stdout/stderr pumps still hold buffered output, and
`HandleJobCompletion` runs final progress tracking and then dequeues — so reporting Finished too
early can drop a job's last log lines. Resources are still released at process exit; only the
*status* waits.

An entry outlives its allocation: once reaped it holds no resources but remains long enough to report
the exit code, and is dropped when its job reaches a terminal state.

The untracked → Failed rule is what makes restart behaviour fall out for free: after a restart the
table is empty, so any job the daemon still believes is Running is reported Failed on its first poll.

### Admission hook

A boolean cannot carry the needed distinction. `false` means "retry next tick", but an impossible
request must fail permanently — and throwing does not achieve that either, because
`HandleWaitingState`'s catch writes the error log and returns *without* changing status
(`StateHandlers.cs:70-75`), leaving the job `Waiting` and re-logging every daemon tick forever.

```csharp
abstract record AdmissionResult
{
    sealed record Admit()               : AdmissionResult;
    sealed record Busy()                : AdmissionResult;
    sealed record Reject(string Reason) : AdmissionResult;
}
```

`JobQueue.CanAdmit(Job job)` — `virtual`, returns `Admit`. `ClusterQueue` overrides it to consult the
executor when `SchedulerType == Managed`.

`HandleWaitingState` (`QueueRepository.StateHandlers.cs:25`) gains one guard, placed after
`IsReadyToStage()` (`Job.cs:596`) and the pool validation, immediately before the transition to
`Staging`:

```csharp
switch (queue.CanAdmit(job))
{
    case AdmissionResult.Busy:
        return;                              // retried next tick, silently
    case AdmissionResult.Reject r:
        await job.WriteToErrorLog(r.Reason); // once
        _jobUpdateCallback(job, j => { j.Status = JobStatus.Failed; j.AddEvent(EventType.Failed); });
        return;
}
```

`Reject` must produce exactly one failure event and message, not one per tick — which the transition
to `Failed` guarantees, since the job leaves `Waiting`.

`ProcessQueueJobs` iterates one queue's jobs sequentially (`foreach … await ProcessJob`), and only
different queues run in parallel, so admission decisions within a managed queue are already
serialised. No extra locking is needed beyond the executor's own.

**Backfill, not FIFO.** Because the guard is evaluated per job as the daemon walks the queue, a job
that fits is admitted even if an earlier job does not. A CPU job therefore does not idle behind a
queued GPU job.

Backfill has **no starvation guarantee**, and this change does not add one. On a multi-GPU host a
large request can wait indefinitely while a stream of smaller jobs keeps at least one device busy.
On single-GPU hosts — the case this feature targets — a request larger than the host is rejected by
`CanEverFit` rather than queued, so the scenario cannot arise. Aging or reservation-after-threshold
is the remedy if this becomes a real complaint; it is deliberately out of scope. Recorded under
*Risks*.

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

Child processes do **not** die with their parent on Linux. `Process.Start` gives no containment, so
"managed jobs die with Relay" is a property that has to be built, not assumed. Worse, the hook the
first draft named does not run at all: `QueueRepository.Dispose(bool)` (`:316`) is unreachable
because `DataManager` implements no disposal across any of its twelve partial files and is
registered as `AddSingleton<DataManager>(new DataManager(relayOptions))` (`Program.cs:88`) — an
externally-constructed instance, which the DI container does not dispose. There are no
`ApplicationStopping` hooks.

Containment therefore has three parts:

**1. Process groups.** Each job is spawned via `setsid` into its own process group, so the whole tree
— including `mpirun` children — can be signalled by group id even without a live `Process` handle.

**2. A shutdown hook that exists.** `Program.cs` registers an `IHostApplicationLifetime.
ApplicationStopping` callback that calls a new `DataManager.ShutdownAsync()`, which reaches
`QueueRepository` and awaits `KillAllAsync()`. Shutdown is ordered:

1. Close admission — `TryAdmit` returns `Busy` from here on.
2. Cancel and await every in-flight staging task, and make `Launch` a no-op once shutdown has begun.
3. Kill all process groups and await their exit.

Steps 1–2 close the race where an admitted entry has no `Process` yet because its fire-and-forget
staging task is still writing the script: `KillAll` would find nothing to kill, return, and the task
would then launch a process *during* shutdown. `ClusterQueue` already keeps a per-job
`CancellationTokenSource` in `StagingJobs` (`:42`), which this reuses.

**3. Kill leftovers at startup.** Graceful shutdown cannot cover a crash or `SIGKILL`. The executor
persists `{ jobId, pid, pgid, startTimeTicks }` to a file under the queue state directory on every
launch and reap. At startup, before any job is admitted, each recorded group whose pid **and start
time** still match a live process is killed; the file is then cleared. Matching on start time as well
as pid is what makes this safe against pid recycling.

- **Abort** — `AbortJob` kills the process group. `HandleAbortingState` then polls `CheckStatus` as
  it does for cluster jobs and finalises to `Aborted`.
- **Restart** — nothing is re-adopted; leftovers are killed as above. Jobs still marked Running are
  reported Failed on the first poll via the untracked rule. This matches `LocalQueue`, which already
  declines to persist unsettled jobs for the same reason (`LocalQueue.cs:216-231`).

Managed queues persist their queued jobs through the base `JobQueue.WriteToJson`, unchanged. Waiting
jobs therefore resume correctly after a restart; Running ones fail fast.

The persistence this needs — pid plus start time — is most of what re-adoption would require. If
re-adoption is wanted later, it is an increment on this file rather than a new mechanism.

## Configuration lifecycle

`ClusterQueue` is constructed before `ReadFromJson` hydrates persisted properties, and the editor can
change totals or copy a queue at any time. The ledger avoids the problem structurally by taking
totals as a parameter on every call rather than capturing them at construction, so there is nothing
to initialise eagerly and nothing to rebuild on edit — each admission simply reads the queue's
current values.

Changing `ManagedCores` / `ManagedMemoryGb` / `ManagedGpus`, or switching `SchedulerType` away from
`Managed`, is **rejected while the executor holds live entries for that queue**, with a message
naming the running jobs. This avoids having to define behaviour for totals dropping below current
usage. Lowering totals with an idle queue is always allowed.

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

`ManagedExecutor` gets tests against trivial real processes (`/bin/bash -c "exit 0"` / `"exit 3"` /
`sleep`), covering:

- Status mapping for each case, plus the untracked → Failed rule.
- Reap-then-report ordering: an exited process must be reportable as Finished/Failed *after* its
  allocation has been released.
- **Admission without launch does not leak.** Admit a job, never launch it, drive its status to
  `Failed` as a staging exception would, reap, assert resources free. Must fail against an
  implementation that reconciles only on `Process.HasExited`.
- **A terminal job with a live process keeps its allocation.** Launch a `sleep`, mark the job
  `Failed` underneath it, reap, and assert the resources are *still* held and the process was
  killed — then that they free once exit is confirmed. Must fail against a rule that retires entries
  on status alone.
- **Terminal status waits for the pumps.** A process emitting output then exiting is not reported
  Finished until both pumps have drained; the output file contains everything written.
- **Shutdown cannot launch.** Begin shutdown while an entry is admitted-but-not-launched; assert no
  process is ever spawned and admission returns `Busy`.
- **Startup kills leftovers.** Given a persisted record whose pid and start time match a live
  process, startup kills it; given a record whose start time does not match (pid recycled), startup
  leaves that process alone.

Admission and job-type tests:

- `Reject` produces exactly one failure event and error-log entry, not one per daemon tick, and moves
  the job out of `Waiting`.
- `Busy` leaves the job in `Waiting` — i.e. the guard sits before the `Staging` transition.
- Two managed queues cannot be configured; the second is rejected.
- Editing totals is rejected while the queue has live entries and allowed when idle.
- **CPU-only job types request zero GPUs**, including types that inherit base resource properties —
  this is the regression guard for the `Job.GpuCount` sequence above.

`CanAdmit` returning false must be shown to leave the job in `Waiting` — i.e. that the guard sits
before the `Staging` transition, not after.

## Files touched

| File | Change |
|---|---|
| `Refund/DataModel/Job.cs` | `GpuCount` base default 1 → 0, after the audit makes it explicit everywhere |
| `Refund/Jobs/**` | Explicit `GpuCount` overrides on types that currently inherit |
| `Refund/DataModel/JobQueue.cs` | `Managed` enum member; `virtual CanAdmit`; `AdmissionResult` |
| `Relay/Program.cs` | `ApplicationStopping` hook calling `DataManager.ShutdownAsync()` |
| `Refund/Services/Core/DataManager/DataManager.Queue.cs` | Reject a second managed queue; reject total edits while busy |
| `Refund/JobQueues/ResourceLedger.cs` | New |
| `Refund/JobQueues/ManagedExecutor.cs` | New |
| `Refund/JobQueues/ClusterQueue.cs` | Three properties; branch in `SubmitJob`/`CheckStatus`/`AbortJob`; `CanAdmit` override; parser guards |
| `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs` | Expose the three properties |
| `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs` | `CanAdmit` guard; pool-queue rejection |
| `Refund/Services/Core/Repositories/QueueRepository.cs` | Own the single `ManagedExecutor`; `KillAllAsync`; startup leftover kill |
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
  tune some of them once this is in real use. The `GpuCount` audit is the first instalment of this.
- **Backfill can starve large jobs on multi-GPU hosts.** Accepted, not mitigated — see *Admission
  hook*. Cannot occur on the single-GPU hosts this targets.
- **A crash between launch and the persistence write leaks one process.** The record is written
  immediately after spawn, so the window is small but non-zero. A leftover process holding a GPU
  makes subsequent jobs wait or be rejected until it is killed by hand.

## Review resolutions

Findings from `2026-08-05-managed-queue-design-review.md`, all verified against the codebase before
being accepted:

| # | Finding | Resolution |
|---|---|---|
| P1.1 | Release while process runs | Liveness rule inverted — a running process always keeps its allocation; reconciliation kills and awaits exit |
| P1.2 | Per-queue ledgers | One host-wide executor owned by `QueueRepository`; second managed queue rejected |
| P1.3 | Children survive Relay | `setsid` groups + persisted pid/start-time + startup kill + a shutdown hook that actually runs |
| P1.4 | `KillAll` races staging | Ordered shutdown: close admission, cancel and await staging, then kill and await |
| P1.5 | `GpuCount` default is 1 | Audit, make explicit, then flip base to 0 — behaviour-preserving in that order |
| P2.6 | Config lifecycle | Totals passed per call; edits rejected while entries are live |
| P2.7 | Boolean admission result | `AdmissionResult` = `Admit` / `Busy` / `Reject(reason)`; handler transitions to `Failed` |
| P2.8 | Ledger still incremental | Ledger made a pure calculator; executor owns the single table |
| P2.9 | Drain output before completion | Terminal status withheld until both pumps complete |
| P2.10 | Backfill starvation | Documented as accepted risk rather than mitigated — remedy noted, out of scope |

P2.10 is the one taken at lower strength than proposed: the review offered aging, bounded backfill,
or explicit documentation, and this takes the third. The scenario needs a multi-GPU host, which is
not the target of this change.
