# Review: Locally Managed Queue

**Reviewed spec:** `2026-08-05-managed-queue-design.md`  
**Review date:** 2026-08-17  
**Recommendation:** Revise before implementation

## Summary

The overall direction is sound, but the design currently has five high-priority issues that can
cause resource over-allocation or leave compute processes running without Relay tracking them:

1. Reconciliation can release resources while a process is still running.
2. Per-queue ledgers do not arbitrate resources across multiple managed queues on the same host.
3. Plain child processes do not reliably die when Relay crashes or is forcibly terminated.
4. Shutdown can race with staging and allow a process to launch after `KillAll()`.
5. The current `Job.GpuCount` default makes CPU jobs request a GPU.

The remaining findings concern configuration lifecycle, admission-result semantics, the ledger's
claimed invariant, output draining, and starvation under backfill.

## Findings

### [P1] Do not release resources for a running process

The liveness rule in lines 102–107 drops an entry when the `Job` becomes terminal even if its
`Process` is still running. Existing daemon error paths can independently mark a job `Failed`, and a
post-spawn staging error can do the same. `Reap()` would then release the GPU and CPU allocation while
the child continues executing, allowing another job onto the same resources.

Terminal entries with no process can be treated as abandoned reservations. If an entry has a running
process, however, reconciliation should terminate and await that process while retaining its
allocation until exit is confirmed.

### [P1] Resource accounting must be host-wide

The design gives each managed `ClusterQueue` its own `ManagedExecutor` and `ResourceLedger`, while the
repository processes different queues concurrently. Two managed queues can therefore each reserve
the entire host and both assign CUDA device 0.

Use one host-wide resource manager shared by every managed queue, enforce that only one managed queue
can exist, or configure explicit non-overlapping partitions. A partitioning design must identify
concrete GPU indices rather than only a GPU count.

### [P1] Child processes do not die with Relay automatically

The restart assumptions in lines 232–240 do not follow from `Process.Start`. A normal child generally
survives a parent crash or forcible termination; `QueueRepository.Dispose()` only covers graceful
shutdown. After restart, the proposed untracked rule would mark the job `Failed` while the orphan
could continue consuming its GPU.

Reliable containment needs an operating-system mechanism such as cgroups or systemd scopes, Linux
parent-death signaling, or Windows Job Objects. Alternatively, persist enough process identity to
recover and terminate or re-adopt children after restart. If neither is in scope, the spec should not
claim that managed jobs die with Relay.

### [P1] `KillAll()` races with staging tasks

An admitted entry may have no `Process` while the fire-and-forget staging task is still writing the
script. `KillAll()` can see nothing to kill and return, after which that task can call `Launch()`
during shutdown.

Shutdown should close admission, cancel and await every staging task, reject subsequent launch
attempts, and then kill and await all process trees before disposal completes.

### [P1] Existing CPU jobs currently request one GPU

The resource model trusts `Job.GpuCount`, but `Refund/DataModel/Job.cs:359` currently returns 1 even
though its documentation says the default is 0. CPU jobs such as `ImportDataSetTs`, `CreateMask`, and
`PostProcess` do not override that property. They would reserve the only GPU or be rejected as
impossible on a managed queue configured with zero GPUs.

Fix the base default or explicitly derive zero for CPU jobs, audit every `IClusterJob`, and add
representative CPU-job tests. `Refund/DataModel/Job.cs` should be included in the files-touched list.

### [P2] Define the executor configuration lifecycle

`ResourceLedger` snapshots totals in its constructor, but `ClusterQueue` is constructed before
`ReadFromJson` hydrates saved properties, and the UI can edit or copy those properties later. Eager
initialization would ignore persisted values, while replacing a ledger on edit would lose active
claims.

Specify lazy initialization or atomic reconfiguration. Changes to `SchedulerType` or managed totals
should either be prohibited while entries exist or preserve all existing allocations with clearly
defined behavior when new totals are below current usage.

### [P2] A boolean admission result cannot distinguish busy from rejected

`CanAdmit(job) == false` is defined as temporarily busy, but impossible requests need permanent
rejection. Returning `false` leaves the job in `Waiting`. Throwing is also insufficient because the
current `HandleWaitingState` catch logs the exception without marking the job `Failed`.

Use an admission result such as `Admit`, `Busy`, or `Reject(reason)`. The state handler can then write
the rejection reason and explicitly add a failure event and transition the job to `Failed`.

### [P2] The proposed ledger is still incremental

The component contract says that `ResourceLedger` owns allocation records and only `Release(token)`
removes them. Removing a `ManagedExecutor` dictionary entry without calling `Release` therefore
cannot free anything, making the derived-release test in lines 292–296 impossible under the described
design.

Establish one source of truth. Either calculate resource snapshots directly from executor-owned live
entries, or make removal of that same entry the ledger operation. Avoid two independently removable
tracking tables.

### [P2] Await output pumps before reporting completion

`Process.HasExited` can become true before the stdout and stderr pump tasks have drained and flushed
their streams. If `GetStatus` immediately reports `Finished` or `Failed`, final progress tracking can
run and the job can be dequeued before its last output is visible.

Store the pump tasks and delay terminal status until both have completed and their writers are
disposed. Resources can still be released immediately when the process exits.

### [P2] Backfill can starve large jobs indefinitely

Backfill has no starvation guarantee. For example, a four-GPU job can wait forever if later one-GPU
jobs continually keep at least one device occupied.

Add aging or resource reservation after a threshold, use bounded backfill, or explicitly accept and
document starvation as an operational risk rather than asserting that it is not practical.

## Suggested additional tests

- A job whose status becomes `Failed` while its process is still running does not release resources
  until the process has been terminated and reaped.
- Two managed queues cannot allocate the same host GPU concurrently.
- Shutdown during pre-launch staging cannot launch a process after shutdown begins.
- CPU-only job types request zero GPUs, including jobs that inherit the base resource properties.
- Persisted and edited managed totals are reflected without losing active allocations.
- Permanent rejection produces one failure event and message rather than retrying every daemon tick.
- Terminal status is not reported until stdout and stderr pumps have drained.
- A large request eventually runs under a sustained stream of smaller jobs, if starvation freedom is
  intended.
