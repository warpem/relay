# RELION Worker-Pool Support for Class3D — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add worker-pool configuration and command building to the RELION `Class3D` job so it can run through RELION's new disk-based CPU worker pool, driven by Relay's existing `IPooledJob`/`WorkerPool` machinery.

**Architecture:** `Class3D` becomes the RELION analog of `WarpJobGpu`: a `UseWorkerPool` toggle flips the job to a CPU-only pool **manager** (`relion_refine_pool … --pool_dir`), requests the new `relion-pool` submission module instead of `relion`, and implements `IPooledJob` so Relay's `WorkerPool` maintains a fleet of CPU **worker** jobs (`relion_refine_pool … --worker --half 0`). Unlike Warp, both manager and workers are CPU, and a worker's command is the manager's full science command plus role flags.

**Tech Stack:** C# / .NET, xUnit (`Refund.Tests`), Relay `[RelayProperty]` + `[GenerateReadOnly]` source generators, attribute-based UI fields (`Refund/UIFields`).

## Global Constraints

- Pool path is **CPU-only**: when pooled, `GpuCount == 0`, `QueueType == CPU`, no `--gpu` flag. (RELION has no GPU pool yet.)
- Pooled 3D classification workers use `--half 0` (verbatim; refinement's `--half 1/2` is out of scope).
- Pooled manager binary is `relion_refine_pool` with **no** `mpirun` and **no** `--worker`.
- The pooled module tag is exactly `relion-pool`, and it **replaces** `relion`/`gpu`/`cpu` in `RequiredModules` when pooled.
- `--j` (RELION threads) for every pool process == `CoresPerWorker` (default `8`).
- Follow existing patterns in `WarpJobGpu.cs` and `Refund.Tests/JobQueues/WorkerPoolTests.cs`.
- Run tests with: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~<Name>"`.

## File Structure

- Modify `Refund/DataModel/Job.cs` — add three pool-counter properties to the `IPooledJob` interface (~line 1657).
- Modify `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs` — replace `(WarpJobGpu)` casts (~lines 170, 182–191) with `IPooledJob`.
- Modify `Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs` — pool fields, resource/module/command branching, `IPooledJob` implementation, arg seams.
- Modify `Refund.Tests/JobQueues/WorkerPoolTests.cs` — extend `FakePooledJob` with the new interface members; add a counter test.
- Create `Refund.Tests/Jobs/Class3DPoolTests.cs` — all new Class3D pool tests.
- Modify `README.md` — document the `relion-pool` module tag in the module list (~lines 140–211).

---

### Task 1: Generalize pool counters onto `IPooledJob`

Removes the only Warp-specific coupling in the pool driver so a non-Warp pooled job (Class3D) can publish live worker counts.

**Files:**
- Modify: `Refund/DataModel/Job.cs` (interface `IPooledJob`, ~line 1657)
- Modify: `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs:170,182-191`
- Modify/Test: `Refund.Tests/JobQueues/WorkerPoolTests.cs` (`FakePooledJob` ~line 540; new `[Fact]`)

**Interfaces:**
- Produces: `IPooledJob.PoolWorkersAlive`, `.PoolWorkersRunning`, `.PoolWorkersSubmitted` — all `int { get; set; }`. `WarpJobGpu` already declares matching public `[RelayProperty]` properties, satisfying them implicitly.

- [ ] **Step 1: Extend `FakePooledJob` and add the failing test**

In `Refund.Tests/JobQueues/WorkerPoolTests.cs`, add to `FakePooledJob` (after `GetWorkerCommand`, ~line 560):

```csharp
    public int PoolWorkersAlive { get; set; }
    public int PoolWorkersRunning { get; set; }
    public int PoolWorkersSubmitted { get; set; }
```

Add a new test to the `WorkerPoolTests` class (after `WarpJobGpu_PoolWorkersAlive_DefaultsToZero`, ~line 216):

```csharp
    [Fact]
    public void IPooledJob_ExposesPoolWorkerCounters_MappingToConcreteProperties()
    {
        // The pool driver writes counters through IPooledJob (not a concrete cast), so the interface
        // members must map to WarpJobGpu's real [RelayProperty] properties.
        var job = new MotionAndCTF2D();
        var pooled = (IPooledJob)job;

        pooled.PoolWorkersAlive     = 3;
        pooled.PoolWorkersRunning   = 2;
        pooled.PoolWorkersSubmitted = 5;

        Assert.Equal(3, job.PoolWorkersAlive);
        Assert.Equal(2, job.PoolWorkersRunning);
        Assert.Equal(5, job.PoolWorkersSubmitted);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~IPooledJob_ExposesPoolWorkerCounters"`
Expected: FAIL — compile error, `IPooledJob` has no `PoolWorkersAlive` (interface not extended yet).

- [ ] **Step 3: Add the three counters to the `IPooledJob` interface**

In `Refund/DataModel/Job.cs`, inside `interface IPooledJob` (after `GetWorkerCommand`, before the closing brace ~line 1692):

```csharp
    /// <summary>
    /// Live pool-worker counters written by QueueRepository each daemon tick and read by the pool UI.
    /// Implementors expose them as [RelayProperty][Clearable] ints so they persist and reset with the job.
    /// </summary>
    int PoolWorkersAlive { get; set; }
    int PoolWorkersRunning { get; set; }
    int PoolWorkersSubmitted { get; set; }
```

- [ ] **Step 4: Replace the `WarpJobGpu` casts in the state handler**

In `Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs`, replace line ~170:

```csharp
                    int submittedBefore = pooledJob.PoolWorkersSubmitted;
```

and replace the block at ~182–192:

```csharp
                    if (pooledJob.PoolWorkersAlive     != alive   ||
                        pooledJob.PoolWorkersRunning   != running ||
                        pooledJob.PoolWorkersSubmitted != submitted)
                    {
                        _jobUpdateCallback(job, j =>
                        {
                            var p = (IPooledJob)j;
                            p.PoolWorkersAlive     = alive;
                            p.PoolWorkersRunning   = running;
                            p.PoolWorkersSubmitted = submitted;
                        });
                    }
```

(Leave the `using Refund.Jobs;` import; it is still used elsewhere in the file. If the build warns it is now unused, remove it.)

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~IPooledJob_ExposesPoolWorkerCounters"`
Expected: PASS.

- [ ] **Step 6: Run the full pool test class to confirm no regressions**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~WorkerPoolTests"`
Expected: PASS (all existing pool tests still green).

- [ ] **Step 7: Commit**

```bash
git add Refund/DataModel/Job.cs Refund/Services/Core/Repositories/QueueRepository.StateHandlers.cs Refund.Tests/JobQueues/WorkerPoolTests.cs
git commit -m "refactor: lift pool worker counters onto IPooledJob (drop WarpJobGpu cast)"
```

---

### Task 2: Add pool configuration fields to `Class3D`

Adds the user-facing knobs and live counters, and hides the GPU/MPI fields when pooling.

**Files:**
- Modify: `Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs` (Compute region ~592–660; existing overrides ~120–130)
- Create/Test: `Refund.Tests/Jobs/Class3DPoolTests.cs`

**Interfaces:**
- Produces: `Class3D.UseWorkerPool` (`bool`), `.PoolQueueId` (`int`), `.CoresPerWorker` (`int`), `.NWorkers` (`int`), `.PoolWorkersAlive/Running/Submitted` (`int`).

- [ ] **Step 1: Write the failing test file**

Create `Refund.Tests/Jobs/Class3DPoolTests.cs`:

```csharp
using Refund.DataModel;
using Refund.JobQueues;
using Class3DJob = Refund.Jobs.Refinement.Classes3D.Class3D.Class3D;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class Class3DPoolTests
{
    private static Class3DJob NewJob() =>
        new() { Space = new Space { RootDirectory = "/tmp/relay-test" } };

    [Fact]
    public void PoolFields_HaveExpectedDefaults()
    {
        var job = new Class3DJob();
        Assert.False(job.UseWorkerPool);
        Assert.Equal(-1, job.PoolQueueId);
        Assert.Equal(8, job.CoresPerWorker);
        Assert.Equal(4, job.NWorkers);
        Assert.Equal(0, job.PoolWorkersAlive);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests.PoolFields_HaveExpectedDefaults"`
Expected: FAIL — compile error, `Class3D` has no `UseWorkerPool` etc.

- [ ] **Step 3: Add the pool fields to the Compute region**

In `Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs`, inside `#region Compute` (after `UseScratch`, ~line 605), add:

```csharp
    [UiBool("", "Use worker pool",
            helpText: "Run this classification through RELION's disk-based worker pool: a CPU-only " +
                      "manager plus a fleet of CPU worker jobs maintained on a cluster queue. Turning " +
                      "this on makes the job CPU-only (RELION's pool has no GPU path yet) and replaces " +
                      "MPI. Leave off for the normal single-job (GPU/MPI) run.")]
    [RelayProperty]
    public bool UseWorkerPool { get; set; } = false;

    [UiQueue("Pool queue",
             helpText: "Cluster queue on which to maintain the CPU pool worker fleet.")]
    [RelayProperty]
    public int PoolQueueId { get; set; } = -1;

    [UiInt("", "Cores per worker",
           1, 99999, 1,
           helpText: "CPU cores requested for each pool worker (and the manager). Also sets RELION's " +
                     "--j threads for every pool process.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int CoresPerWorker { get; set; } = 8;

    [UiInt("", "Number of pool workers",
           1, 99999, 1,
           helpText: "Target number of CPU worker jobs maintained in the pool.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int NWorkers { get; set; } = 4;
```

Then add the live counters just before `#endregion` at the end of the Compute region (~line 660):

```csharp
    [RelayProperty] [Clearable] public int PoolWorkersAlive { get; set; }
    [RelayProperty] [Clearable] public int PoolWorkersRunning { get; set; }
    [RelayProperty] [Clearable] public int PoolWorkersSubmitted { get; set; }
```

> NOTE: `ConditionalOnField`/`ConditionalOnValue` are used on `[UiInt]` elsewhere in this file (`NGpus`). Confirm they compile on `[UiQueue]` too (they are inherited from `UiFieldBase`); if `[UiQueue]` does not accept them, the queue field simply shows unconditionally — acceptable for now, note it and continue.

- [ ] **Step 4: Hide the GPU/MPI fields when pooling**

In the same file, modify the existing `UseGpu`, `NThreads`, and `NProcesses` attributes to hide them when the pool is on. `NGpus` is already conditional on `UseGpu`, so it hides transitively.

`UseGpu` (~line 607) — add the two conditional args:

```csharp
    [UiBool("gpu", "Use GPU",
            helpText: "If set to Yes, the program will use the GPU for calculations. " +
                      "This will speed up the calculations significantly. If set to No, " +
                      "the calculations will be done on the CPU.",
            ConditionalOnField = nameof(UseWorkerPool),
            ConditionalOnValue = false)]
```

`NThreads` (~line 624) — add after the existing `helpText`:

```csharp
    [UiInt("j", "Number of threads",
           1,
           99999,
           1,
           helpText: "Number of threads running in parallel on each worker. Threads don't increase " +
                     "the memory usage as much as processes do, but the performance gain is smaller when " +
                     "compared to processes distributed over the same number of CPU cores.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = false)]
```

`NProcesses` (~line 634) — add after the existing `helpText`:

```csharp
    [UiInt("", "Number of workers",
           1,
           99999,
           1,
           helpText: "The number of workers to use for the job. This is the number of MPI processes " +
                     "that will be started. When >1, 1 process is reserved for the work manager. The number of workers " +
                     "should not exceed the number of available CPU cores.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = false)]
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests.PoolFields_HaveExpectedDefaults"`
Expected: PASS.

- [ ] **Step 6: Add and run a JSON round-trip test**

Add to `Class3DPoolTests`:

```csharp
    [Fact]
    public void PoolFields_RoundTripJson()
    {
        var job = new Class3DJob { UseWorkerPool = true, PoolQueueId = 3, CoresPerWorker = 16, NWorkers = 10 };
        var node = new System.Text.Json.Nodes.JsonObject();
        job.WriteToJson(node);

        var job2 = new Class3DJob();
        job2.ReadFromJson(node);

        Assert.True(job2.UseWorkerPool);
        Assert.Equal(3, job2.PoolQueueId);
        Assert.Equal(16, job2.CoresPerWorker);
        Assert.Equal(10, job2.NWorkers);
    }
```

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests"`
Expected: PASS (both tests).

- [ ] **Step 7: Commit**

```bash
git add Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs Refund.Tests/Jobs/Class3DPoolTests.cs
git commit -m "feat: add worker-pool config fields to Class3D"
```

---

### Task 3: Branch resources, modules, command name, and pool arguments

Makes the pooled job CPU-only, swaps the module set to `relion-pool`, switches the binary to `relion_refine_pool`, and injects the shared pool arguments. No `IPooledJob` yet.

**Files:**
- Modify: `Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs` (overrides ~120–130; `CommandName` ~842; `ComposeCommandArguments` ~868–943)
- Test: `Refund.Tests/Jobs/Class3DPoolTests.cs`

**Interfaces:**
- Consumes: `UseWorkerPool`, `PoolQueueId`, `CoresPerWorker` (Task 2).
- Produces: `Class3D.IsPooled` (`bool`); `Class3D.ApplyPoolArguments(Dictionary<string,string> result)` (`public void`).

- [ ] **Step 1: Write the failing tests**

Add to `Class3DPoolTests`:

```csharp
    [Fact]
    public void CommandName_SwitchesToRelionRefinePoolWhenPooled()
    {
        Assert.Equal("relion_refine_pool",
            new Class3DJob { UseWorkerPool = true, PoolQueueId = 1 }.CommandName);
        Assert.Equal("relion_refine",
            new Class3DJob { UseWorkerPool = false, NProcesses = 1 }.CommandName);
        Assert.Equal("mpirun -n 4 relion_refine_mpi",
            new Class3DJob { UseWorkerPool = false, NProcesses = 4 }.CommandName);
    }

    [Fact]
    public void RequiredModules_PooledReplacesRelionWithRelionPool()
    {
        var pooled = new Class3DJob { UseWorkerPool = true, PoolQueueId = 1 }.RequiredModules;
        Assert.Equal(new[] { "relion-pool" }, pooled);

        var gpu = new Class3DJob { UseWorkerPool = false, UseGpu = true }.RequiredModules;
        Assert.Contains("relion", gpu);
        Assert.Contains("gpu", gpu);
        Assert.DoesNotContain("relion-pool", gpu);
    }

    [Fact]
    public void SupportedModules_IncludesRelionPool()
    {
        Assert.Contains("relion-pool", new Class3DJob().SupportedModules);
    }

    [Fact]
    public void PooledResourcesAreCpuOnly()
    {
        var job = new Class3DJob { UseWorkerPool = true, PoolQueueId = 1, CoresPerWorker = 8, MemoryPerWorker = 12 };
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal(8, job.CoreCount);
        Assert.Equal(1, job.ProcessCount);
        Assert.Equal(12, job.MemoryGb);
    }

    [Fact]
    public void ApplyPoolArguments_SetsThreadsAndPoolDir_AndRemovesGpu()
    {
        var job = NewJob();
        job.CoresPerWorker = 8;
        var args = new Dictionary<string, string> { ["gpu"] = "", ["j"] = "2" };

        job.ApplyPoolArguments(args);

        Assert.Equal("8", args["j"]);
        Assert.True(args.ContainsKey("pool_dir"));
        Assert.False(args.ContainsKey("gpu"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests"`
Expected: FAIL — `IsPooled`/`ApplyPoolArguments` undefined; pooled `CommandName`/modules/resources not yet branched.

- [ ] **Step 3: Add `IsPooled` and branch the resource/module overrides**

In `Class3D.cs`, modify the existing overrides at ~120–130 to read:

```csharp
    /// <summary>True when this job runs as a RELION disk-pool manager (CPU-only, relion_refine_pool).</summary>
    public bool IsPooled => UseWorkerPool && PoolQueueId > 0;

    public override string[] SupportedModules =>
        base.SupportedModules.Concat(["gpu", "cpu", "relion-pool"]).ToArray();

    public override string[] RequiredModules =>
        IsPooled ? ["relion-pool"]
                 : base.RequiredModules.Concat(UseGpu ? ["gpu"] : ["cpu"]).ToArray();

    public override int CoreCount => IsPooled ? CoresPerWorker : NThreads;

    public override int MemoryGb => IsPooled ? MemoryPerWorker
                                             : Math.Max(NProcesses - 1, 1) * MemoryPerWorker;

    public override int GpuCount => IsPooled ? 0 : (UseGpu ? NGpus : 0);

    public override int ProcessCount => IsPooled ? 1 : NProcesses;
```

Then modify the existing `QueueType` override (~line 79) to:

```csharp
    public override JobQueueType QueueType =>
        IsPooled ? JobQueueType.CPU : (UseGpu ? JobQueueType.GPU : JobQueueType.CPU);
```

- [ ] **Step 4: Branch `CommandName` and add `ApplyPoolArguments`**

Modify `CommandName` (~842):

```csharp
    public override string CommandName =>
        IsPooled ? "relion_refine_pool"
                 : (NProcesses == 1 ? "relion_refine"
                                    : $"mpirun -n {NProcesses} relion_refine_mpi");
```

Add this method immediately after `ComposeCommandArguments` (~line 944):

```csharp
    /// <summary>
    /// Applies the RELION disk-pool argument overrides shared by the manager and every worker:
    /// forces --j to the per-worker core count, points --pool_dir at the shared coordination
    /// directory, and drops --gpu (the pool path is CPU-only). Kept as a public seam so it can be
    /// unit-tested without a fully connected input port graph.
    /// </summary>
    public void ApplyPoolArguments(Dictionary<string, string> result)
    {
        result["j"] = CoresPerWorker.ToString(CultureInfo.InvariantCulture);
        result["pool_dir"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "pool"));
        result.Remove("gpu");
    }
```

- [ ] **Step 5: Wire `ApplyPoolArguments` into `ComposeCommandArguments`**

In `ComposeCommandArguments`, immediately before the final `return result;` (~line 942), add:

```csharp
        if (IsPooled)
            ApplyPoolArguments(result);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests"`
Expected: PASS (all Task 2 + Task 3 tests).

- [ ] **Step 7: Commit**

```bash
git add Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs Refund.Tests/Jobs/Class3DPoolTests.cs
git commit -m "feat: CPU-only pool branching + relion-pool module + pool args for Class3D"
```

---

### Task 4: Implement `IPooledJob` on `Class3D`

Wires Class3D into Relay's `WorkerPool` fleet driver with CPU worker resources and a full-science worker command.

**Files:**
- Modify: `Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs` (class declaration ~line 32; new IPooledJob members)
- Test: `Refund.Tests/Jobs/Class3DPoolTests.cs`

**Interfaces:**
- Consumes: `IsPooled`, `CoresPerWorker`, `NWorkers`, `MemoryPerWorker`, `GetResourceValues()`, `ComposeCommandArguments()`, `RunDirectory`.
- Produces (satisfying `IPooledJob`): `PoolSize` (`public int`), explicit `PoolQueueId`/`PoolSubmissionCap`/`GetWorkerResourceValues`/`WorkerRequiredModules`/`GetWorkerCommand`, and public `ComposeWorkerCommand(Dictionary<string,string>)`.

- [ ] **Step 1: Write the failing tests**

Add to `Class3DPoolTests`:

```csharp
    [Fact]
    public void Class3D_ImplementsIPooledJob()
    {
        Assert.IsAssignableFrom<IPooledJob>(new Class3DJob());
    }

    [Fact]
    public void PoolQueueId_ActiveOnlyWhenUseWorkerPool()
    {
        var off = new Class3DJob { UseWorkerPool = false, PoolQueueId = 5 };
        Assert.Equal(-1, ((IPooledJob)off).PoolQueueId);   // stored but not active

        var on = new Class3DJob { UseWorkerPool = true, PoolQueueId = 5 };
        Assert.Equal(5, ((IPooledJob)on).PoolQueueId);
    }

    [Fact]
    public void PoolSize_EqualsNWorkers()
    {
        Assert.Equal(6, ((IPooledJob)new Class3DJob { NWorkers = 6 }).PoolSize);
    }

    [Fact]
    public void GetWorkerResourceValues_AreCpuOnly()
    {
        var job = NewJob();
        job.UseWorkerPool = true; job.PoolQueueId = 1; job.CoresPerWorker = 8; job.MemoryPerWorker = 12;

        var w = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");

        Assert.Equal("0", w["n_gpus"]);          // CPU-only workers
        Assert.Equal("8", w["n_cores"]);
        Assert.Equal("12", w["memory_gb"]);
        Assert.Equal("1", w["n_processes"]);
        Assert.Contains("%j", w["std_out"]);
    }

    [Fact]
    public void WorkerRequiredModules_IsRelionPool()
    {
        Assert.Equal(new[] { "relion-pool" }, ((IPooledJob)new Class3DJob()).WorkerRequiredModules);
    }

    [Fact]
    public void ComposeWorkerCommand_WrapsArgsWithRoleFlags()
    {
        var job = NewJob();
        var cmd = job.ComposeWorkerCommand(new Dictionary<string, string>
        {
            ["o"] = "run", ["pool_dir"] = "pool", ["j"] = "8",
        });

        Assert.StartsWith("cd ", cmd);
        Assert.Contains("relion_refine_pool", cmd);
        Assert.Contains("--worker", cmd);
        Assert.Contains("--half 0", cmd);
        Assert.Contains("--pool_dir pool", cmd);
        Assert.Contains("--j 8", cmd);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests"`
Expected: FAIL — `Class3D` is not `IPooledJob`; `ComposeWorkerCommand` undefined.

- [ ] **Step 3: Add `IPooledJob` to the class declaration**

In `Class3D.cs`, change the class declaration (~line 32):

```csharp
public class Class3D : RelionJob, IClusterJob, IPooledJob
```

- [ ] **Step 4: Implement the `IPooledJob` members**

Add a new region at the end of the class (before the final closing brace of the class), after the `#region Results paths` section:

```csharp
    #region Worker pool (IPooledJob)

    // DirectoryPath and the PoolWorkers* counters satisfy IPooledJob implicitly (public members).

    /// <summary>Target number of CPU worker jobs in the pool.</summary>
    public int PoolSize => NWorkers;

    // Explicit: the stored [UiQueue] value persists across toggles, but pooling must only activate
    // when the user has turned UseWorkerPool on. The generic pool machinery reads PoolQueueId > 0.
    int IPooledJob.PoolQueueId => UseWorkerPool ? PoolQueueId : -1;

    int IPooledJob.PoolSubmissionCap => PoolSize * 100;

    // Both manager and workers are CPU here (RELION's pool has no GPU path yet), so workers request
    // CoresPerWorker cores and zero GPUs — the opposite of the WarpTools pool, whose workers are GPU.
    Dictionary<string, string> IPooledJob.GetWorkerResourceValues(string workerLogDir)
    {
        var values = GetResourceValues();
        values["job_id"]      = $"{Id}-worker";
        values["n_processes"] = "1";
        values["n_cores"]     = CoresPerWorker.ToString(CultureInfo.InvariantCulture);
        values["memory_gb"]   = MemoryPerWorker.ToString(CultureInfo.InvariantCulture);
        values["n_gpus"]      = "0";
        values["std_out"]     = Path.Combine(workerLogDir, "%j.out");
        values["std_err"]     = Path.Combine(workerLogDir, "%j.err");
        return values;
    }

    string[] IPooledJob.WorkerRequiredModules => ["relion-pool"];

    // A RELION pool worker runs the same run as the manager, so it needs the manager's full science
    // arguments (RELION requires manager/worker arg parity), plus the worker role flags. 3D
    // classification workers all use --half 0.
    string IPooledJob.GetWorkerCommand(int deviceIndex) =>
        ComposeWorkerCommand(ComposeCommandArguments());

    /// <summary>
    /// Wraps a fully-composed argument set into a pool worker command: cd into the run directory,
    /// then launch relion_refine_pool with those arguments plus --worker --half 0. Public seam so it
    /// is unit-testable without a connected input port graph (the arg dict is supplied directly).
    /// </summary>
    public string ComposeWorkerCommand(Dictionary<string, string> args)
    {
        string flat = string.Join(" ", args.Select(kv =>
            string.IsNullOrWhiteSpace(kv.Value) ? $"--{kv.Key}" : $"--{kv.Key} {kv.Value}"));
        return $"cd {RunDirectory}\nrelion_refine_pool {flat} --worker --half 0";
    }

    #endregion
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests"`
Expected: PASS.

- [ ] **Step 6: Confirm the read-only wrapper still generates**

The `[GenerateReadOnly]` generator must still compile `ReadOnlyClass3D` after the interface is added. A clean build proves it.

Run: `dotnet build Refund.Tests/Refund.Tests.csproj`
Expected: Build succeeded, 0 errors. (If the generator tries to mirror `IPooledJob`'s worker methods onto the read-only type and fails, mark this as a blocker and stop — do not work around it silently.)

- [ ] **Step 7: Commit**

```bash
git add Refund/Jobs/Refinement/Classes3D/Class3D/Class3D.cs Refund.Tests/Jobs/Class3DPoolTests.cs
git commit -m "feat: implement IPooledJob on Class3D (CPU workers, relion_refine_pool)"
```

---

### Task 5: Document the `relion-pool` module

Records the new module tag so queue admins add a matching template block, and locks in that it auto-registers.

**Files:**
- Modify: `README.md` (module list ~lines 140–211)
- Test: `Refund.Tests/Jobs/Class3DPoolTests.cs`

**Interfaces:**
- Consumes: `Class3D.SupportedModules` (Task 3), `Job.Modules` static registry, `Job.PopulateStatic()`.

- [ ] **Step 1: Write the failing test**

Add to `Class3DPoolTests`:

```csharp
    [Fact]
    public void JobModules_Registry_IncludesRelionPool()
    {
        // Modules auto-register from every job's SupportedModules during PopulateStatic; the Queue
        // editor lists Job.Modules, so this is what makes {{relion-pool}} available to template authors.
        if (Job.Types.Count == 0)
            Job.PopulateStatic();

        Assert.Contains("relion-pool", Job.Modules);
    }
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~JobModules_Registry_IncludesRelionPool"`
Expected: PASS — `relion-pool` is already in `Class3D.SupportedModules` from Task 3, so `PopulateStatic` unions it into `Job.Modules`. (This test formally guards that behavior.)

- [ ] **Step 3: Document the module in the README**

In `README.md`, find the module-list section (the list containing `gpu`, `cpu`, `warp`, `relion`, `imod`, `aretomo2`, `missalignment`, `mpi`). Add an entry:

```markdown
- `relion-pool` — requested instead of `relion` when a RELION job runs through the disk-based worker
  pool (CPU-only manager + CPU worker fleet). The block must `module load` a RELION build that
  provides the `relion_refine_pool` binary. Both the pool manager and its workers request this module.
```

Match the surrounding list's exact bullet/formatting style.

- [ ] **Step 4: Run the full new test class**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~Class3DPoolTests"`
Expected: PASS (all Class3D pool tests).

- [ ] **Step 5: Commit**

```bash
git add README.md Refund.Tests/Jobs/Class3DPoolTests.cs
git commit -m "docs: document the relion-pool submission module"
```

---

### Task 6: Full regression pass

**Files:** none (verification only).

- [ ] **Step 1: Build the whole test project**

Run: `dotnet build Refund.Tests/Refund.Tests.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the full pool + Class3D suites**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --filter "FullyQualifiedName~WorkerPoolTests|FullyQualifiedName~Class3DPoolTests"`
Expected: PASS, no skips of the new tests.

- [ ] **Step 3: Run the entire `Refund.Tests` suite to catch cross-cutting regressions**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj`
Expected: PASS (or only pre-existing unrelated failures — if any fail, confirm they fail on `main` before this branch, otherwise investigate).

---

## Self-Review

**Spec coverage:**
- Bool `UseWorkerPool` toggle + queue dropdown + pool options, hiding MPI/GPU fields → Task 2. ✓
- CPU-only when pooled (`GpuCount=0`, `QueueType=CPU`, no `--gpu`) → Task 3 (+`ApplyPoolArguments` drops `--gpu`). ✓
- `relion-pool` module replaces `relion` when pooled; regular `relion` otherwise → Task 3 (`RequiredModules`), documented Task 5. ✓
- CPU workers (contrast with Warp GPU workers) → Task 4 (`n_gpus=0`, `WorkerRequiredModules=relion-pool`). ✓
- Cores-per-worker (default 8) drives `--j` and per-worker cores → Task 2 (field), Task 3 (`ApplyPoolArguments` `--j`), Task 4 (`n_cores`). ✓
- `relion_refine_pool` manager (no mpirun/--worker) + workers `--worker --half 0` → Task 3 (`CommandName`), Task 4 (`ComposeWorkerCommand`). ✓
- Generalize the `WarpJobGpu` hard-cast → Task 1. ✓
- Full `IPooledJob` wiring so it runs end-to-end via `WorkerPool` → Task 4. ✓
- Manager reuses `CoresPerWorker` for its core count → Task 3 (`CoreCount`). ✓
- Dedicated `NWorkers` field for fleet size → Task 2, Task 4 (`PoolSize`). ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code; every test has assertions.

**Type consistency:** `IsPooled` (bool), `PoolSize` (int, public), `ApplyPoolArguments`/`ComposeWorkerCommand` (public), explicit-interface `PoolQueueId`/`PoolSubmissionCap`/`GetWorkerResourceValues`/`WorkerRequiredModules`/`GetWorkerCommand`, counters `int {get;set;}` — names identical across tasks and matching `IPooledJob`/`WarpJobGpu`.

## Known risks carried from the spec (verify during execution, do not silently work around)

1. `ConditionalOnValue = false` and its use on `[UiQueue]` — Task 2 Step 3 note.
2. `[GenerateReadOnly]` compiling `ReadOnlyClass3D` after `IPooledJob` is added — Task 4 Step 6 gate.
3. A misconfigured pool queue leaves the manager blocking on worker registration (RELION has no manager-side timeout yet) — runtime behavior, out of code scope.
4. The `relion-pool` module's `module load` must resolve to a RELION build with the `refine_pool` target from the `disk-worker-pool` branch — deployment/queue-config concern.
