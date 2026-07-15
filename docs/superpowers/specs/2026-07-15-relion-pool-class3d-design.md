# RELION Worker-Pool Support for 3D Classification (Class3D)

**Status:** Approved
**Date:** 2026-07-15
**Scope:** Add worker-pool configuration and command building to the RELION `Class3D` job type, driven by Relay's existing `IPooledJob`/`WorkerPool` machinery. First step toward running RELION's new disk-based worker pool through Relay's pools.

## Background

The RELION `disk-worker-pool` branch (`/Users/tegunovd/dev/relion`) adds a third orchestration
layer alongside single-process and MPI: a **manager + worker fleet** that coordinate *only* through
a shared directory (`--pool_dir`), no MPI, no sockets. It is a **separate binary,
`relion_refine_pool`**:

- **Manager** — one process, launched without `--worker`, owns the iteration loop, reconstruction,
  and checkpointing. CPU-only by design.
- **Worker** — `relion_refine_pool <science args> --worker --half 0 --pool_dir D`, persistent and
  preemptible. For **3D classification, workers use `--half 0`** (refinement's paired `--half 1/2`
  is out of scope here). Workers need the *same* science args as the manager, plus their role flags.
- The pool path is **strictly CPU today** — GPU is specced in RELION but not implemented. Therefore
  **turning the pool on must make the Class3D job CPU-only.**
- `--j` (threads) is forwarded unchanged to every process, so **cores-per-worker maps directly to
  `--j`**.

Relay already drives an analogous manager+fleet model for WarpTools GPU jobs via `WarpJobGpu :
WarpJob, IPooledJob` and the queue-agnostic `WorkerPool`/`QueueRepository` wiring. RELION's disk-pool
lines up with that machinery almost exactly; the differences are (1) workers are **CPU** not GPU,
(2) the worker command is the **full science command** plus role flags, not a thin queue-poller, and
(3) a new **`relion-pool`** submission-template module.

## Model mapping

| Relay concept | WarpTools (today) | RELION-pool (new) |
|---|---|---|
| Manager cluster job | `WarpTools … --external_provisioner` | `relion_refine_pool … --pool_dir D` (no `--worker`) |
| Worker fleet job | `WarpWorker2 --queue-dir … --persistent` (GPU) | `relion_refine_pool … --pool_dir D --worker --half 0` (**CPU**) |
| Shared dir | `<jobdir>/tasks` | `<jobdir>/pool` (`--pool_dir`) |
| Manager resources | CPU-only, 0 GPU | CPU-only, 0 GPU |
| Worker resources | 1 GPU each | `CoresPerWorker` cores, 0 GPU |
| Module | `gpu` (workers) / `cpu` (mgr) | `relion-pool` (both) |

## Placement decision

Implement `IPooledJob` **directly on `Class3D`** for now (no shared base class). When
`Refine3D`/`Class2D` gain pool support later, extract the shared parts into a `RelionPoolJob` base —
a clean, mechanical refactor. This fits the "moving slowly, 3D classification first" constraint and
avoids designing an abstraction against a single consumer. Subclasses `Class3DContinue` /
`Class3DSupervised` inherit pool support from `Class3D`.

## Detailed design

All changes are in `Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs` unless noted.

### 1. New parameters (Compute region)

