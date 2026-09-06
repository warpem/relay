# Unified Job Execution and Queue Management — Design Spec

**Status:** Implemented
**Date:** 2026-09-06  
**Scope:** Replace Relay's orchestration for local, locally managed, cluster, and pooled execution. Preserve existing space and job definitions, but not active execution state or queue-runtime state.

## 1. Goal

Relay needs one execution model that answers, consistently:

- whether a job is ready to run;
- where it is waiting and why;
- whether Relay may start it now;
- what was actually launched or submitted;
- what Relay knows, and does not know, about the running work;
- how cancellation, shutdown, restart, failure, and rerun behave; and
- when resources and subordinate workers are truly released.

The current system answers these questions in several overlapping places: mutable `Job.Status`,
`QueueRepository`'s polling daemon, queue-owned staging and limbo collections, `ManagedExecutor`, and
the independent `WorkerPool` state machine. Each component can move the same job or perform side
effects. This makes races difficult to rule out and turns polling anomalies into lifecycle bugs.

The replacement should be small enough to understand as a whole: one serialized coordinator, one
durable execution record per run, and backends that perform effects but do not own state.

This is a replacement, not another layer around the existing orchestration.

## 2. Decisions and constraints

These are requirements, not implementation suggestions.

1. **One Relay process is the sole writer.** Multi-process coordination, distributed locks, leases,
   and leader election are out of scope.
2. **Local jobs remain in-process.** Interactive local jobs must keep working without a separate
   worker service.
3. **Managed commands are owner-bound.** They run through a small `relay-runner` supervisor and must
   stop when their owning Relay process dies.
4. **External scheduler jobs are scheduler-bound.** They may outlive Relay and must be re-adopted
   after an ordinary Relay restart.
5. **Relay-managed admission is strict FIFO.** Later small jobs do not bypass an older job that is
   waiting for capacity.
6. **Only eligible work joins FIFO.** A run waiting for dependencies does not block independent
   runnable work.
7. **Rerunning creates a new attempt.** An old execution is never moved backwards through its state
   machine.
8. **There is no legacy runtime compatibility.** Existing spaces and their job definitions must
   remain readable. Active attempts, queue membership, scheduler IDs, managed processes, pool state,
   and old queue-runtime files may be discarded during migration.
9. **No per-tool heartbeat wrapper.** Relay will not claim that an arbitrary process is alive merely
   because no failure has been observed.
10. **Uncertainty is represented honestly.** A failed status query or a temporarily missing scheduler
    record is not evidence that a job failed.

Queue definitions are configuration rather than runtime state. The implementation may replace the
old queue serialization format; jobs whose saved queue IDs no longer resolve will require queue
selection before their next run. The job definitions themselves must not be lost.

## 3. Non-goals

- A distributed workflow engine or multiple concurrent Relay writers.
- Reconnecting to or resuming a managed payload after Relay dies.
- Forcefully terminating an uncooperative in-process local task without terminating Relay.
- Resource enforcement beyond existing mechanisms. Managed CPU and memory limits remain accounting;
  GPU visibility may be enforced through the process environment.
- Exactly-once guarantees from an external scheduler that provides neither idempotent submission nor
  a searchable correlation token. Relay must handle this ambiguity safely rather than pretend it is
  solvable locally.
- Arbitrarily nested jobs or a general-purpose actor framework.
- Preserving old active execution or worker-pool state during the migration.

## 4. Domain model

### 4.1 Job definition

A job is a reusable node in a space. It contains configuration, parameters, dependency edges, and
defaults for future runs. It does not itself represent a running process.

The job may expose a projected status for the existing UI, but that status is derived from its
current attempt. It is not an orchestration input and backends never assign it.

### 4.2 Execution attempt

An execution attempt is one immutable intention to run a job. It has:

