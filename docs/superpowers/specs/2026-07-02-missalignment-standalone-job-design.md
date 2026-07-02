# MissAlignment as a standalone GPU job (drop worker-pool support)

Date: 2026-07-02
Status: Design — awaiting user approval

## Problem

`AlignMiss` (internal `TypeName` "MissAlignment") inherits worker-pool support it
should not have. The whole worker-pool mechanism lives on the abstract base
`WarpJobGpu`:

- `WarpJobGpu : WarpJob, IPooledJob` (`Refund/Jobs/Abstract/WarpJobGpu.cs:9`)
- The `IPooledJob` contract, the `[UiQueue("Pool Queue")]` field (`PoolQueueId`),
  `PoolSize`, the `PoolWorkers*` counters, `GetWorkerCommand` (which literally
  launches `WarpWorker2 --queue-dir …`), and `GetWorkerResourceValues`.

Every WarpTools GPU job (`Motion2D`, `CTF2D`, `TemplateMatch`, `Refine`, … — 19
in total) legitimately derives from `WarpJobGpu` and runs per-item work via
`WarpWorker2` pulling from a shared task queue, so pooling makes sense for them.

`AlignMiss` is **not** a WarpTools per-item job. It runs a single `miss-alignment`
command, defines its own `NWorkers` ("reconstruction workers"), and overrides
`CoreCount`/`MemoryGb` with its own formulas. It never populates the
`<output>/tasks` queue that `GetWorkerCommand` targets. Because pooling is baked
into the shared base class, `AlignMiss` shows a "Pool Queue" picker and, if a user
sets it, `QueueRepository.GetOrCreatePool` would spawn `WarpWorker2` workers
against a queue `AlignMiss` never fills.

## Decision

Make `AlignMiss` a **standalone GPU cluster job** that does not inherit
`WarpJobGpu` or `WarpJob` at all:

```
AlignMiss : Job, IClusterJob      // was: WarpJobGpu, IClusterJob
```

Replicate only the members `AlignMiss` genuinely uses. This removes worker-pool
support (the fix) and, as a deliberate benefit, decouples it from WarpTools log
parsing so miss-alignment's own output format can be parsed independently later.

### Why standalone rather than a base-class split

