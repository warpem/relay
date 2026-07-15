# RELION Worker-Pool Support for 3D Classification (Class3D)

**Status:** Approved
**Date:** 2026-07-15
**Scope:** Add worker-pool configuration and command building to the RELION `Class3D` job type, driven by Relay's existing `IPooledJob`/`WorkerPool` machinery. First step toward running RELION's new disk-based worker pool through Relay's pools.

## Background

The RELION `disk-worker-pool` branch (`/Users/tegunovd/dev/relion`) adds a third orchestration
layer alongside single-process and MPI: a **manager + worker fleet** that coordinate *only* through
a shared directory (`--pool_dir`), no MPI, no sockets. It is a **separate binary,
`relion_refine_pool`**:

- **Manager** — one process, launched without `--worker`, owns the iteration loop and checkpoint
  orchestration. CPU-only. It does **not** do the heavy compute: the E-step, reconstruction, and
  maximization are all dispatched as tasks to the workers (`ml_optimiser_pool.cpp:1933-1982`).
- **Worker** — `relion_refine_pool <science args> --worker --half 0 --pool_dir D`, persistent and
  preemptible. Workers run the E-step **and** reconstruction/maximization, so their per-worker memory
  matters. For **3D classification, workers use `--half 0`** (refinement's paired `--half 1/2` is out
  of scope here). Workers need the *same* science args as the manager, plus their role flags.
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
avoids designing an abstraction against a single consumer.

**Subclasses are explicitly out of scope.** `Class3DContinue` and `Class3DSupervised` both derive
from `Class3D` and **override `ComposeCommandArguments`** with their own continuation/supervised
argument logic — so they would incidentally inherit the `UseWorkerPool` toggle and the `IPooledJob`
interface without a validated pooled command path. Until they are deliberately brought into scope,
`ValidateInputs` rejects a pooled configuration on any type other than the base `Class3D`.

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

`IsPooled => UseWorkerPool`. (Deliberately not gated on `PoolQueueId`: enabling the toggle always
builds the CPU-only pooled command. A forgotten queue means no workers get provisioned and the run
fails clearly, instead of silently falling back to the legacy GPU/MPI path — so no validation code is
needed for a first test.)

### 2. Resource / module overrides (branch on `IsPooled`)

```
QueueType      => IsPooled ? CPU : (UseGpu ? GPU : CPU)
GpuCount       => IsPooled ? 0   : (UseGpu ? NGpus : 0)
CoreCount      => IsPooled ? CoresPerWorker : NThreads          // manager reuses CoresPerWorker
MemoryGb       => IsPooled ? MemoryPerWorker : Max(NProcesses-1,1) * MemoryPerWorker
ProcessCount   => IsPooled ? 1 : NProcesses
SupportedModules => base + ["gpu","cpu","relion-pool"]
RequiredModules  => IsPooled ? ["cpu","relion-pool"]           // relion-pool replaces the *software*
                             : base.RequiredModules + (UseGpu ? ["gpu"] : ["cpu"])
```

`relion-pool` replaces the **`relion` software tag**, not the `cpu` resource tag: the `{{cpu}}`
template block carries the CPU partition/queue `#SBATCH` directives (`README.md` module section), so
dropping it would submit a partition-less job. The pooled manager therefore requires **both** `cpu`
and `relion-pool`, and excludes `gpu` and the ordinary `relion` tag.

### 3. Command building

```
CommandName => IsPooled ? "relion_refine_pool"
                        : (NProcesses == 1 ? "relion_refine"
                                           : $"mpirun -n {NProcesses} relion_refine_mpi")
```

In `ComposeCommandArguments()`, when `IsPooled`, apply pool-owned argument normalization **after** the
`AdditionalArguments` merge (so a user cannot override pool-owned flags):
- Force `result["j"] = CoresPerWorker`.
- Add `result["pool_dir"] = <jobdir>/pool` (relative to run dir, consistent with other paths).
- Remove `gpu` (auto-emitted from the persisted `UseGpu` field) and `scratch_dir` (from a persisted
  `UseScratch`) — both unsupported on the CPU pool path.