- a globally unique `AttemptId`;
- the owning job ID;
- an immutable snapshot of parameters and relevant queue configuration;
- dependency and FIFO ordering information;
- its lifecycle phase and proposed terminal outcome, where applicable;
- its resource request and any concrete reservation, including assigned GPU IDs;
- a backend kind and durable backend receipt;
- optional worker-group state;
- progress and health information; and
- an append-only history of meaningful transitions.

A job has at most one non-terminal attempt. Completed attempts remain records; rerun creates a new
one. Every asynchronous result carries its `AttemptId`, and results for anything other than the
current matching attempt are ignored.

### 4.3 Queue definition

A queue describes configuration and policy:

- the backend to use;
- command templates and scheduler-specific parsing or observation settings;
- Relay-owned capacity, if any;
- backend capabilities; and
- worker-group suitability.

A queue does not own a mutable job list. Queue membership is derived solely from active attempts.
Configuration edits affect future attempts; an active attempt uses the snapshot captured for it.

The old orchestration responsibilities of `QueueRepository` disappear. A simple queue catalog may
remain for loading, validating, and saving queue definitions.

### 4.4 Backend receipt

A backend receipt is the durable identity required to observe or cancel launched work. Examples are
a task identity for an in-process run, a `relay-runner` control record, or a scheduler job ID plus a
stable attempt correlation token.

A receipt is data. It does not own lifecycle logic.

### 4.5 Worker group

A worker group is optional subordinate execution state owned by an attempt. It contains:

- a worker queue and immutable worker launch specification;
- a mutable desired worker count;
- worker receipts and their latest observations;
- a stable lifetime submission or replacement limit; and
- cleanup state.

Workers are not Relay jobs and do not have independent workflow lifecycle state. The separate
`WorkerPool` state machine and `pool_state.json` are removed.

## 5. Attempt lifecycle

The durable phases are:

```text
WaitingForDependencies
        |
        v
    Preparing -----> Failed
        |
        v
      Queued
        |
        v
     Starting ------> Pending ------> Running
        |                |               |
        +----------------+---------------+
                         |
                         v
                     Finalizing
                         |
                         v
          Succeeded | Failed | Canceled | Interrupted
```

`Cancelling` may be entered from any non-terminal phase. It ends only after the backend and worker
group have reached a state that makes the cancellation outcome truthful.

The diagram omits failure and cancellation edges for readability. The rules are:

- `WaitingForDependencies` means a run was requested but is not yet eligible for queue ordering.
- Once dependencies are satisfied, Relay assigns a monotonic FIFO sequence and begins preparation.
- `Preparing` builds and validates the immutable launch specification. It performs no launch or
  submission.
- `Queued` means preparation succeeded and the attempt is waiting for its turn to start.
- `Starting` means launch/submission intent has been persisted and the backend side effect is in
  progress or awaiting reconciliation.
- `Pending` is used when an external scheduler accepted the work but has not reported it running.
- `Running` means the backend has supplied positive evidence that computation is active. An
  in-process or managed backend may enter it immediately after a successful handshake.
- `Finalizing` performs job result handling and mandatory worker cleanup after computation ends.
- `Interrupted` means owner-bound work ended because Relay exited, or continued execution cannot be
  established safely after recovery. It is terminal and distinct from a user cancellation or tool
  failure.

An observation-health flag such as `Healthy`, `Delayed`, or `Indeterminate` is separate from the
lifecycle phase. A transient scheduler outage therefore does not create false state transitions or
repeated history entries.

## 6. The execution coordinator

One in-process `ExecutionCoordinator` is the only component allowed to:

- transition attempts;
- reserve or release Relay-managed resources;
- assign FIFO sequence numbers;
- update worker-group desired and observed state;
- append lifecycle history;
- decide which backend effects to issue; and
- persist runtime state.

It processes commands and backend results serially, for example:

- `RequestRun`, `RequestCancel`, and `ResizeWorkerGroup`;
- `DependenciesSatisfied`;
- `Prepared` or `PreparationFailed`;
- `BackendStarted`, `BackendObserved`, or `BackendCommandFailed`;
- `BackendExited`; and
- `FinalizationCompleted`.

The coordinator may use an in-process channel and a small reducer. It does not need an actor system,
database server, or event-sourcing framework.

Long-running work never executes on the coordinator loop. The loop persists a transition and emits
an effect; an asynchronous backend operation later posts a result message containing the attempt ID.
Timers and pollers only post observations. They never mutate jobs or attempts directly.

State changes follow this order:

1. validate the command or observation against the current attempt;
2. calculate the complete next state and any effects;
3. persist the next state atomically;
4. publish the projected UI update; and
5. execute effects asynchronously.

If an effect can create external work, its intent is persisted before the effect begins.

## 7. Scheduling and admission

Relay assigns FIFO order when an attempt first becomes dependency-eligible. Preparation may happen
concurrently, but starting is ordered by that sequence.

For a queue whose capacity Relay controls:

1. inspect the oldest eligible, non-started attempt;
2. start it if its complete resource request fits;
3. otherwise start no later attempt on that queue; and
4. reconsider when capacity or the head attempt changes.

This deliberately permits temporary unused capacity to prevent large jobs from starving.

Resource checking is pure. Admission and reservation occur together in one coordinator transition;
there is no side-effecting `CanAdmit` call. A reservation belongs to exactly one attempt and is
released only when Relay has the backend-specific evidence required by the cancellation or completion
contract.

For external queues without Relay-owned capacity, submission commands are issued in FIFO order.
The external scheduler controls priority and start order after accepting them.

The built-in local queue uses the same ordering model with a simple concurrency capacity. Managed
capacity is a CPU, memory, and GPU vector. The initial replacement need not support several managed
queues competing for one host-wide resource ledger; either expose one managed queue or reject an
ambiguous configuration explicitly.

## 8. Backend contract

Backends translate between the coordinator's generic effects and a particular execution mechanism.
They may prepare backend-specific data, start work, observe it, cancel it, and batch-observe or
batch-cancel worker receipts where useful.

Backends do not:

- mutate a job or attempt;
- append user-visible lifecycle events;
- decide retries or terminal outcomes;
- reserve resources;
- own queue membership; or
- run an independent lifecycle state machine.

Backend failures must be typed at least well enough to distinguish:

- a rejected or permanently invalid request;
- a transient transport or command failure;
- a malformed backend response;
- absence from one scheduler view; and
- positive pending, running, or terminal evidence.

### 8.1 In-process local backend

The local backend starts `RunLocal` asynchronously with a cancellation token and reports completion
back to the coordinator. The task never runs on the coordinator loop.

Cancellation is cooperative. Relay requests cancellation and leaves the attempt in `Cancelling`
until the task exits. It must not report `Canceled` while user code is still running. Relay process
death definitively ends the task; a non-terminal local attempt loaded on restart becomes
`Interrupted`.

### 8.2 Locally managed backend

Every managed command runs under a small generic supervisor named `relay-runner`. Individual tools
do not need modification or heartbeats.

The startup protocol closes the dangerous launch windows:

1. Relay persists the attempt in `Starting` with its reservation and launch token.
2. Relay starts `relay-runner` with a private inherited control channel.
3. The runner creates the platform-specific process containment boundary and replies `READY` with
   its identity.
4. Relay persists the runner receipt.
5. Relay sends `GO`.
6. The runner launches the payload inside the containment boundary.

If Relay exits before `GO`, the runner exits without launching the payload. If Relay exits after
`GO`, end-of-file on the control channel makes the runner terminate the complete payload process
tree. Normal cancellation asks the runner to stop the tree, waits for a bounded grace period, and
escalates if necessary.

Platform mechanisms may strengthen the same contract, such as Linux parent-death signals and
Windows Job Objects. The control channel and handshake define the portable behavior; they are not a
heartbeat protocol.