Considered and rejected in favor of the standalone approach (user's call):

- *Mask locally* (override `PoolQueueId` to -1, hide the field): leaves
  `IPooledJob` dead-implemented on MissAlignment — masks rather than fixes.
- *Additive/rename base split* (`WarpJobGpuBase` etc.): keeps MissAlignment on the
  WarpTools hierarchy, including WarpTools log parsing it does not want.

Standing MissAlignment on its own `Job` base is the honest expression of "this job
is outside the WarpTools ecosystem" and enables custom log parsing.

## Members replicated into `AlignMiss`

| Member | Source today | Standalone behavior |
|---|---|---|
| `QueueType` | `WarpJobGpu` | `=> JobQueueType.GPU` (base `Job.QueueType` is abstract) |
| `NGpus` ("Number of GPUs") | `WarpJobGpu` | own `[RelayProperty]`, default 1 — **kept** |
| `PerDevice` ("Workers per GPU", CliName `perdevice`) | `WarpJobGpu` | own `[RelayProperty]`, default 1 — **kept**; still composed into the command |
| `GpuCount` | `WarpJobGpu` (`IsPooled?0:NGpus`) | `=> NGpus` |
| `CoreCount` / `MemoryGb` | already overridden in `AlignMiss` | unchanged (`NWorkers*4+4`, `NWorkers*6+20`) |
| `GpuMemoryGb` | base `Job` default (12) | inherit base default |
| `CommandSuffix` | `WarpJob` (`&& touch SUCCESS`) | replicated — queue relies on the SUCCESS marker |
| `ComposeCommandArguments` | `WarpJob` adds `strict`; `WarpJobGpu` adds `external_provisioner` | `base` (reflection) + `config-file` + `prepare-stacks`; no `strict` to remove, no pool arg |
| `ResProcessedItemsJson` | `WarpJob` | replicated — the expanded view reads `processed_items.json` |
| `TrackProgressLogs()` | `WarpJob` (WarpTools parser) | replicated verbatim now; custom parser later |
| `NItemsProcessed/Total/Failed` | `WarpJob` | replicated (used by the verbatim parser) |

## Members dropped

- **All worker-pool support** — `IPooledJob`, `PoolQueueId` + `[UiQueue("Pool
  Queue")]`, `PoolSize`, `PoolWorkersAlive/Running/Submitted`, `IsPooled`,
  `ManagerCoreCount/MemoryGb`, `GetWorkerCommand`, `GetWorkerResourceValues`,
  `WorkerRequiredModules`, and the `external_provisioner` arg. **This is the fix.**
- **`MemoryPerWorker`** ("Memory per worker" field) — vestigial for AlignMiss: its
  `MemoryGb` uses `NWorkers`, and the field's CliName is empty so it never reaches
  the command. Removed.

## Resolved decision points (defaults chosen while user was away — revisit on review)

1. **Cluster modules — KEEP `warp` for now.** Today: `warp` + `gpu` +
   `missalignment`. The standalone class keeps `RequiredModules => [warp, gpu,
   missalignment]` and `SupportedModules => base(cpu, gpu) + [warp, missalignment]` to avoid
   any runtime regression in locating the `miss-alignment` binary. Dropping `warp`
   (to `[gpu, missalignment]`) is the cleaner "outside WarpTools" choice and is a
   one-line change — flip it if miss-alignment's environment is confirmed
   independent of the warp module.

2. **Log parsing — REPLICATE WarpTools verbatim.** Copy `WarpJob.TrackProgressLogs`
   exactly (stderr capture, `JobTools.CleanProgressBarLines`, write cleaned log to
   `LogFilePath(0)`, `WarpTools.ExtractProgressInfo` → `NItems*`). Behavior is
   byte-identical to today; the miss-alignment-specific parser is a follow-up at a
   clearly marked seam.

## Impact / blast radius

Changes confined to `AlignMiss.cs` (base class + replicated members). The
generated `ReadOnlyAlignMiss` regenerates from `ReadOnlyJob` and still exposes
everything the expanded view uses (`AngPix`, `ResProcessedItemsJson`, ports,
`DirectoryPath`).

Unchanged and verified safe:

- The 19 pooled jobs, `WarpJobGpu`, `WorkerPool`, `QueueRepository` (pool casts are
  inside `is IPooledJob` guards → never reached for AlignMiss).
- `WorkerPoolTests` and `JobTaxonomyTests` (the latter only checks the category
  string, which is unchanged).
- Type registration (`Job.Types`) — filters on `IsSubclassOf(typeof(Job))`, which
  still holds.

### Known minor UI consequence

- `JobCard.razor:289` gates the "items processed" line on
  `Job is ReadOnlyWarpJob && NItemsTotal > 0`. `ReadOnlyAlignMiss` is no longer a
  `ReadOnlyWarpJob`, so that line stops rendering for MissAlignment even though
  `NItems*` are still computed. This is cosmetic and consistent with the job owning
  its own presentation. Optional follow-up: generalize that check (e.g. a shared
  read-only interface) if the item count should still show. Out of scope here.
- `QueueJobCard.razor:97` gates the pool status line on `ReadOnlyWarpJobGpu` — it
  stops rendering for MissAlignment, which is exactly the goal.

## Testing

- Build succeeds; `Refund.Tests` (incl. `WorkerPoolTests`, `JobTaxonomyTests`) pass.
- Manual/inspection: MissAlignment card no longer shows a "Pool Queue" field;
  "Number of GPUs" and "Workers per GPU" still present; expanded view still loads
  `processed_items.json`.