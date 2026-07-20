# Review: RELION Worker-Pool Support for Class3D

Reviewed:

- `docs/superpowers/specs/2026-07-15-relion-pool-class3d-design.md`
- `docs/superpowers/plans/2026-07-15-relion-pool-class3d.md`

Cross-checked against the current Relay tree and RELION's `disk-worker-pool` branch at commit
`22e7ccd5`.

## Verdict

The base `Class3D` direction is sound and suitable for a first RELION-pool test, but the design and
plan need a few focused revisions before implementation. The proposed module set can omit existing
CPU scheduler directives, an enabled pool can silently fall back to the legacy execution path, and
the tests do not exercise the actual manager/worker command path. `Class3DContinue` is explicitly
out of scope for this first test and should not be promised by the documents.

## Findings

### 1. Medium: the pool path still permits arguments that RELION explicitly rejects

The plan hides GPU/MPI controls but leaves `UseScratch` available, and its normalizer removes only
`gpu` (`plan.md:436-448`). `UseScratch` is a normal CLI field on `Class3D`
(`Class3D.cs:595-605`). The current RELION pool implementation fails closed on `--scratch_dir`; it
also rejects `--preread_images` and `--no_parallel_disc_io`
(`ml_optimiser_pool.cpp:485-490`).

The free-form `AdditionalArguments` field creates a second path for incompatible or role-changing
flags. In pooled mode a user can still inject `--scratch_dir`, `--worker`, or `--half`. The latter
two violate the design's guarantee that the manager command carries neither role flag.

Required revision:

- Hide or clearly disable `UseScratch` when pooling and either omit `scratch_dir` or reject the
  configuration in `ValidateInputs()` with a useful message.
- Reserve and normalize pool-owned arguments after additional arguments are merged: `pool_dir`,
  `j`, `gpu`, `worker`, and `half`. Also reject the known unsupported pool flags rather than letting
  the cluster job fail after submission.
- Test a pre-existing scratch setting and conflicting additional arguments in pooled mode. This is
  not required for a default-settings smoke test, but it prevents a confusing post-submission
  failure for an otherwise valid Class3D configuration.

### 2. High: replacing `cpu` with `relion-pool` can remove CPU scheduler directives

The proposed manager and worker module set is only `["relion-pool"]` (`design.md:79-81,108`; plan
`RequiredModules_PooledReplacesRelionWithRelionPool` and `WorkerRequiredModules_IsRelionPool`). The
same documents describe `relion-pool` as the software-loading block for the special RELION build.

Relay's documented submission template uses `{{cpu}}` for CPU partition directives
(`README.md:180-197`), and existing pooled Warp managers retain `cpu` specifically for CPU
partition/queue directives (`WarpJobGpu.cs:83-90`). With the proposed set, a queue following the
documented template will load the pool binary but omit its CPU partition block for both manager and
workers.

Required revision:

- Make `relion-pool` replace the `relion` software tag, not the CPU resource tag. The pooled manager
  and workers should normally require both `cpu` and `relion-pool`, while excluding `gpu` and the
  ordinary `relion` tag.
- If the intent is instead for `{{relion-pool}}` to duplicate all CPU scheduler directives, state
  that explicitly in the design and README. That is less composable and would require queue admins
  to duplicate their CPU configuration.
- Update tests to assert both the software and CPU tags.

### 3. High: enabling the toggle can silently run the legacy GPU/MPI job

`IsPooled` is defined as `UseWorkerPool && PoolQueueId > 0` (`design.md:69`; `plan.md:397`). If the
user enables the worker pool but leaves the queue picker on Local (`-1`), the UI hides the normal
GPU/MPI fields while `IsPooled` is false. `CommandName`, resources, and modules then silently fall
back to the stored legacy `UseGpu`, `NThreads`, and `NProcesses` values. This contradicts the stated
invariant that turning the pool on makes the job CPU-only.

Required revision:

- Add `ValidateInputs()` coverage that rejects `UseWorkerPool == true` with `PoolQueueId <= 0`.
  The toggle can continue to preserve the stored queue ID, but a visibly pooled configuration must
  not be executable as a non-pooled job.