On restart, any non-terminal managed attempt becomes `Interrupted`. The runner's ownership channel
provides the cleanup contract; Relay does not try to reconnect to or adopt a prior runner.

### 8.3 External scheduler backend

An external backend persists its scheduler receipt and survives Relay restart. Startup reconciliation
uses that receipt to resume observation and cancellation.

Every submission template can use the stable `AttemptId` as `{{ attempt_id }}` in scheduler-visible
metadata. Submission intent is persisted before invoking the scheduler, and the returned scheduler
ID is persisted before the attempt leaves `Starting`.

There is an unavoidable two-system crash window if the scheduler accepts a job but Relay loses the
returned ID. Relay marks that attempt `Interrupted`, records its health as indeterminate, and never
resubmits it automatically. The stable attempt token lets an operator correlate it with scheduler
records where the site configuration exposes that metadata. A deliberate rerun creates a new
attempt and token.

## 9. Scheduler observation and transient unknown states

The scheduler adapter reports evidence, not a guessed Relay status. Its observation vocabulary
includes:

- `Pending`;
- `Running`;
- `Succeeded`;
- `Failed`;
- `Canceled`;
- `AbsentFromActiveView`;
- `Unreachable`;
- `Unparseable`; and
- `Indeterminate`.

Communication and parsing failures never prove that computation ended.

Schedulers may have separate active and historical views. Slurm is the motivating example:

1. query the active queue view (`squeue`);
2. if the job is absent, query accounting history (`sacct`);
3. map a present active record to pending or running;
4. map a terminal accounting record to its terminal outcome; and
5. if neither source has the record yet, retain the last lifecycle phase and report an indeterminate
   observation.

This covers the real gap in which a Slurm job has disappeared from `squeue` but has not yet become
visible through accounting. Polling the same uncertainty must not append another staging event or
turn a previously running attempt into failure.

Site submission templates may additionally write a completion receipt in a shared job directory.
This can provide corroborating evidence, but it is not a replacement for scheduler observation.

An indeterminate observation is retained as attempt health with its latest detail. It does not
silently become `Failed` merely because time passed.

## 10. Worker groups and live resizing

When an attempt has pooled workers, the coordinator activates its worker group after the primary
backend reaches `Running`. It reconciles observed workers toward `DesiredSize` using the same queue
and backend machinery as primary attempts.

The coordinator:

- submits missing workers without exceeding the stable lifetime submission limit;
- uses batch scheduler observation and cancellation when configured, otherwise the same per-receipt
  operations as primary scheduler jobs;
- replaces workers that have positively reached a terminal state;
- does not treat one missing or failed status query as worker death;
- persists desired size, receipts, observations, and submission count in the main runtime snapshot;
  and
- drains or cancels every worker before the parent attempt becomes terminal.

Pool counters shown by a job are projections from its current worker group. They are not writable job
state.

### 10.1 Future interactive resizing

The architecture explicitly supports changing pool capacity while a job is running:

1. the UI sends `ResizeWorkerGroup(AttemptId, DesiredSize)`;
2. the coordinator verifies that the attempt and group are active;
3. it persists the new desired size and appends one history event; and
4. ordinary reconciliation submits or retires the difference.

The job definition's pool size remains the default for future attempts. Resizing one active attempt
does not silently edit that default. The lifetime replacement limit is separate from `DesiredSize`,
so decreasing and later increasing a pool cannot accidentally reset or inflate its failure budget.

The exact scale-down policy remains an implementation decision: either retire excess pending workers
first and drain running workers, or explicitly cancel selected running workers for immediate shrink.
It must be visible to the user and safe for the worker protocol; it must not emerge accidentally from
collection ordering.

## 11. Cancellation, clearing, and rerun

Cancellation is an idempotent command against an attempt ID.

