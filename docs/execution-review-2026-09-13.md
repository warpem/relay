# Execution and queue review — 2026-09-13

## Scope and conclusion

Reviewed the managed-queue introduction and its hardening history, then all 23 commits from
`b3b43f9c` through `b27851f9` implementing and hardening the unified execution system. Compared
the coordinator, runtime, process supervision, scheduler adapters, worker groups, persistence,
DataManager lifecycle commands, factory integration, UI notifications, and artifact rendering with
the earlier implementation and both design documents. Independent reviewers covered the core state
machine, managed backend, and scheduler/configuration code; integration changes were cross-reviewed.

The redesign's central direction is sound: one attempt owns a run, durable state drives effects,
and queues no longer carry competing lifecycle state. Most defects were incomplete boundaries
between that design and the surrounding application. The corrections below retain that architecture.
Managed restart follows the deliberately accepted owner-bound cleanup contract. This working tree also
adds interactive pool resizing through application commands as a new feature; its missing UI was not
a regression from the previous implementation.

## Fixed in this working tree

| Priority | Defect and concrete trigger | Correction |
|---|---|---|
| P1 | A fast worker submission recursively reconciled and dispatched another worker; the original effect batch then submitted that same worker again. | Claim the complete effect batch under the existing coordinator gate before dispatch. No second outbox or duplicated phase rules. |
| P1 | Enqueue validated a Building job, released the DataManager lock, and waited for queue configuration. Clear/delete could then remove its files or definition before execution took ownership. | Keep admission and lifecycle commands under the existing model lock. Execution callbacks write through the repository's existing write lock, removing the callback lock inversion. Local and cluster enqueue share one method. |
| P1 | `configure_job` could change resource parameters after admission; preparation used the live job with a different request from the reserved one. Input connections and upstream files were similarly mutable. Autosave also allowed recovery to read older parameters than the durable request. | Freeze execution parameters and input connections while owned, save the definition before execution intent, and protect upstream data with active consumers. Metadata edits remain available. GUI and MCP use one guarded parameter-update API. |
| P1 | A shell could launch a background child and exit before Relay identified its process group. | Establish a gated shell and confirmed group before `READY`; `GO` releases the payload through the existing stdin pipe. |
| P1 | Killing `relay-runner` left its separate payload group alive, while the host treated runner exit as completion. | Include the confirmed group in `READY`, and check/contain that group before releasing live ownership. A real subprocess reproduction confirmed the original orphan and subsequent cleanup. |
| P1 | An activation error with uncertain cleanup released managed capacity while work might still be alive. | Keep the receipt and reservation and use the existing cancellation path until terminal evidence arrives. |
| P1 | Graceful shutdown rejected late effect results, losing a scheduler receipt returned by a submission already in progress. | Close admission and effect generation immediately, allow issued effects to persist results through the same serialized path, and wait a bounded five seconds for them to settle. |
| P1 | Queue snapshots serialized custom-variable value tuples as `{}`, silently losing descriptions and default values on every copied/submitted configuration. | Serialize and read tuple fields explicitly. Verify the restored values in an actual generated script. Previously lost values cannot be reconstructed automatically. |
| P1 | Slurm accounting could mix allocation and child-step states, allowing a failed step to end a still-running or successful allocation. | Ask `sacct` for allocations only with `--allocations`. This removes the ambiguity at its source. |
| P2 | Accepted worker cancellations were reissued, overlapping cancellation batches could target the same workers, and stale observations could resurrect ended workers. | Use the existing cancellation model with worker `Stopping`, count workers already retiring, and serialize cancellation per parent. Terminal worker observations stay terminal. |
| P2 | Terminal projection could silently reject a failure when an earlier active projection had failed and the job still appeared Building. | Remove the obsolete job-status guard. Runtime ownership already prevents reuse until the terminal projection is saved. Write failure detail before publishing the status update. |
| P2 | Scheduler observation-health changes never reached the UI; a healthy primary observation also erased uncertainty about untraceable workers. | Include health in the projection fingerprint, preserve worker uncertainty, and display the projected warning in the queue card. |
| P2 | A subscriber queue silently discarded distinct object/deletion events after 64 queued notifications. | Use ordered lossless notification channels. Generic object events cannot safely be treated as interchangeable redraw requests. |
| P2 | Log tails did not switch to newly available iterations or stop polling at completion. Expanded figures lost cache invalidation when server-side file probes were removed. | Derive tail identity from path, polling interval, and terminal version; add semantic image versions to expanded figures. This removes several independent display flags and comparisons. |