- Add tests for the Local/unselected queue state and for queueing validation, not only an explicit
  interface getter test.

### 4. Medium: CPU-only worker metadata still advertises GPU memory

`GetWorkerResourceValues()` starts from `GetResourceValues()` and overrides `n_gpus` but not
`gpu_memory_gb` (`plan.md:585-595`). `Job.GpuMemoryGb` defaults to 12, so pooled Class3D manager and
worker resource dictionaries still expose `gpu_memory_gb=12`. `WarpJobGpu` already zeroes GPU
memory for its CPU-only manager (`WarpJobGpu.cs:16-20`).

This will not matter for templates that keep every GPU directive inside `{{gpu}}`, but it violates
the CPU-only resource contract and can affect custom templates that consume the value directly.
Override pooled `GpuMemoryGb` to zero and assert both `n_gpus` and `gpu_memory_gb` in tests.

### 5. High: the tests do not exercise the command paths that will run on the cluster

The plan introduces public `ApplyPoolArguments` and `ComposeWorkerCommand` seams and tests them with
manually constructed dictionaries. It does not build a connected Class3D fixture and assert the
actual manager arguments produced by `ComposeCommandArguments()`, nor the actual worker string from
`IPooledJob.GetWorkerCommand()`.

That test shape can report green even if the production wiring never applies the helper. For the
first test, add one connected base-Class3D fixture that asserts the actual manager and worker
commands, manager/worker argument parity, role-flag differences, and CPU-only resources/modules.
Subclass and continuation coverage can wait until those job types are deliberately brought into
scope.

### 6. Low: the queue visibility code does not match the design

The design says `PoolQueueId` is conditional on `UseWorkerPool` (`design.md:64-67`), but the code
snippet in Task 2 omits `ConditionalOnField` and `ConditionalOnValue` from `[UiQueue]`
(`plan.md:199-202`). The note at `plan.md:230` says to confirm that the named values compile, but
`UiQueue` inherits them from `UiFieldBase`, so the plan should include them directly and test field
visibility.

## Documentation corrections

These do not block the Relay implementation but should be corrected while revising the documents:

- Remove the claim that `Class3DContinue` inherits supported pool behavior (`design.md:43-47`) and
  list it explicitly as out of scope. Inheriting the interface and UI fields incidentally is not the
  same as supporting its independent command builder. If the inherited toggle remains visible,
  reject pooled `Class3DContinue` configurations until that path is intentionally implemented.
- The RELION manager does have an initial-worker registration timeout: 600 polls at 500 ms, followed
  by an error if no compatible workers appear (`ml_optimiser_pool.cpp:531-569`). The risk section's
  claim that it hangs indefinitely without a manager-side timeout is stale.
- The current pool manager owns iteration control and checkpoint orchestration, but reconstruction/
  maximization is dispatched to workers. The background statement that the CPU-only manager owns
  reconstruction is inaccurate and obscures why worker memory is important.
- The read-only generator is not an open blocker here. It explicitly skips behavioral interfaces
  such as `IPooledJob`; adding setter-bearing counter properties will not make the generated wrapper
  implement that interface (`SourceGenerators/RelaySourceGenerators/ReadOnlyGenerator.cs:55-94`). A
  normal build is still appropriate verification.
- README's module section is a shell code block, not a Markdown bullet list (`README.md:140-176`).
  Task 5 should add a `{{relion-pool}} ... {{/relion-pool}}` example matching that structure, plus a
  corresponding load block in the SLURM example.

## Recommended plan changes

Before implementation, add explicit tasks for:

1. Pool-incompatible option validation/sanitization for base `Class3D`.
2. Composable module requirements (`cpu` plus `relion-pool`) and fully zeroed GPU metadata.
3. Validation of the enabled-but-unselected pool queue state.
4. One connected base-Class3D fixture that tests the actual manager and worker commands.
5. Explicitly excluding `Class3DContinue` from this first implementation and test scope, with a
   guard against accidentally queueing it in pooled mode if the inherited toggle remains exposed.