- Before launch, it removes the attempt from scheduling and releases any reservation.
- For local work, it requests cooperative cancellation and waits for task exit.
- For managed work, it waits for `relay-runner` to confirm process-tree death.
- For scheduler work, it sends cancellation and observes until terminal evidence is available.
- For pooled work, it also drains or cancels the worker group before the parent becomes terminal.

Repeated cancellation requests do not launch repeated cancellation workflows or append repeated
history entries.

Rerun is allowed only by creating a new attempt. Late observations from the old attempt cannot affect
the new one because every message and receipt is attempt-scoped.

The product behavior for clearing or deleting a job with a non-terminal attempt must be chosen before
implementation. The safe options are to reject the operation until cancellation completes, or define
the operation as cancel-then-clear. Removing state while execution may still exist is not allowed.

## 12. Persistence and recovery

The coordinator persists one atomically replaced runtime snapshot. A small journal or generation
number may be used to detect torn writes, but a database and full event sourcing are unnecessary.
History is persisted for users; it is not replayed as the source of truth.

The snapshot contains only orchestration data required to reconstruct active attempts, reservations,
backend receipts, FIFO order, and worker groups. Queue configuration and space/job definitions remain
separate durable data.

Startup recovery is deterministic:

1. load and validate the latest complete snapshot;
2. rebuild all derived indexes and resource totals from attempts rather than trusting cached sums;
3. mark non-terminal in-process attempts `Interrupted`;
4. reconcile and interrupt non-terminal managed attempts through `relay-runner` cleanup records;
5. resume observation of external scheduler attempts from durable receipts;
6. reconcile ambiguous external submissions conservatively by correlation token;
7. resume cluster worker-group reconciliation; and
8. persist the recovered state before admitting new work.

No backend callback held in memory is required for recovery. No attempt may become runnable merely
because a queue-local list was reconstructed differently.

During migration to this architecture, old runtime files are not imported. Existing jobs load as
definitions with no current attempt and a neutral projected status. Queue definitions may need to be
recreated or migrated separately; that is configuration migration, not execution recovery.

## 13. UI, progress, and history

The UI consumes a projection containing:

- the current attempt and phase;
- why it is waiting;
- FIFO position where meaningful;
- reserved or requested resources;
- backend identity and last positive observation;
- current observation health and warning text;
- cancellation or cleanup progress;
- worker desired, pending, running, and terminal counts; and
- the final outcome.

History is transition-based, not poll-based. Entering `Starting` creates one event. Remaining in
`Starting` for ten polls creates none. Observation warnings may update a single current health record
rather than flooding job history.

Progress samples are separate from lifecycle history and may be throttled or replaced. A progress
parser cannot change lifecycle phase.

The existing familiar job statuses may be mapped from attempt phases during UI migration, but all
commands and decisions use the attempt model directly.

## 14. Required invariants

The implementation and tests must enforce these invariants:

1. A job has at most one non-terminal attempt.
2. Only the coordinator mutates execution state, reservations, worker groups, and lifecycle history.
3. Every asynchronous command result is scoped to an attempt ID; stale results are harmless.
4. A launch or submission is issued at most once unless recovery has positive evidence that retrying
   cannot duplicate external work.
5. One logical transition appends at most one lifecycle event.
6. Queue membership has one source of truth: active attempts.
7. Admission and reservation are one atomic coordinator decision.
8. Resources cannot be double-booked or released while owned work may still be running under Relay's
   backend contract.
9. A transient observation failure cannot produce a terminal attempt outcome.
10. A permanent preparation or configuration error cannot retry forever.
11. Cancellation is idempotent and never reports completion before the backend-specific stop
    condition is met.
12. A parent cannot report success or cancellation when a worker submission remains untraceable;
    the parent becomes `Interrupted` instead.
13. An exception while processing one attempt or queue cannot stop scheduling unrelated work.
14. Rerun never reuses an attempt ID, backend receipt, reservation, or worker group.
15. Restart recovery makes no claim stronger than the evidence available for that backend.