- `[UiBool] UseWorkerPool` (default `false`) — master toggle (mirrors `AlignMiss`'s pattern).
- `[UiQueue] PoolQueueId` (default `-1`) — shown when `UseWorkerPool`; cluster queue for the fleet.
  (Dropdown still shows "Local" for now; a real cluster queue must be picked to actually pool.)
- `[UiInt] CoresPerWorker` (default `8`) — shown when `UseWorkerPool`; drives `--j` **and** the
  per-worker CPU core request (and the manager's core request).
- `[UiInt] NWorkers` (default `4`) — shown when `UseWorkerPool`; pool fleet size (`= PoolSize`).
- `PoolWorkersAlive` / `PoolWorkersRunning` / `PoolWorkersSubmitted` (`[RelayProperty][Clearable]`)
  — live counters written by `QueueRepository`, same as `WarpJobGpu`.

Visibility (via existing `ConditionalOnField`/`ConditionalOnValue`, which is transitive):
- Pool fields (`PoolQueueId`, `CoresPerWorker`, `NWorkers`) conditional on `UseWorkerPool == true`.
- `UseGpu`, `NThreads`, `NProcesses` conditional on `UseWorkerPool == false`. `NGpus` is already
  conditional on `UseGpu`, so it is transitively hidden when pooled.

`IsPooled => UseWorkerPool && PoolQueueId > 0`.

### 2. Resource / module overrides (branch on `IsPooled`)

```
QueueType      => IsPooled ? CPU : (UseGpu ? GPU : CPU)
GpuCount       => IsPooled ? 0   : (UseGpu ? NGpus : 0)
CoreCount      => IsPooled ? CoresPerWorker : NThreads          // manager reuses CoresPerWorker
MemoryGb       => IsPooled ? MemoryPerWorker : Max(NProcesses-1,1) * MemoryPerWorker
ProcessCount   => IsPooled ? 1 : NProcesses
SupportedModules => base + ["gpu","cpu","relion-pool"]
RequiredModules  => IsPooled ? ["relion-pool"]                  // replaces relion/gpu/cpu when pooled
                             : base.RequiredModules + (UseGpu ? ["gpu"] : ["cpu"])
```

### 3. Command building

```
CommandName => IsPooled ? "relion_refine_pool"
                        : (NProcesses == 1 ? "relion_refine"
                                           : $"mpirun -n {NProcesses} relion_refine_mpi")
```

In `ComposeCommandArguments()`, when `IsPooled`:
- Force `result["j"] = CoresPerWorker`.
- Add `result["pool_dir"] = <jobdir>/pool` (relative to run dir, consistent with other paths).
- Ensure no `--gpu` flag is emitted (`UseGpu` is hidden/ignored when pooled).

The manager command carries no `--worker` / `--half`.

### 4. `IPooledJob` implementation on `Class3D`

- `DirectoryPath` — already on `Job`.
- `PoolQueueId` — effective value gates pooling (`> 0` only when `UseWorkerPool`).
- `PoolSize => NWorkers`.
- `PoolSubmissionCap => PoolSize * 100`.
- `GetWorkerResourceValues(workerLogDir)` — from `GetResourceValues()`, override `n_processes=1`,
  `n_cores=CoresPerWorker`, `memory_gb=MemoryPerWorker`, **`n_gpus=0`**, `std_out`/`std_err` to
  `workerLogDir/%j.out|err`.
- `WorkerRequiredModules => ["relion-pool"]`.
- `GetWorkerCommand(_)` — device index ignored (CPU). Reuse `ComposeCommandArguments()` for full
  arg parity with the manager, then append role flags:
  ```
  cd <RunDirectory>
  relion_refine_pool <ComposeCommandArguments as --k v> --worker --half 0
  ```
  One CPU worker process per cluster job (no `PerDevice` background loop, no success-touch — the
  success marker is the manager's via `CommandSuffix`, which `GetWorkerCommand` does not use).

### 5. Generalize the pool-counter coupling

`QueueRepository.StateHandlers.cs` currently hard-casts the pooled job to `WarpJobGpu` to read/write
`PoolWorkers{Alive,Running,Submitted}` (lines ~170, ~182–191). Lift those three counters onto the
`IPooledJob` interface (get/set) and change the casts to use `pooledJob`. `WarpJobGpu` already
declares the properties (satisfies the interface); `Class3D` adds them. This is unavoidable once a
non-Warp job pools.

### 6. The `relion-pool` module

Modules are free-form string tags matched against `{{name}} … {{/name}}` blocks in a queue's
submission template; there is no central registry. Listing `relion-pool` in `SupportedModules` is all
the code needs — it then auto-registers into `Job.Modules` and appears in the Queue editor. A queue
admin adds a `{{relion-pool}} … {{/relion-pool}}` block that `module load`s a RELION build providing
the `relion_refine_pool` binary. Document the new tag in `README.md`'s module list.

## Risks / verification during implementation

1. **`ConditionalOnValue = false`** — confirm the attribute accepts a `false` bool value for hiding
   `UseGpu`/`NThreads`/`NProcesses` when pooled (transitivity confirmed by the user).
2. **ReadOnly source generator with interface setters** — confirm the generated read-only wrappers
   still expose the three counters after they move onto `IPooledJob` (get/set).
3. **Manager blocks until workers register** — if the pool queue never provisions workers (bad
   config), the manager hangs; RELION has no manager-side registration timeout yet. Acceptable for a
   first test; note it.
4. **`relion_refine_pool` availability** — the `relion-pool` module's `module load` must point at a
   RELION build that includes the `refine_pool` target from the `disk-worker-pool` branch.
5. **Cannot run a real cluster here** — verification is by asserting the constructed command strings
   and module set (manager: `relion_refine_pool … --pool_dir … --j 8`, no `mpirun`, no `--gpu`;
   worker: same + `--worker --half 0`; `RequiredModules == ["relion-pool"]`).

## Out of scope

- GPU pool (not implemented in RELION yet).
- `Refine3D` / `Class2D` pooling (extract a shared base later).
- Refinement's paired `--half 1/2` workers.
- Manager-side registration timeout.
- Suppressing the "Local" entry in the pool-queue dropdown (deferred).