The manager command carries no `--worker` / `--half` (those are appended only by the worker command).

### 4. `IPooledJob` implementation on `Class3D`

- `DirectoryPath` — already on `Job`.
- `PoolQueueId` — effective value gates pooling (`> 0` only when `UseWorkerPool`).
- `PoolSize => NWorkers`.
- `PoolSubmissionCap => PoolSize * 100`.
- `GetWorkerResourceValues(workerLogDir)` — from `GetResourceValues()`, override `n_processes=1`,
  `n_cores=CoresPerWorker`, `memory_gb=MemoryPerWorker`, **`n_gpus=0`**, `std_out`/`std_err` to
  `workerLogDir/%j.out|err`.
- `WorkerRequiredModules => ["cpu","relion-pool"]` (same CPU-partition + software set as the manager).
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
the `relion_refine_pool` binary. `README.md`'s module section is a **shell code block** (not a bullet
list); add a `{{ relion-pool }} … {{ /relion-pool }}` block there and a matching load line in the
SLURM example.

### 7. Input validation — intentionally omitted for the first test

No `ValidateInputs` guards are added. The `IsPooled => UseWorkerPool` decoupling already prevents the
silent legacy-path fallback; `UseScratch` is hidden when pooled and `scratch_dir`/`gpu` are stripped in
`ApplyPoolArguments`; RELION's pool `REPORT_ERROR`s clearly on any remaining unsupported flag. Pooling
subclasses (`Class3DContinue`/`Class3DSupervised`) is out of scope; guarding it is deferred rather than
built now. Iterate once real testing surfaces a concrete need.

## Risks / verification during implementation

1. **`ConditionalOnValue = false`** — confirmed the attribute accepts a `false` value
   (`UiFieldBase.cs:52,58`, `ConditionalOnValue` is `object`), used for hiding
   `UseGpu`/`NThreads`/`NProcesses`/`UseScratch` when pooled; `NGpus` hides transitively via `UseGpu`.
2. **ReadOnly source generator** — *not* a blocker: the generator explicitly skips behavioral
   interfaces including `IPooledJob` (`ReadOnlyGenerator.cs`). The three counters are separate public
   `[RelayProperty]` properties and mirror normally. A clean build is sufficient verification.
3. **Manager registration timeout** — the manager waits ~5 min for a compatible worker set (600 polls
   × 500 ms) then `REPORT_ERROR`s (`ml_optimiser_pool.cpp:531-569`); it does **not** hang forever. The
   pool queue must actually provision workers within that window for a run to start.
4. **`relion_refine_pool` availability** — the `relion-pool` module's `module load` must point at a
   RELION build that includes the `refine_pool` target from the `disk-worker-pool` branch.
5. **Cannot run a real cluster here** — verification is by asserting the constructed command strings
   and module set. Beyond the per-seam unit tests, **one connected base-`Class3D` fixture** (a
   ParticleSet + reference MapList wired through edges) asserts the *actual* manager command from
   `ComposeCommandArguments()` and the *actual* worker string from `GetWorkerCommand()`:
   manager/worker arg parity, worker-only `--worker --half 0`, `relion_refine_pool` + `--pool_dir` +
   `--j 8`, no `mpirun`/`--gpu`, and `RequiredModules == ["cpu","relion-pool"]`. This guards against
   the seams being green while the production wiring never calls them.

## Out of scope

- GPU pool (not implemented in RELION yet).
- `Refine3D` / `Class2D` pooling (extract a shared base later).
- `Class3DContinue` / `Class3DSupervised` pooling (guarded off via `ValidateInputs`).
- Refinement's paired `--half 1/2` workers.
- Suppressing the "Local" entry in the pool-queue dropdown (deferred — but a pooled job with no queue
  selected is now rejected by `ValidateInputs`, closing the silent-fallback hole).