## 15. Test strategy before replacement

Before deleting the old system, characterize its externally important behavior with black-box tests.
Each test must be labelled as one of:

- behavior to preserve;
- known defect to eliminate; or
- behavior intentionally changed by this spec.

Characterization coverage includes:

- dependency readiness and failure propagation;
- queue selection, ordering, and resource admission;
- preparation, submission, pending, running, completion, and finalization;
- cancellation, clear, delete, and rerun requests in every phase;
- completion before the first status poll;
- status-command timeout, exception, malformed output, and transient unknown results;
- shutdown, restart, and runtime-state loading;
- local concurrency, cooperative cancellation, results, and exceptions;
- scheduler script generation, job-ID parsing, observation, and cancellation;
- managed resource accounting, GPU assignment, process-tree containment, cancellation, and Relay
  death;
- worker creation, replenishment, submission limits, batch observation, restart adoption, and cleanup
  on every parent exit path; and
- failure isolation: one bad attempt, queue, parser, or callback cannot stop the daemon equivalent.

The new coordinator is then tested primarily with deterministic fake backends and an in-memory
snapshot store. Tests drive commands, observations, time, and simulated restarts explicitly and
assert complete state plus emitted effects after each step. Small integration suites cover real
process containment and scheduler parsers.

Race-focused tests must include:

- cancel during preparation and startup;
- completion concurrent with cancellation;
- duplicate and out-of-order backend observations;
- stale results arriving after rerun;
- crash before launch, after launch intent, after backend acceptance, and before receipt persistence;
- repeated indeterminate scheduler observations between active and historical views;
- managed runner death and Relay death on both sides of the `GO` handshake;
- worker resize concurrent with worker completion and parent termination; and
- snapshot recovery with each non-terminal phase.

The replacement is not complete while correctness depends on sleep-based timing tests.

## 16. Replacement boundaries

The following current mechanisms are removed rather than retained behind adapters:

- the `QueueRepository` status-switch daemon;
- mutable `QueuedJobs` as independent runtime state;
- queue-specific `StagingJobs` and `JobsInLimbo` collections;
- fire-and-forget queue tasks that mutate `Job`;
- backends assigning `Job.Status`;
- side-effecting admission checks;
- rerun by moving the same `Job` backwards;
- the independent `WorkerPool` state machine; and
- `pool_state.json` as a second persistence authority.

The replacement may reuse pure functionality such as command templating, parsing, resource-value
construction, and progress extraction after those functions are separated from mutable lifecycle
state.

## 17. Open decisions

These do not require more architecture, but they must be resolved before implementation reaches the
affected behavior:

1. **Pool scale-down:** graceful drain versus immediate cancellation, and whether both are exposed.
2. **Long scheduler uncertainty:** the warning/escalation UX and operator actions available while an
   attempt remains indeterminate.
3. **Queue configuration migration:** whether old queue definitions receive a one-time conversion or
   administrators recreate them. Active execution state is not migrated either way.
4. **Custom scheduler correlation:** whether a future adapter may safely adopt an ambiguous
   submission by querying the configured `{{ attempt_id }}` metadata.

## 18. Acceptance criteria

The design is successful when:

- local, managed, cluster, and pooled execution all use the same attempt lifecycle;
- only one serialized component can change execution truth;
- a poll can never create another copy of the current lifecycle event;
- a transient scheduler visibility gap leaves the attempt alive and records indeterminate health;
- managed work cannot survive loss of its owning Relay process under the supported platform
  contract;
- cluster work is re-adopted after restart without normal-case resubmission;
- strict FIFO admission is deterministic and covered by tests;
- cancellation never frees resources before the relevant work is known stopped;
- a user-facing pool resize maps to one persisted desired-size command and deterministic
  reconciliation;
- old orchestration and duplicate persistence paths are deleted; and
- existing space/job definitions still load with no dependency on legacy active-state files.