The allocation query follows the scheduler's documented distinction between allocations and job
steps: [SchedMD sacct documentation](https://slurm.schedmd.com/sacct.html).

## Accepted restart contract and new resizing capability

### Managed restart ends the old ownership

The supervisor now has its own `Relay.Runner` console project, depending only on the .NET runtime.
Relay launches its sibling executable directly. The SDK includes it in the ordinary build and publish
output through the project reference; no second deployment or web-entry-point mode is needed.

`ExecutionCoordinator.Recover()` releases managed reservations and gives old attempts the `Interrupted`
outcome. The old runner owns cleanup through EOF on its inherited control channel. The new Relay
process neither adopts that runner nor probes or kills saved process IDs, and admission does not wait
for old process receipts. Numeric IDs can be reused, so treating them as surviving proof of ownership
would create a different and unsafe recovery contract.

This behavior is intentional and accepted, not an open P1 defect or a requirement for new containment
machinery. Cleanup is asynchronous; it is not an instantaneous barrier before restart admission, and
there is no absolute cleanup guarantee after simultaneous failure of Relay and its supervisor. The
live-host process-group check fixes runner death while its owning Relay process remains alive. It
does not turn a saved PID into authority for a subsequent Relay process.

### Live pool resizing is a new application command

The redesign prepared `ResizeWorkerGroup` in the coordinator, runtime, and QueueRepository for a future
application command. The previous application had no live resizing UI or MCP operation. Reading a
mutable `PoolSize` internally was not an exposed resizing feature, so its removal is not a lost-feature
finding. The parameter freeze remains appropriate.

`DataManager.ResizeJobPool`, the queue card's Workers controls, and MCP `resize_job_pool` change only the
running attempt's desired worker count. The minimum target is one. The job's configured pool size remains
the default for future runs. Scale-down requests immediate cancellation of excess workers and waits for
terminal observations; scale-up uses the remaining lifetime submission budget and rejects unattainable
increases. Resizing never replenishes that budget. `get_job.Pool` and the UI expose the active target and
running, pending, stopping, and submitted counts.

## Other operating boundaries

- Queue configuration snapshots isolate active runs from edits. Fixing a bad observation command in
  the queue definition does not repair an already-active attempt. Validate active and terminal queries
  before submission; a future operator repair action should explicitly update observation configuration
  for an owned attempt, not silently change its launch specification.
- Lossless asynchronous notifications can accumulate behind a permanently stalled subscriber. If this
  becomes a practical problem, redesign UI subscriptions around coalesced entity invalidations and
  resynchronization, rather than dropping arbitrary create/delete events.
- External acceptance followed by abrupt process death before receipt persistence remains an unavoidable
  ambiguity without scheduler-side idempotency or lookup. The redesign correctly refuses blind resubmission.
- The five-second graceful settling period is bounded; effects that outlast it are not guaranteed to
  return a receipt before the application process exits.
- macOS without `setsid` has weaker descendant containment. Process tests use a real private-group
  stand-in; a real Linux host and real external schedulers were not exercised during this review.

## Preserved behavior and intentional changes

Preserved: local in-process and interactive jobs; managed CPU/memory accounting and GPU assignment;
worker resource/module overrides, replenishment and lifetime submission limits; batch scheduler
observation/cancellation with per-receipt fallbacks; script templates; final progress collection;
external-receipt adoption on ordinary restart; and fresh attempts for reruns.

Intentional according to the redesign: strict FIFO replaces backfill, dependency-blocked work does
not occupy FIFO, owner-bound interrupted work is distinguished from tool failure, uncertain scheduler
observations do not become failure merely through elapsed time, and old active runtime/pool state is
not migrated. Removing blind submission retries and timeout-based fake cancellation was appropriate.

The later storage hardening also rejects legacy nonnumeric job-directory names. Normal materialized
jobs use numeric IDs; a space containing custom historical names now needs explicit directory migration.
This compatibility restriction should remain visible to operators rather than being mistaken for
execution-state migration.

## Validation

- Baseline: **379/379** Refund tests passed before edits.
- Initial audit result: **403/403** Refund tests passed after the original review edits, before the
  additional resizing and explicit restart-contract coverage.
- Final result: **423/423** Refund tests passed, including active pool projection/MCP output,
  resize validation, immediate successive cancellation batches, fixed lifetime budgets, and actual
  standalone runner launch/EOF cleanup before and after activation.
- Explicit managed restart contract: **2/2** focused tests passed, covering a saved receipt before
  and after activation, interruption/resource release, and new admission without stale-PID effects.
- `dotnet build Relay/Relay.csproj --no-restore`: succeeded, no errors; existing warnings remain.
- Release publish with the deployment pipeline's flags succeeded and included the standalone runner.
  An isolated copy of its four runtime files successfully cleaned up its payload after its owner
  process was killed with SIGKILL, with no application assemblies present.
- Actual queue-card component rendering verified the minimum, running/busy disabled states, and
  click/mouseup/double-click propagation guards using existing ASP.NET/Fluent assemblies. No browser
  visual check was performed.
- `git diff --check`: clean.
- New regression coverage includes synchronous duplicate dispatch, overlapping worker observations,
  shutdown receipt persistence, ambiguous activation, real process containment, DataManager admission
  races, frozen inputs, queue snapshot round-trips, lossless notification bursts, and component JS calls.

Tests use deterministic gates and completion signals for concurrency cases. No scheduler jobs were
submitted and no existing project data was modified. The Warp submodule is updated to upstream main
`88932134e9a1b996eac22085a6de875b93725e60`; all 423 Refund tests pass with that revision.
Existing local OS metadata files were retained and are now ignored by Warp's upstream ignore rules.
