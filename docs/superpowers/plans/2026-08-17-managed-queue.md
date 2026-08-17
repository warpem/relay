# Locally Managed Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a queue that runs jobs as local processes on the Relay host, admitting them only when CPU cores, memory and GPUs are free.

**Architecture:** A new `ClusterScheduler.Managed` value makes `ClusterQueue` branch to a host-wide `ManagedExecutor` instead of shell command templates. The executor owns one table of live entries; `ResourceLedger` is a pure calculator over that table, so freeing a resource is a consequence of an entry leaving, not an action anyone must remember. Script composition, the daemon state machine, progress tracking and finalisation are reused unchanged.

**Tech Stack:** C# / .NET 10, ASP.NET Blazor Server, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-05-managed-queue-design.md` (revised 2026-08-17 after review)

## Global Constraints

- Target framework `net10.0`. Tests are xUnit; `Refund` already has `<InternalsVisibleTo Include="Refund.Tests" />`.
- Test classes that construct concrete `Job` types MUST carry `[Collection("JobRegistry")]` and call an `EnsurePopulated()` guard before `Job.PopulateStatic()` — that method is not idempotent.
- Commit messages use a lowercase type prefix (`feat:`, `fix:`, `docs:`, `test:`) and MUST end with:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- Do NOT commit the `Warp` submodule; it is dirty for unrelated reasons. Always `git add` explicit paths.
- Existing behaviour for SLURM/LSF/PBS/SGE/Flux/Custom queues must not change. `CanAdmit` returns `Admit` for every non-managed queue.
- Run the full suite with `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`. Baseline before this plan: **249 passing, 0 failing**.

## File Structure

| File | Responsibility |
|---|---|
| `Refund/JobQueues/ResourceLedger.cs` | **New.** Pure resource arithmetic. No state, no `Process`, no `Job`. |
| `Refund/JobQueues/ManagedExecutor.cs` | **New.** The single entry table, process spawn/reap/kill, output pumps. |
| `Refund/JobQueues/ManagedProcessRegistry.cs` | **New.** Persists `{jobId,pid,pgid,startTime}`; kills leftovers at startup. |
| `Refund/DataModel/JobQueue.cs` | `ClusterScheduler.Managed`; `AdmissionResult`; `virtual CanAdmit`. |
| `Refund/DataModel/Job.cs` | `GpuCount` base default 1 → 0 (last, after the audit). |
| `Refund/Jobs/**` | Explicit `GpuCount` overrides where currently inherited. |
| `Refund/JobQueues/ClusterQueue.cs` | Managed properties; branch in submit/status/abort; `CanAdmit` override. |
| `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs` | Expose the managed properties. |
| `Refund/Services/Core/Repositories/QueueRepository*.cs` | Own the executor; admission guard; shutdown. |
| `Refund/Services/Core/DataManager/DataManager.Queue.cs` | Reject a second managed queue; guard edits while busy. |
| `Relay/Program.cs` | `ApplicationStopping` hook. |
| `Relay/Screens/Overlay/Settings/QueueEditor.razor{,.cs}` | Managed field group. |

Tasks 1–3 are independent and may be done in any order. Tasks 4–6 build the executor and are sequential. Tasks 7–9 wire it in. Tasks 10–11 surface it.

---

### Task 1: Make `Job.GpuCount` explicit, then flip the base default

`Job.GpuCount` returns `1` (`Refund/DataModel/Job.cs:359`) while its own docstring says the default is 0. Every job type that does not override it — the CPU-only tools — silently requests one GPU. Admission is only as trustworthy as this property, so it is corrected first.

The order matters and must not be shortcut: make every currently-implicit value **explicit** first, *then* flip the base. After the explicit pass, no job's effective value depends on the base, so flipping it changes nothing observable — including for existing SLURM/Flux queues, whose templates already interpolate `{{ n_gpus }}`.

**Files:**
- Modify: `Refund/DataModel/Job.cs:359`
- Modify: various under `Refund/Jobs/` (determined by the audit in Step 1)
- Test: `Refund.Tests/JobQueues/JobResourceRequestTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `Job.GpuCount` is trustworthy — every concrete job type states its GPU count explicitly. Task 2 and Task 7 rely on this.

- [ ] **Step 1: Run the audit and write down the result**

```bash
cd /Users/tegunovd/dev/relay-public
# Types that already state it (expected: 9)
grep -rn "override int GpuCount" --include="*.cs" Refund/Jobs | grep -v obj
# Every concrete job type
grep -rln "public override JobQueueType QueueType" --include="*.cs" Refund/Jobs | grep -v obj | sort
```

Anything in the second list but not the first currently inherits `1`. For each, read its `CommandName` and `ComposeCommandArguments` and decide the true value: `0` for CPU-only tools, the real count for anything that touches a GPU. Record the list in the commit message.

Known to inherit and be CPU-only: `CreateMask`, `ImportDataSetTs`, `ImportAlignments`, `PostProcess`. Known to override already: `WarpJobGpu`, `Class3D`, `InitialReference`, `Refine3D`, `CreateDataSource`, `CreateSpecies`, `EstimateWeights`, `AlignMiss`, `Denoising`.

- [ ] **Step 2: Write the failing test**

Create `Refund.Tests/JobQueues/JobResourceRequestTests.cs`:

```csharp
using Refund.DataModel;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;
using ImportTsJob = Refund.Jobs.Ts.Import.ImportDataSetTs.ImportDataSetTs;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class JobResourceRequestTests
{
    private static readonly object _populateLock = new();

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    [Fact]
    public void CpuOnlyJobs_RequestNoGpus()
    {
        EnsurePopulated();
        Assert.Equal(0, new MaskJob().GpuCount);
        Assert.Equal(0, new ImportTsJob().GpuCount);
    }

    [Fact]
    public void BaseGpuCountDefault_IsZero_MatchingItsDocumentedContract()
    {
        // Guards against a future job type silently inheriting a GPU request.
        Assert.Equal(0, typeof(Job).GetProperty(nameof(Job.GpuCount))!
                                   .GetValue(new MaskJob()));
    }
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~JobResourceRequestTests"`
Expected: FAIL, `Assert.Equal() Failure: Expected: 0, Actual: 1`.

- [ ] **Step 4: Add explicit overrides**

For each type identified in Step 1 that currently inherits, add next to its other resource overrides:

```csharp
    /// <summary>CPU-only tool; requests no GPUs.</summary>
    public override int GpuCount => 0;
```

Use the real count instead of `0` for any type the audit shows genuinely uses a GPU.

- [ ] **Step 5: Run the whole suite — still green, and nothing changed yet**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: 249 + 2 new; the two new tests may still fail (the base default is untouched), everything else passes.

- [ ] **Step 6: Flip the base default**

In `Refund/DataModel/Job.cs:359`:

```csharp
    /// <summary>
    /// Number of GPUs to allocate when running this job.
    /// Default is 0 (no GPUs), but can be overridden by job implementations that use GPU acceleration.
    /// Every concrete job type states this explicitly; the default exists so a new type that forgets
    /// cannot silently reserve a GPU.
    /// </summary>
    public virtual int GpuCount => 0;
```

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: all pass, 251 total. If any pre-existing test fails here, a job was relying on the implicit `1` and Step 4 missed it — fix that type's override rather than reverting the default.

- [ ] **Step 8: Commit**

```bash
git add Refund/DataModel/Job.cs Refund/Jobs Refund.Tests/JobQueues/JobResourceRequestTests.cs
git commit -m "$(cat <<'EOF'
fix: make every job's GPU count explicit, and default to none

Job.GpuCount returned 1 while its docstring said 0, so every job type that
did not override it — the CPU-only tools — silently requested a GPU. That is
harmless while an external scheduler is only reading {{ n_gpus }} for a
directive, but it becomes an admission decision once Relay schedules locally.

Made the value explicit on every concrete type first, so flipping the base to
its documented 0 changes nothing observable for existing SLURM/Flux queues.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `ResourceLedger` — pure resource arithmetic

No mutable state, no `Process`, no `Job`. Totals are passed on every call rather than captured, which is what removes the configuration-lifecycle problem: there is nothing to initialise eagerly and nothing to rebuild when an admin edits the queue's totals.

**Files:**
- Create: `Refund/JobQueues/ResourceLedger.cs`
- Test: `Refund.Tests/JobQueues/ResourceLedgerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces, used by Tasks 4 and 7:
  - `readonly record struct ResourceTotals(int Cores, int MemoryGb, int Gpus)`
  - `readonly record struct ResourceRequest(int Cores, int MemoryGb, int Gpus)`
  - `sealed record ResourceAllocation(int Cores, int MemoryGb, IReadOnlyList<int> GpuIndices)`
  - `readonly record struct LedgerSnapshot(int FreeCores, int FreeMemoryGb, IReadOnlyList<int> FreeGpuIndices)`
  - `static LedgerSnapshot ResourceLedger.Compute(ResourceTotals, IEnumerable<ResourceAllocation>)`
  - `static bool ResourceLedger.TryFit(ResourceTotals, IEnumerable<ResourceAllocation>, ResourceRequest, out ResourceAllocation)`
  - `static bool ResourceLedger.CanEverFit(ResourceTotals, ResourceRequest)`

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/JobQueues/ResourceLedgerTests.cs`:

```csharp
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ResourceLedgerTests
{
    private static readonly ResourceTotals Host = new(Cores: 16, MemoryGb: 64, Gpus: 4);

    private static ResourceAllocation Alloc(int cores, int mem, params int[] gpus) =>
        new(cores, mem, gpus);

    [Fact]
    public void Compute_WithNoAllocations_ReportsEverythingFree()
    {
        var snap = ResourceLedger.Compute(Host, Array.Empty<ResourceAllocation>());

        Assert.Equal(16, snap.FreeCores);
        Assert.Equal(64, snap.FreeMemoryGb);
        Assert.Equal(new[] { 0, 1, 2, 3 }, snap.FreeGpuIndices);
    }

    [Fact]
    public void Compute_SubtractsLiveAllocations()
    {
        var snap = ResourceLedger.Compute(Host, new[] { Alloc(4, 16, 0), Alloc(2, 8, 2) });

        Assert.Equal(10, snap.FreeCores);
        Assert.Equal(40, snap.FreeMemoryGb);
        Assert.Equal(new[] { 1, 3 }, snap.FreeGpuIndices);
    }

    [Fact]
    public void DroppingAnEntryFreesItsResources_WithoutAnyReleaseCall()
    {
        // The invariant the whole design rests on: "release" is not an operation, it is what has
        // already happened once an entry is no longer in the live set.
        var live = new List<ResourceAllocation> { Alloc(16, 64, 0, 1, 2, 3) };
        Assert.False(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 1), out _));

        live.Clear();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 1), out var got));
        Assert.Equal(new[] { 0 }, got.GpuIndices);
    }

    [Theory]
    [InlineData(17, 1, 0)]   // cores
    [InlineData(1, 65, 0)]   // memory
    [InlineData(1, 1, 5)]    // gpus
    public void TryFit_RefusesRequestsLargerThanFree(int cores, int mem, int gpus)
    {
        Assert.False(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(cores, mem, gpus), out _));
    }

    [Fact]
    public void TryFit_AssignsLowestFreeGpuIndices_AndTheyAreDisjoint()
    {
        var live = new List<ResourceAllocation>();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var first));
        live.Add(first);
        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var second));

        Assert.Equal(new[] { 0, 1 }, first.GpuIndices);
        Assert.Equal(new[] { 2, 3 }, second.GpuIndices);
        Assert.Empty(first.GpuIndices.Intersect(second.GpuIndices));
    }

    [Fact]
    public void TryFit_ReusesIndicesFreedByADroppedEntry()
    {
        var live = new List<ResourceAllocation> { Alloc(1, 1, 0, 1) };
        live.Clear();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 1), out var got));
        Assert.Equal(new[] { 0 }, got.GpuIndices);
    }

    [Fact]
    public void TryFit_RequestingZeroGpus_AssignsNone()
    {
        Assert.True(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(2, 4, 0), out var got));
        Assert.Empty(got.GpuIndices);
    }

    [Fact]
    public void CanEverFit_IsAboutTotals_NotCurrentUsage()
    {
        var full = new[] { Alloc(16, 64, 0, 1, 2, 3) };

        // Busy, but possible later.
        Assert.True(ResourceLedger.CanEverFit(Host, new ResourceRequest(16, 64, 4)));
        Assert.False(ResourceLedger.TryFit(Host, full, new ResourceRequest(16, 64, 4), out _));

        // Impossible on an empty host — must be rejected, never queued.
        Assert.False(ResourceLedger.CanEverFit(Host, new ResourceRequest(1, 1, 5)));
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ResourceLedgerTests"`
Expected: FAIL to compile — `ResourceLedger` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Refund/JobQueues/ResourceLedger.cs`:

```csharp
namespace Refund.JobQueues;

/// <summary>Everything a managed queue may hand out.</summary>
public readonly record struct ResourceTotals(int Cores, int MemoryGb, int Gpus);

/// <summary>What one job is asking for.</summary>
public readonly record struct ResourceRequest(int Cores, int MemoryGb, int Gpus);

/// <summary>What one job was given. GpuIndices are host device ids, not job-relative.</summary>
public sealed record ResourceAllocation(int Cores, int MemoryGb, IReadOnlyList<int> GpuIndices);

/// <summary>What is left over.</summary>
public readonly record struct LedgerSnapshot(
    int FreeCores, int FreeMemoryGb, IReadOnlyList<int> FreeGpuIndices);

/// <summary>
/// Pure resource arithmetic for a managed queue. Holds no state: callers pass both the totals and
/// the currently-live allocations on every call.
/// </summary>
/// <remarks>
/// Statelessness is the point. An incremental ledger — subtract on admit, add back on release —
/// leaks permanently if any one of the exit paths (finished, failed, aborted, killed at shutdown,
/// job deleted mid-flight) forgets to release, and the symptom is a queue that silently never
/// starts anything. Here "release" is not an operation at all: a job's resources are free the
/// moment its entry stops being in the live set the caller passes in.
///
/// It also means there is no configuration lifecycle to get wrong. ClusterQueue is constructed
/// before ReadFromJson hydrates its persisted totals, and an admin can edit them later; because
/// totals arrive per call, neither needs special handling.
/// </remarks>
public static class ResourceLedger
{
    public static LedgerSnapshot Compute(ResourceTotals totals, IEnumerable<ResourceAllocation> live)
    {
        int usedCores = 0, usedMemory = 0;
        var takenGpus = new HashSet<int>();

        foreach (var a in live)
        {
            usedCores += a.Cores;
            usedMemory += a.MemoryGb;
            foreach (var g in a.GpuIndices)
                takenGpus.Add(g);
        }

        var freeGpus = Enumerable.Range(0, totals.Gpus).Where(g => !takenGpus.Contains(g)).ToList();

        return new LedgerSnapshot(totals.Cores - usedCores, totals.MemoryGb - usedMemory, freeGpus);
    }

    public static bool TryFit(ResourceTotals totals,
                              IEnumerable<ResourceAllocation> live,
                              ResourceRequest request,
                              out ResourceAllocation allocation)
    {
        allocation = null;

        var snap = Compute(totals, live);

        if (request.Cores > snap.FreeCores ||
            request.MemoryGb > snap.FreeMemoryGb ||
            request.Gpus > snap.FreeGpuIndices.Count)
            return false;

        allocation = new ResourceAllocation(
            request.Cores, request.MemoryGb, snap.FreeGpuIndices.Take(request.Gpus).ToList());

        return true;
    }

    /// <summary>
    /// Whether a request could ever be satisfied on an empty host. Distinguishes "busy now, retry"
    /// from "impossible, fail the job" — without this a job asking for more than the host has would
    /// sit in Waiting forever with no explanation.
    /// </summary>
    public static bool CanEverFit(ResourceTotals totals, ResourceRequest request) =>
        request.Cores <= totals.Cores &&
        request.MemoryGb <= totals.MemoryGb &&
        request.Gpus <= totals.Gpus;
}
```

- [ ] **Step 4: Run and watch them pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ResourceLedgerTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Refund/JobQueues/ResourceLedger.cs Refund.Tests/JobQueues/ResourceLedgerTests.cs
git commit -m "$(cat <<'EOF'
feat: add ResourceLedger for managed queue admission

Pure arithmetic over totals and the currently-live allocations, both passed
per call. Statelessness is deliberate: an incremental ledger leaks permanently
if any exit path forgets to release, and the symptom is a queue that silently
stops starting jobs. Here a job's resources are free the moment its entry
leaves the live set, so there is no release call to forget.

CanEverFit separates "busy now" from "impossible", so an over-large request
can be failed with an explanation instead of queued forever.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `AdmissionResult` and `JobQueue.CanAdmit`

A `bool` cannot carry the needed distinction. `false` means "retry next tick", but an impossible request must fail permanently. Throwing does not work either: `HandleWaitingState`'s catch writes the error log and returns *without* changing status (`QueueRepository.StateHandlers.cs:70-75`), so the job would stay `Waiting` and re-log every daemon tick forever.

This task adds the type and the default; the guard that consumes it lands in Task 8.

**Files:**
- Modify: `Refund/DataModel/JobQueue.cs`
- Test: `Refund.Tests/JobQueues/AdmissionResultTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces, used by Tasks 7 and 8:
  - `abstract record AdmissionResult` with nested `Admit`, `Busy`, `Reject(string Reason)`
  - `AdmissionResult.Admitted` / `AdmissionResult.IsBusy` — shared singletons
  - `virtual AdmissionResult JobQueue.CanAdmit(Job job)` returning `Admitted`

- [ ] **Step 1: Write the failing test**

Create `Refund.Tests/JobQueues/AdmissionResultTests.cs`:

```csharp
using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class AdmissionResultTests
{
    [Fact]
    public void ClusterQueue_WithAnExternalScheduler_AlwaysAdmits()
    {
        // Every non-managed queue must behave exactly as before this feature existed.
        var queue = new ClusterQueue((_, _) => { }) { SchedulerType = ClusterScheduler.Slurm };
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(null));
    }

    [Fact]
    public void Reject_CarriesItsReason()
    {
        var rejected = new AdmissionResult.Reject("needs 4 GPUs, queue has 1");
        Assert.Contains("4 GPUs", rejected.Reason);
    }

    [Fact]
    public void AdmitAndBusy_AreSharedSingletons()
    {
        // Returned for every waiting job on every daemon tick; do not allocate per call.
        Assert.Same(AdmissionResult.Admitted, AdmissionResult.Admitted);
        Assert.Same(AdmissionResult.IsBusy, AdmissionResult.IsBusy);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AdmissionResultTests"`
Expected: FAIL to compile — `AdmissionResult` does not exist.

- [ ] **Step 3: Add the type**

In `Refund/DataModel/JobQueue.cs`, inside `namespace Refund.DataModel`, after the `ClusterScheduler` enum:

```csharp
    /// <summary>
    /// The outcome of asking a queue whether it can start a job right now.
    /// </summary>
    /// <remarks>
    /// A boolean cannot express the difference that matters. "No, resources are busy" must leave the
    /// job Waiting so the daemon retries; "no, this can never run here" must fail it once, with a
    /// reason. Throwing is not an alternative — HandleWaitingState's catch logs and returns without
    /// changing job status, so an exception would leave the job Waiting and re-log every tick.
    /// </remarks>
    public abstract record AdmissionResult
    {
        private AdmissionResult() { }

        /// <summary>Start the job now.</summary>
        public sealed record Admit : AdmissionResult;

        /// <summary>Resources are in use. Leave the job Waiting; the daemon will ask again.</summary>
        public sealed record Busy : AdmissionResult;

        /// <summary>The job can never run on this queue. Fail it once with this reason.</summary>
        public sealed record Reject(string Reason) : AdmissionResult;

        /// <summary>Shared instance — returned for every waiting job on every tick.</summary>
        public static readonly AdmissionResult Admitted = new Admit();

        /// <summary>Shared instance — returned for every waiting job on every tick.</summary>
        public static readonly AdmissionResult IsBusy = new Busy();
    }
```

- [ ] **Step 4: Add the virtual to `JobQueue`**

In the `JobQueue` class body, next to `SubmitJob` / `CheckStatus` / `AbortJob`:

```csharp
        /// <summary>
        /// Whether this queue can start <paramref name="job"/> right now.
        /// Queues backed by an external scheduler always admit — the scheduler does the arbitration.
        /// </summary>
        public virtual AdmissionResult CanAdmit(Job job) => AdmissionResult.Admitted;
```

- [ ] **Step 5: Run and watch it pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AdmissionResultTests"`
Expected: PASS, 3 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Refund/DataModel/JobQueue.cs Refund.Tests/JobQueues/AdmissionResultTests.cs
git commit -m "$(cat <<'EOF'
feat: add AdmissionResult and JobQueue.CanAdmit

Queues backed by an external scheduler always admit; the scheduler arbitrates.
A managed queue will not, and needs to distinguish "busy, ask again" from
"this can never run here". A bool cannot, and throwing is worse:
HandleWaitingState's catch logs without changing job status, so an exception
would leave the job Waiting and re-log on every daemon tick.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `ManagedExecutor` — entry table, admission, reconciliation

The heart of the feature. Process *spawning* lands in Task 5; this task defines the seam (`IManagedProcess`) and gets the liveness rules right against a fake, because those rules are where the resource bugs live.

**The liveness rule, and why it is ordered this way:**

```
live  ==  process is running
      ||  (job is active && entry has no process yet)
```

A running process **always** keeps its allocation, whatever the job's status says. A job can go terminal while its process is alive — `HandleAbortingState` force-marks a job `Aborted` after 30 seconds regardless of reported status (`QueueRepository.StateHandlers.cs:327-328`), and daemon error paths mark jobs `Failed` independently. Retiring the entry then would hand a live job's GPU to another job. Reconciliation instead kills the tree and waits for exit.

Job status can retire only an entry with **no** process — the abandoned reservation left by a staging failure.

**Files:**
- Create: `Refund/JobQueues/IManagedProcess.cs`
- Create: `Refund/JobQueues/ManagedExecutor.cs`
- Test: `Refund.Tests/JobQueues/ManagedExecutorTests.cs`

**Interfaces:**
- Consumes: `ResourceTotals`, `ResourceRequest`, `ResourceAllocation`, `ResourceLedger` (Task 2); `AdmissionResult` (Task 3).
- Produces, used by Tasks 5–8:
  - `interface IManagedProcess { int Pid; DateTime StartTime; bool HasExited; int ExitCode; void KillTree(); Task WaitForExitAsync(CancellationToken ct = default); }`
  - `ManagedExecutor.RequestFor(Job) → ResourceRequest`
  - `ManagedExecutor.TryAdmit(Job, ResourceTotals) → AdmissionResult`
  - `ManagedExecutor.Attach(Job, IManagedProcess)`
  - `ManagedExecutor.Reap()`
  - `ManagedExecutor.GetStatus(Job) → ClusterJobStatus`
  - `ManagedExecutor.Kill(Job)`
  - `ManagedExecutor.LiveAllocations(Job excludeSelf = null) → IEnumerable<ResourceAllocation>`
  - `ManagedExecutor.HasEntries(Func<Job,bool> predicate) → bool`

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/JobQueues/ManagedExecutorTests.cs`:

```csharp
using Refund.DataModel;
using Refund.JobQueues;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class ManagedExecutorTests
{
    private static readonly object _populateLock = new();

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    private static readonly ResourceTotals Host = new(Cores: 8, MemoryGb: 32, Gpus: 2);

    private static Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Space = new Space { RootDirectory = "/tmp/relay-test" },
                             Status = JobStatus.Waiting };
    }

    /// <summary>Stands in for a real OS process so liveness can be driven deterministically.</summary>
    private sealed class FakeProcess : IManagedProcess
    {
        public int Pid { get; init; } = 4242;
        public DateTime StartTime { get; init; } = new(2026, 1, 1);
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public bool WasKilled { get; private set; }

        public void Exit(int code) { ExitCode = code; HasExited = true; }
        public void KillTree() { WasKilled = true; }
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void RequestFor_MultipliesCoresByProcessCount_ButTakesMemoryAsATotal()
    {
        // CoreCount is documented as cores *per process* (Job.cs:347); MemoryGb is already a total
        // in every override. Conflating them silently over- or under-books the host.
        var job = NewJob();
        var request = ManagedExecutor.RequestFor(job);

        Assert.Equal(job.ProcessCount * job.CoreCount, request.Cores);
        Assert.Equal(job.MemoryGb, request.MemoryGb);
        Assert.Equal(job.GpuCount, request.Gpus);
    }

    [Fact]
    public void TryAdmit_WhenResourcesAreFree_Admits()
    {
        var executor = new ManagedExecutor();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), Host));
    }

    [Fact]
    public void TryAdmit_WhenRequestExceedsTotals_RejectsPermanently()
    {
        var executor = new ManagedExecutor();
        var tiny = new ResourceTotals(Cores: 0, MemoryGb: 0, Gpus: 0);

        var result = executor.TryAdmit(NewJob(), tiny);

        var reject = Assert.IsType<AdmissionResult.Reject>(result);
        Assert.Contains("never", reject.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAdmit_WhenHostIsBusy_ReportsBusyNotReject()
    {
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var first = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(first, oneCore));
        executor.Attach(first, new FakeProcess());

        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), oneCore));
    }

    [Fact]
    public void AdmittedButNeverLaunched_DoesNotLeakOnceTheJobFails()
    {
        // The staging-failure path: TryAdmit reserved, but PrepareAndWriteScript threw inside
        // SubmitJob's Task.Run, so no process ever appeared. Reconciling only on Process.HasExited
        // would strand this reservation forever.
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var stuck = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(stuck, oneCore));

        stuck.Status = JobStatus.Failed;
        executor.Reap();

        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), oneCore));
    }

    [Fact]
    public void TerminalJobWithALiveProcess_KeepsItsAllocationAndIsKilled()
    {
        // HandleAbortingState force-marks a job Aborted after 30s whether or not the kill landed.
        // Freeing here would hand a still-computing job's GPU to someone else.
        var executor = new ManagedExecutor();
        var oneGpu = new ResourceTotals(Cores: 8, MemoryGb: 32, Gpus: 1);

        var running = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(running, oneGpu));
        var process = new FakeProcess();
        executor.Attach(running, process);

        running.Status = JobStatus.Aborted;
        executor.Reap();

        Assert.True(process.WasKilled);
        Assert.Single(executor.LiveAllocations());          // still held
        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), oneGpu));

        process.Exit(137);
        executor.Reap();

        Assert.Empty(executor.LiveAllocations());           // released only after exit
    }

    [Theory]
    [InlineData(0, ClusterJobStatus.Finished)]
    [InlineData(3, ClusterJobStatus.Failed)]
    public void GetStatus_MapsExitCode(int exitCode, ClusterJobStatus expected)
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        process.Exit(exitCode);
        executor.Reap();

        Assert.Equal(expected, executor.GetStatus(job));
    }

    [Fact]
    public void GetStatus_AdmittedButNotLaunched_IsPending()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);

        Assert.Equal(ClusterJobStatus.Pending, executor.GetStatus(job));
    }

    [Fact]
    public void GetStatus_WhileRunning_IsRunning()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        executor.Attach(job, new FakeProcess());

        Assert.Equal(ClusterJobStatus.Running, executor.GetStatus(job));
    }

    [Fact]
    public void GetStatus_UntrackedJob_IsFailed()
    {
        // After a restart the table is empty, so any job the daemon still believes is Running
        // must be reported Failed rather than hanging forever.
        Assert.Equal(ClusterJobStatus.Failed, new ManagedExecutor().GetStatus(NewJob()));
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedExecutorTests"`
Expected: FAIL to compile — `IManagedProcess` and `ManagedExecutor` do not exist.

- [ ] **Step 3: Define the process seam**

Create `Refund/JobQueues/IManagedProcess.cs`:

```csharp
namespace Refund.JobQueues;

/// <summary>
/// The bit of an OS process the executor needs. Exists so the reconciliation rules — which is where
/// the resource-accounting bugs live — can be tested deterministically without spawning anything.
/// </summary>
public interface IManagedProcess
{
    int Pid { get; }

    /// <summary>Start time, paired with the pid to survive pid recycling across a Relay restart.</summary>
    DateTime StartTime { get; }

    bool HasExited { get; }

    /// <summary>Only meaningful once <see cref="HasExited"/> is true.</summary>
    int ExitCode { get; }

    /// <summary>Terminate the whole tree, not just the direct child — jobs launch mpirun.</summary>
    void KillTree();

    Task WaitForExitAsync(CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the executor**

Create `Refund/JobQueues/ManagedExecutor.cs`:

```csharp
using Refund.DataModel;

namespace Refund.JobQueues;

/// <summary>
/// Runs jobs as local processes and accounts for the host's resources. Exactly one instance exists
/// per Relay host, owned by QueueRepository — a host has one set of GPUs, so it has one ledger.
/// </summary>
public sealed class ManagedExecutor
{
    private sealed class Entry
    {
        public required ResourceAllocation Allocation { get; init; }
        public IManagedProcess Process { get; set; }
        public int? ExitCode { get; set; }
    }

    private readonly Dictionary<Job, Entry> _entries = new();
    private readonly object _sync = new();

    /// <summary>
    /// Cores are per-process in the job model (<c>Job.CoreCount</c>, Job.cs:347) while memory is
    /// already a total in every override. Conflating the two silently over- or under-books the host.
    /// </summary>
    public static ResourceRequest RequestFor(Job job) =>
        new(job.ProcessCount * job.CoreCount, job.MemoryGb, job.GpuCount);

    public AdmissionResult TryAdmit(Job job, ResourceTotals totals)
    {
        var request = RequestFor(job);

        if (!ResourceLedger.CanEverFit(totals, request))
            return new AdmissionResult.Reject(
                $"Job needs {request.Cores} cores, {request.MemoryGb} GB and {request.Gpus} GPU(s); " +
                $"this queue has {totals.Cores} cores, {totals.MemoryGb} GB and {totals.Gpus} GPU(s), " +
                "so it can never run here.");

        lock (_sync)
        {
            Reconcile();

            if (_entries.ContainsKey(job))
                return AdmissionResult.Admitted;   // already admitted; idempotent

            if (!ResourceLedger.TryFit(totals, LiveAllocationsLocked(), request, out var allocation))
                return AdmissionResult.IsBusy;

            _entries[job] = new Entry { Allocation = allocation };
            return AdmissionResult.Admitted;
        }
    }

    /// <summary>The GPU indices this job was given, for CUDA_VISIBLE_DEVICES. Empty if untracked.</summary>
    public IReadOnlyList<int> GpuIndicesFor(Job job)
    {
        lock (_sync)
            return _entries.TryGetValue(job, out var e) ? e.Allocation.GpuIndices : Array.Empty<int>();
    }

    public void Attach(Job job, IManagedProcess process)
    {
        lock (_sync)
            if (_entries.TryGetValue(job, out var entry))
                entry.Process = process;
    }

    public void Reap()
    {
        lock (_sync)
            Reconcile();
    }

    public IEnumerable<ResourceAllocation> LiveAllocations()
    {
        lock (_sync)
        {
            Reconcile();
            return LiveAllocationsLocked().ToList();
        }
    }

    public bool HasEntries(Func<Job, bool> predicate)
    {
        lock (_sync)
        {
            Reconcile();
            return _entries.Keys.Any(predicate);
        }
    }

    public ClusterJobStatus GetStatus(Job job)
    {
        lock (_sync)
        {
            Reconcile();

            if (!_entries.TryGetValue(job, out var entry))
                return ClusterJobStatus.Failed;     // untracked: nothing is running this

            if (entry.ExitCode is { } code)
                return code == 0 ? ClusterJobStatus.Finished : ClusterJobStatus.Failed;

            return entry.Process == null ? ClusterJobStatus.Pending : ClusterJobStatus.Running;
        }
    }

    public void Kill(Job job)
    {
        lock (_sync)
            if (_entries.TryGetValue(job, out var entry))
                entry.Process?.KillTree();
    }

    /// <summary>
    /// The single reconciliation pass. Everything that frees a resource happens here, which is why
    /// there is no Release() for any exit path to forget.
    /// </summary>
    private void Reconcile()
    {
        foreach (var (job, entry) in _entries.ToList())
        {
            if (entry.Process is { HasExited: true } exited)
            {
                entry.ExitCode ??= exited.ExitCode;         // resources free from here (see IsLive)
                if (!IsJobActive(job))
                    _entries.Remove(job);                   // job settled too; forget it entirely
                continue;
            }

            if (IsJobActive(job))
                continue;

            if (entry.Process != null)
            {
                // Terminal job, live process. Never free here: HandleAbortingState force-marks a job
                // Aborted after 30s whether or not the kill landed, and releasing would hand a
                // still-computing job's GPU to someone else. Kill, and wait for a later pass.
                entry.Process.KillTree();
                continue;
            }

            _entries.Remove(job);                           // abandoned reservation, no process
        }
    }

    private static bool IsJobActive(Job job) =>
        job.Status.IsUnsettled() || job.Status == JobStatus.Waiting;

    /// <summary>An entry holds resources until its process has exited; see Reconcile.</summary>
    private IEnumerable<ResourceAllocation> LiveAllocationsLocked() =>
        _entries.Values.Where(e => e.ExitCode == null).Select(e => e.Allocation);
}
```

- [ ] **Step 5: Run and watch them pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedExecutorTests"`
Expected: PASS, 10 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Refund/JobQueues/IManagedProcess.cs Refund/JobQueues/ManagedExecutor.cs \
        Refund.Tests/JobQueues/ManagedExecutorTests.cs
git commit -m "$(cat <<'EOF'
feat: add ManagedExecutor admission and reconciliation

One entry table per host, with a single reconciliation pass that is the only
place a resource is freed — so no exit path has a Release() call to forget.

Two rules carry the weight. A running process always keeps its allocation
whatever the job's status says, because HandleAbortingState force-marks a job
Aborted after 30s whether or not the kill landed; reconciliation kills the
tree and waits for exit instead of freeing. Job status can retire only an
entry with no process, which is the reservation left behind when staging
throws inside SubmitJob's Task.Run.

Process spawning is behind IManagedProcess so these rules can be tested
deterministically rather than by racing real processes.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Spawning, output pumps, and terminal-status ordering

Implements `IManagedProcess` against a real OS process, in its own group via `setsid` so the whole tree can be signalled by group id even without a live handle (Task 6 needs that).

Two details are easy to get wrong:

- **stdout/stderr go through .NET redirection, pumped line-by-line with a flush.** Not shell redirection — job directory paths would need quoting. Not `CopyToAsync` — its 80 KiB buffer would stall `TrackProgressLogs`, which tails these files for the live UI.
- **Terminal status waits for the pumps.** `Process.HasExited` can go true while buffered output is still unwritten, and `HandleJobCompletion` runs final progress tracking then dequeues — so reporting `Finished` too early silently drops a job's last log lines.
- **`setsid` is Linux-only; macOS is the dev environment.** Verified absent from macOS's base system (no `/usr/bin/setsid`, not on `PATH`). Without it the child inherits **Relay's own process group**, so `kill(-pgid)` would kill Relay. `Pgid` is therefore `int?`, non-null only when we actually created a group, and a null must never become a group kill — including in Task 6's startup sweep. macOS falls back to `Process.Kill(entireProcessTree: true)`, which works for live kills but cannot clean up after a Relay crash. That limitation is dev-only and is recorded in the README in Task 11.

**Files:**
- Create: `Refund/JobQueues/SystemManagedProcess.cs`
- Modify: `Refund/JobQueues/ManagedExecutor.cs`
- Test: `Refund.Tests/JobQueues/ManagedExecutorProcessTests.cs`

**Interfaces:**
- Consumes: `IManagedProcess`, `ManagedExecutor` (Task 4).
- Produces, used by Task 7:
  - `ManagedExecutor.Launch(Job job, string scriptPath, string workingDirectory) → IManagedProcess`
  - `SystemManagedProcess.Start(scriptPath, workingDirectory, gpuIndices, stdOutPath, stdErrPath) → SystemManagedProcess`
  - `SystemManagedProcess.Pgid`

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/JobQueues/ManagedExecutorProcessTests.cs`:

```csharp
using Refund.DataModel;
using Refund.JobQueues;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class ManagedExecutorProcessTests : IDisposable
{
    private static readonly object _populateLock = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-managed-" + Guid.NewGuid());

    public ManagedExecutorProcessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    private Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Space = new Space { RootDirectory = _dir }, Status = JobStatus.Running };
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_dir, "submit.sh");
        File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
        return path;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    [Fact]
    public async Task Launch_RunsTheScript_AndReportsFinishedOnCleanExit()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("exit 0"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        Assert.Equal(ClusterJobStatus.Finished, executor.GetStatus(job));
    }

    [Fact]
    public async Task Launch_NonZeroExit_ReportsFailed()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("exit 3"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Failed);

        Assert.Equal(ClusterJobStatus.Failed, executor.GetStatus(job));
    }

    [Fact]
    public async Task TerminalStatus_IsWithheldUntilOutputHasBeenFlushed()
    {
        // Process.HasExited can go true while buffered output is unwritten. HandleJobCompletion
        // runs final progress tracking and then dequeues, so reporting Finished early loses the
        // job's last log lines.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("for i in $(seq 1 500); do echo line-$i; done"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        var written = await File.ReadAllTextAsync(job.PathStdOut);
        Assert.Contains("line-1\n", written);
        Assert.Contains("line-500", written);
    }

    [Fact]
    public async Task Kill_TerminatesTheWholeTree_NotJustTheDirectChild()
    {
        // Jobs launch mpirun, which launches ranks. Killing only the shell orphans the real work.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var marker = Path.Combine(_dir, "child-alive");
        executor.Launch(job, WriteScript(
            $"( while true; do touch '{marker}'; sleep 0.1; done ) & wait"), _dir);

        await WaitUntil(() => File.Exists(marker));
        executor.Kill(job);
        await WaitUntil(() => executor.GetStatus(job) is ClusterJobStatus.Failed
                                                      or ClusterJobStatus.Finished);

        File.Delete(marker);
        await Task.Delay(500);
        Assert.False(File.Exists(marker), "grandchild survived the kill");
    }

    [Fact]
    public async Task Launch_ExportsAssignedGpuIndices()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 4));

        executor.Launch(job, WriteScript("echo \"visible=$CUDA_VISIBLE_DEVICES\""), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        var expected = string.Join(",", executor.GpuIndicesFor(job));
        Assert.Contains($"visible={expected}", await File.ReadAllTextAsync(job.PathStdOut));
    }

    [Fact]
    public void GroupKill_IsNeverUsedForAGroupWeDidNotCreate()
    {
        // Without setsid (macOS) the child inherits Relay's process group. Turning that pgid into
        // kill(-pgid) would terminate Relay itself. Assert the interlock directly: a null pgid, or
        // one that is not the child's own pid, must take the fallback path and never signal a group.
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 1234, pgid: null,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false);
        Assert.True(fallbackCalled);

        fallbackCalled = false;
        SystemManagedProcess.KillTree(pid: 1234, pgid: 1,      // pgid != pid: not ours
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false);
        Assert.True(fallbackCalled);
    }

    [Fact]
    public void KillTree_DoesNothingForAnAlreadyExitedProcess()
    {
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 1234, pgid: null,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => true);

        Assert.False(fallbackCalled);
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedExecutorProcessTests"`
Expected: FAIL to compile — `Launch` does not exist.

- [ ] **Step 3: Implement `SystemManagedProcess`**

Create `Refund/JobQueues/SystemManagedProcess.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Refund.JobQueues;

/// <summary>
/// A real OS process running one job's submission script, in its own process group.
/// </summary>
/// <remarks>
/// The group is what makes the tree killable. Jobs launch mpirun, which launches ranks; signalling
/// only the direct child orphans the real work. A group id also survives losing the Process handle,
/// which is what lets a restarted Relay clean up leftovers (see ManagedProcessRegistry).
/// </remarks>
public sealed class SystemManagedProcess : IManagedProcess
{
    private readonly Process _process;
    private readonly Task _pumps;

    public int Pid { get; }

    /// <summary>
    /// The process group we created for this job, or null when the platform has no
    /// <c>setsid</c> (macOS). Null means the child inherited <em>Relay's own</em> group, and
    /// group-signalling it would kill Relay — so a null here must never be turned into a
    /// group kill anywhere, including the startup leftover sweep.
    /// </summary>
    public int? Pgid { get; }

    public DateTime StartTime { get; }

    /// <summary>True only once the process has exited AND its output has been fully flushed.</summary>
    public bool HasExited => _process.HasExited && _pumps.IsCompleted;

    public int ExitCode => _process.ExitCode;

    private SystemManagedProcess(Process process, Task pumps, bool ownGroup)
    {
        _process = process;
        _pumps = pumps;
        Pid = process.Id;
        StartTime = process.StartTime;

        // setsid makes the child its own group leader, so pgid == pid by construction. If we did
        // not launch through setsid we have no group of our own — record null rather than guessing.
        Pgid = ownGroup ? process.Id : null;
    }

    /// <summary>
    /// setsid(1) is present on Linux but not in macOS's base system. Probed once: on Linux we get
    /// our own process group and can signal the whole tree by group id even after losing the
    /// handle; on macOS we fall back to .NET's tree walk, which cannot survive a Relay crash.
    /// </summary>
    private static readonly string SetsidPath =
        new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);

    public static SystemManagedProcess Start(string scriptPath,
                                             string workingDirectory,
                                             IReadOnlyList<int> gpuIndices,
                                             string stdOutPath,
                                             string stdErrPath)
    {
        bool ownGroup = SetsidPath != null;

        var info = new ProcessStartInfo
        {
            // With setsid the script gets a fresh process group, so the whole tree can be signalled
            // by group id. Without it (macOS) we run bash directly and rely on .NET's tree walk.
            FileName = ownGroup ? SetsidPath : "/bin/bash",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (ownGroup)
            info.ArgumentList.Add("/bin/bash");
        info.ArgumentList.Add(scriptPath);

        // Enforced, unlike cores and memory: CUDA renumbers these to 0..n-1, which is what jobs
        // already assume (AlignMiss.cs:294, WarpJobGpu.cs:183). Set here rather than injected into
        // the script so the template stays scheduler-agnostic.
        info.Environment["CUDA_VISIBLE_DEVICES"] = string.Join(",", gpuIndices);

        // Relay's own web-host variables would otherwise leak into compute processes.
        foreach (var key in info.Environment.Keys
                     .Where(k => k.StartsWith("ASPNETCORE_") || k.StartsWith("Kestrel__")).ToList())
            info.Environment.Remove(key);

        var process = new Process { StartInfo = info };
        process.Start();

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, stdOutPath),
            PumpAsync(process.StandardError, stdErrPath));

        return new SystemManagedProcess(process, pumps);
    }

    /// <summary>
    /// Line-by-line with a flush per line. CopyToAsync would buffer 80 KiB, which is far too coarse
    /// for TrackProgressLogs, which tails these files to drive the job card's live progress.
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var writer = new StreamWriter(path, append: true) { AutoFlush = true };

        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteLineAsync(line);
    }

    public void KillTree() => KillTree(Pid, Pgid, () => _process.Kill(entireProcessTree: true),
                                       () => _process.HasExited);

    /// <summary>
    /// Shared by the live path and the startup leftover sweep, which has no Process handle.
    /// </summary>
    /// <remarks>
    /// The <paramref name="pgid"/> null check is a safety interlock, not an optimisation. A null
    /// group means the child inherited Relay's group; <c>kill(-pgid)</c> would then take Relay down
    /// with it. Only a group we created ourselves is ever signalled as a group.
    /// </remarks>
    internal static void KillTree(int pid, int? pgid, Action fallbackKill, Func<bool> hasExited)
    {
        try
        {
            if (hasExited())
                return;

            if (pgid is { } group && group == pid)   // our own group: pgid == pid by construction
                Kill(-group, SIGKILL);               // negative pid signals the whole group
            else
                fallbackKill();                      // .NET walks the child tree
        }
        catch { /* already gone */ }
    }

    private const int SIGKILL = 9;

    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        await _process.WaitForExitAsync(ct);
        await _pumps;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);
}
```

- [ ] **Step 4: Add `Launch` to `ManagedExecutor`**

```csharp
    /// <summary>
    /// Spawns the job's script and attaches the resulting process to its (already admitted) entry.
    /// Throws if the job was not admitted first — a process must never run unaccounted for.
    /// </summary>
    public IManagedProcess Launch(Job job, string scriptPath, string workingDirectory)
    {
        IReadOnlyList<int> gpus;
        lock (_sync)
        {
            if (!_entries.ContainsKey(job))
                throw new InvalidOperationException(
                    $"Job {job.Id} was not admitted; refusing to launch it unaccounted for.");
            gpus = _entries[job].Allocation.GpuIndices;
        }

        var process = SystemManagedProcess.Start(
            scriptPath, workingDirectory, gpus, job.PathStdOut, job.PathStdErr);

        Attach(job, process);
        return process;
    }
```

- [ ] **Step 5: Run and watch them pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedExecutorProcessTests"`
Expected: PASS, 7 tests — on both Linux and macOS. If the tree-kill test fails on macOS, the fallback is not reaching the grandchild; fix the fallback rather than skipping the test.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Refund/JobQueues/SystemManagedProcess.cs Refund/JobQueues/ManagedExecutor.cs \
        Refund.Tests/JobQueues/ManagedExecutorProcessTests.cs
git commit -m "$(cat <<'EOF'
feat: spawn managed jobs as real processes

Each job runs under setsid in its own process group, so the whole tree —
jobs launch mpirun, which launches ranks — can be signalled by group id, and
can still be signalled after Relay has lost the handle.

Output is redirected through .NET and pumped line-by-line with a flush.
Shell redirection would need job paths quoted; CopyToAsync would buffer 80 KiB
and stall TrackProgressLogs, which tails these files for the live job card.

HasExited is deliberately "process exited AND pumps drained": reporting a
terminal status early lets HandleJobCompletion finalise and dequeue before the
last log lines are on disk.

setsid is absent from macOS, the dev environment, so Pgid is nullable and null
means "the child inherited Relay's own group". Only a group we created is ever
signalled as a group — otherwise kill(-pgid) would take Relay down with it.
macOS falls back to .NET's process-tree walk, which handles live kills but
cannot clean up after a Relay crash.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Containment — leftover registry and a shutdown hook that runs

"Managed jobs die with Relay" is a property that has to be built. Child processes do not die with their parent, and the hook the design first named is **unreachable**: `QueueRepository.Dispose(bool)` (`:316`) is never called, because `DataManager` implements no disposal across any of its twelve partial files and is registered as `AddSingleton<DataManager>(new DataManager(relayOptions))` (`Relay/Program.cs:88`) — an externally-constructed instance, which the DI container does not dispose. There are no `ApplicationStopping` hooks.

Three parts: a persisted registry, a startup sweep, and a shutdown path that actually executes.

**Files:**
- Create: `Refund/JobQueues/ManagedProcessRegistry.cs`
- Modify: `Refund/JobQueues/ManagedExecutor.cs`
- Test: `Refund.Tests/JobQueues/ManagedProcessRegistryTests.cs`

**Interfaces:**
- Consumes: `IManagedProcess`, `SystemManagedProcess.KillTree` (Tasks 4–5).
- Produces, used by Tasks 8 and 9:
  - `record ManagedProcessRecord(int JobId, int Pid, int? Pgid, long StartTimeTicks)`
  - `ManagedProcessRegistry(string path)` with `Record(...)`, `Forget(int jobId)`, `Load()`, `Clear()`
  - `static int ManagedProcessRegistry.KillLeftovers(string path, Func<int, DateTime?> startTimeOf)`
  - `ManagedExecutor.BeginShutdown()`, `ManagedExecutor.KillAllAsync()`

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/JobQueues/ManagedProcessRegistryTests.cs`:

```csharp
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ManagedProcessRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-registry-" + Guid.NewGuid());
    private string Path_ => System.IO.Path.Combine(_dir, "managed-processes.json");

    public ManagedProcessRegistryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RecordsSurviveAReload()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(JobId: 7, Pid: 111, Pgid: 111, StartTimeTicks: 999));

        var reloaded = new ManagedProcessRegistry(Path_).Load();

        var record = Assert.Single(reloaded);
        Assert.Equal(7, record.JobId);
        Assert.Equal(111, record.Pid);
        Assert.Equal(111, record.Pgid);
        Assert.Equal(999, record.StartTimeTicks);
    }

    [Fact]
    public void Forget_RemovesOnlyThatJob()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 111, 111, 5));
        registry.Record(new ManagedProcessRecord(2, 222, 222, 6));

        registry.Forget(1);

        Assert.Equal(2, Assert.Single(new ManagedProcessRegistry(Path_).Load()).JobId);
    }

    [Fact]
    public void CorruptFile_LoadsAsEmptyRatherThanThrowing()
    {
        // A half-written file after a crash must not stop Relay from starting.
        File.WriteAllText(Path_, "{ not json");
        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWhoseStartTimeNoLongerMatches()
    {
        // Pids are recycled. Killing on pid alone could terminate an unrelated process that
        // happened to inherit the number after a crash.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(JobId: 1, Pid: 4242, Pgid: 4242, StartTimeTicks: 1000));

        var killed = ManagedProcessRegistry.KillLeftovers(
            Path_, startTimeOf: _ => new DateTime(9999));   // live, but a different process

        Assert.Equal(0, killed);
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWithNoLiveProcess()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 4242, 4242, 1000));

        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(Path_, startTimeOf: _ => null));
    }

    [Fact]
    public void KillLeftovers_ClearsTheFileAfterSweeping()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 4242, 4242, 1000));

        ManagedProcessRegistry.KillLeftovers(Path_, startTimeOf: _ => null);

        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedProcessRegistryTests"`
Expected: FAIL to compile — `ManagedProcessRegistry` does not exist.

- [ ] **Step 3: Implement the registry**

Create `Refund/JobQueues/ManagedProcessRegistry.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace Refund.JobQueues;

/// <summary>One launched job, identified well enough to be killed after a Relay restart.</summary>
/// <param name="Pgid">Null when the platform had no setsid; see SystemManagedProcess.Pgid.</param>
public record ManagedProcessRecord(int JobId, int Pid, int? Pgid, long StartTimeTicks);

/// <summary>
/// Persists which processes a managed queue launched, so leftovers from a crashed Relay can be
/// killed at the next startup.
/// </summary>
/// <remarks>
/// Graceful shutdown cannot cover SIGKILL or a hard crash, and an orphan holding a GPU makes every
/// later job on a single-GPU host wait or be rejected. Identity is pid <em>plus start time</em>:
/// pids are recycled, and killing on pid alone could take out an unrelated process.
/// </remarks>
public sealed class ManagedProcessRegistry
{
    private readonly string _path;
    private readonly object _sync = new();

    public ManagedProcessRegistry(string path) => _path = path;

    public IReadOnlyList<ManagedProcessRecord> Load()
    {
        lock (_sync)
            return LoadLocked();
    }

    private List<ManagedProcessRecord> LoadLocked()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<ManagedProcessRecord>();

            return JsonSerializer.Deserialize<List<ManagedProcessRecord>>(File.ReadAllText(_path))
                   ?? new List<ManagedProcessRecord>();
        }
        catch
        {
            // A half-written file after a crash must never stop Relay from starting.
            return new List<ManagedProcessRecord>();
        }
    }

    public void Record(ManagedProcessRecord record)
    {
        lock (_sync)
        {
            var all = LoadLocked();
            all.RemoveAll(r => r.JobId == record.JobId);
            all.Add(record);
            SaveLocked(all);
        }
    }

    public void Forget(int jobId)
    {
        lock (_sync)
        {
            var all = LoadLocked();
            all.RemoveAll(r => r.JobId == jobId);
            SaveLocked(all);
        }
    }

    public void Clear()
    {
        lock (_sync)
            SaveLocked(new List<ManagedProcessRecord>());
    }

    private void SaveLocked(List<ManagedProcessRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var tmp = _path + ".tmp." + Environment.ProcessId;
        File.WriteAllText(tmp, JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Kills every recorded process that is still alive and still the same process, then clears the
    /// file. Returns how many were killed. Call once at startup, before any job is admitted.
    /// </summary>
    /// <param name="startTimeOf">
    /// Start time of the live process with this pid, or null if no such process exists. Injected so
    /// the recycling logic is testable without spawning anything.
    /// </param>
    public static int KillLeftovers(string path, Func<int, DateTime?> startTimeOf)
    {
        var registry = new ManagedProcessRegistry(path);
        int killed = 0;

        foreach (var record in registry.Load())
        {
            var actual = startTimeOf(record.Pid);

            if (actual == null)
                continue;                                        // already gone

            if (actual.Value.Ticks != record.StartTimeTicks)
                continue;                                        // pid recycled: not our process

            SystemManagedProcess.KillTree(
                record.Pid, record.Pgid,
                fallbackKill: () => KillByPid(record.Pid),
                hasExited: () => false);

            killed++;
        }

        registry.Clear();
        return killed;
    }

    /// <summary>Default start-time probe for production use.</summary>
    public static DateTime? LiveProcessStartTime(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime; }
        catch { return null; }
    }

    private static void KillByPid(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
    }
}
```

- [ ] **Step 4: Add shutdown ordering to `ManagedExecutor`**

An admitted entry can have no `Process` yet because its staging task is still writing the script. A naive `KillAll` would find nothing to kill, return, and let that task launch a process *during* shutdown. So admission closes first.

Add to `ManagedExecutor`:

```csharp
    private volatile bool _shuttingDown;

    /// <summary>
    /// Stops admitting. Call before killing anything: an entry admitted but not yet launched has no
    /// process to find, and its staging task would otherwise spawn one after the sweep had passed.
    /// </summary>
    public void BeginShutdown() => _shuttingDown = true;

    /// <summary>Kills every tracked process tree and waits for them to actually exit.</summary>
    public async Task KillAllAsync()
    {
        BeginShutdown();

        List<IManagedProcess> processes;
        lock (_sync)
            processes = _entries.Values.Select(e => e.Process).Where(p => p != null).ToList();

        foreach (var process in processes)
            process.KillTree();

        await Task.WhenAll(processes.Select(p => p.WaitForExitAsync()));

        lock (_sync)
            _entries.Clear();
    }
```

Guard both entry points. At the top of `TryAdmit`:

```csharp
        if (_shuttingDown)
            return AdmissionResult.IsBusy;
```

At the top of `Launch`, before spawning:

```csharp
        if (_shuttingDown)
            throw new InvalidOperationException(
                $"Relay is shutting down; refusing to launch job {job.Id}.");
```

- [ ] **Step 5: Record and forget around launch**

Give `ManagedExecutor` an optional registry (constructor parameter `ManagedProcessRegistry registry = null`), write the record immediately after `SystemManagedProcess.Start` returns in `Launch`:

```csharp
        if (process is SystemManagedProcess system)
            _registry?.Record(new ManagedProcessRecord(
                job.Id, system.Pid, system.Pgid, system.StartTime.Ticks));
```

and drop it in `Reconcile`, in the branch that removes a settled entry:

```csharp
                if (!IsJobActive(job))
                {
                    _entries.Remove(job);
                    _registry?.Forget(job.Id);
                }
```

- [ ] **Step 6: Add the shutdown-cannot-launch test**

Append to `Refund.Tests/JobQueues/ManagedExecutorTests.cs`:

```csharp
    [Fact]
    public void OnceShutdownBegins_NothingNewIsAdmittedOrLaunched()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, Host));

        executor.BeginShutdown();

        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), Host));
        Assert.Throws<InvalidOperationException>(
            () => executor.Launch(job, "/tmp/does-not-matter.sh", "/tmp"));
    }
```

- [ ] **Step 7: Run and watch them pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedProcessRegistryTests|FullyQualifiedName~ManagedExecutorTests"`
Expected: PASS — 6 registry tests, 11 executor tests.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q`
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add Refund/JobQueues/ManagedProcessRegistry.cs Refund/JobQueues/ManagedExecutor.cs \
        Refund.Tests/JobQueues/ManagedProcessRegistryTests.cs \
        Refund.Tests/JobQueues/ManagedExecutorTests.cs
git commit -m "$(cat <<'EOF'
feat: kill leftover managed processes and order shutdown

Children do not die with their parent, so "managed jobs die with Relay" has to
be built. Graceful shutdown cannot cover SIGKILL or a crash, and an orphan
holding a GPU makes every later job on a single-GPU host wait or be rejected.

The registry persists pid, pgid and start time per launched job; the startup
sweep kills anything still alive whose start time still matches. Matching on
start time as well as pid is what makes this safe against pid recycling.

Shutdown closes admission before killing, because an entry admitted but not
yet launched has no process to find and its staging task would otherwise spawn
one after the sweep had already passed it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Wire `Managed` into `ClusterQueue`

Adds the enum member, the three resource properties, and the branch in submit / status / abort. Script composition is reused untouched — `PrepareAndWriteScript` (`ClusterQueue.cs:418`) already produces what is needed, and a managed template is just the existing one without the `#SBATCH`/`#FLUX` header.

**Files:**
- Modify: `Refund/DataModel/JobQueue.cs` (enum member)
- Modify: `Refund/JobQueues/ClusterQueue.cs`
- Modify: `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs`
- Test: `Refund.Tests/JobQueues/ManagedClusterQueueTests.cs`

**Interfaces:**
- Consumes: `ManagedExecutor` (Tasks 4–6), `AdmissionResult` (Task 3).
- Produces, used by Tasks 8–10:
  - `ClusterScheduler.Managed`
  - `ClusterQueue.ManagedCores` / `ManagedMemoryGb` / `ManagedGpus` (`[RelayProperty] int`)
  - `ClusterQueue.ManagedTotals → ResourceTotals`
  - `ClusterQueue.Executor` — settable once, by `QueueRepository`
  - `ClusterQueue.IsManaged → bool`

- [ ] **Step 1: Write the failing tests**

Create `Refund.Tests/JobQueues/ManagedClusterQueueTests.cs`:

```csharp
using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ManagedClusterQueueTests
{
    private static ClusterQueue Managed() => new ClusterQueue((_, _) => { })
    {
        SchedulerType = ClusterScheduler.Managed,
        ManagedCores = 8,
        ManagedMemoryGb = 32,
        ManagedGpus = 2,
    };

    [Fact]
    public void ManagedDefaults_AreSensibleForASingleWorkstation()
    {
        var queue = new ClusterQueue((_, _) => { });

        Assert.Equal(Environment.ProcessorCount, queue.ManagedCores);
        Assert.Equal(64, queue.ManagedMemoryGb);
        Assert.Equal(1, queue.ManagedGpus);
    }

    [Fact]
    public void ManagedProperties_RoundTripThroughJson()
    {
        var saved = Managed().ToJson();

        var loaded = new ClusterQueue((_, _) => { });
        loaded.ReadFromJson(saved, (_, _, _) => null);

        Assert.Equal(ClusterScheduler.Managed, loaded.SchedulerType);
        Assert.Equal(8, loaded.ManagedCores);
        Assert.Equal(32, loaded.ManagedMemoryGb);
        Assert.Equal(2, loaded.ManagedGpus);
    }

    [Fact]
    public void ManagedTotals_ReadTheQueuesCurrentValues()
    {
        // Read per call, never snapshotted: ClusterQueue is constructed before ReadFromJson
        // hydrates it, and an admin can edit the totals later.
        var queue = Managed();
        Assert.Equal(new ResourceTotals(8, 32, 2), queue.ManagedTotals);

        queue.ManagedGpus = 4;
        Assert.Equal(new ResourceTotals(8, 32, 4), queue.ManagedTotals);
    }

    [Fact]
    public void IsManaged_IsTrueOnlyForTheManagedScheduler()
    {
        Assert.True(Managed().IsManaged);
        Assert.False(new ClusterQueue((_, _) => { }) { SchedulerType = ClusterScheduler.Flux }
                     .IsManaged);
    }

    [Fact]
    public void ParsersAreNeverConsultedForAManagedQueue()
    {
        // There is no scheduler output to parse; reaching a parser means a wiring mistake.
        var queue = Managed();

        Assert.Throws<InvalidOperationException>(() => queue.ParseClusterJobId("anything"));
        Assert.Equal(ClusterJobStatus.Unknown, queue.ParseClusterJobStatus("anything"));
    }

    [Fact]
    public void CanAdmit_WithoutAnExecutor_RejectsRatherThanRunningUnaccounted()
    {
        // QueueRepository injects the host-wide executor. If that wiring is missing, failing loudly
        // beats silently spawning processes nobody is accounting for.
        var result = Managed().CanAdmit(null);

        var reject = Assert.IsType<AdmissionResult.Reject>(result);
        Assert.Contains("executor", reject.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedClusterQueueTests"`
Expected: FAIL to compile — `ClusterScheduler.Managed` does not exist.

- [ ] **Step 3: Add the enum member**

In `Refund/DataModel/JobQueue.cs`, in `ClusterScheduler`, after `Flux = 4`:

```csharp
        /// <summary>
        /// No external scheduler: Relay runs the job as a local process and accounts for the host's
        /// cores, memory and GPUs itself. Not a scheduler in the sense the other values are — the
        /// job ID and status parsers are never consulted for a managed queue.
        /// </summary>
        Managed = 6,
```

Use `6`, not `5`; `Custom = 5` already exists and its persisted value must not shift.

- [ ] **Step 4: Add the properties to `ClusterQueue`**

```csharp
    /// <summary>Total CPU cores a managed queue may hand out. Ignored unless SchedulerType is Managed.</summary>
    [RelayProperty]
    public int ManagedCores { get; set; } = Environment.ProcessorCount;

    /// <summary>Total memory in GB a managed queue may hand out. Ignored unless SchedulerType is Managed.</summary>
    [RelayProperty]
    public int ManagedMemoryGb { get; set; } = 64;

    /// <summary>Number of GPUs on this host. Ignored unless SchedulerType is Managed.</summary>
    [RelayProperty]
    public int ManagedGpus { get; set; } = 1;

    /// <summary>True when Relay schedules this queue's jobs itself.</summary>
    public bool IsManaged => SchedulerType == ClusterScheduler.Managed;

    /// <summary>
    /// Read fresh on every use rather than snapshotted: this object is constructed before
    /// ReadFromJson hydrates the persisted values, and the editor can change them later.
    /// </summary>
    public ResourceTotals ManagedTotals => new(ManagedCores, ManagedMemoryGb, ManagedGpus);

    /// <summary>
    /// The host-wide executor, injected by QueueRepository. Null on a queue that was constructed
    /// outside the repository (templates, copies, tests).
    /// </summary>
    public ManagedExecutor Executor { get; set; }
```

- [ ] **Step 5: Guard the parsers**

At the top of `ParseClusterJobId`:

```csharp
        if (IsManaged)
            throw new InvalidOperationException(
                "A managed queue has no scheduler output to parse; job IDs are process ids " +
                "assigned by ManagedExecutor. Reaching this is a wiring mistake.");
```

At the top of `ParseClusterJobStatus`:

```csharp
        if (IsManaged)
            return ClusterJobStatus.Unknown;
```

- [ ] **Step 6: Override `CanAdmit`**

```csharp
    public override AdmissionResult CanAdmit(Job job)
    {
        if (!IsManaged)
            return AdmissionResult.Admitted;      // the external scheduler arbitrates

        if (Executor == null)
            return new AdmissionResult.Reject(
                $"Queue \"{Alias}\" is managed but has no executor attached. This is a Relay wiring " +
                "fault, not a job problem; refusing to run the job unaccounted for.");

        return Executor.TryAdmit(job, ManagedTotals);
    }
```

- [ ] **Step 7: Branch submit, status and abort**

In `SubmitJob`, inside the existing `Task.Run` staging body, replace the submission step for the managed case. After `string scriptPath = await PrepareAndWriteScript(job, customValues);`:

```csharp
                    if (IsManaged)
                    {
                        var process = Executor.Launch(job, scriptPath, job.RunDirectory);
                        await job.WriteToLifecycleLog(
                            $"Launched locally as pid {process.Pid} on GPUs " +
                            $"[{string.Join(",", Executor.GpuIndicesFor(job))}]");

                        JobUpdateCallback(job, j => { j.ClusterJobId = process.Pid.ToString(); });
                        return;
                    }
```

At the top of `CheckStatus`, after the existing `StagingJobs` check:

```csharp
        if (IsManaged)
            return (Executor?.GetStatus(job) ?? ClusterJobStatus.Failed, "");
```

At the top of `AbortJob`, after `base.AbortJob(job)`:

```csharp
        if (IsManaged)
        {
            Executor?.Kill(job);
            return;
        }
```

- [ ] **Step 8: Expose the properties read-only**

In `Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs`:

```csharp
    /// <summary>Total CPU cores a managed queue may hand out.</summary>
    public int ManagedCores => _queue.ManagedCores;

    /// <summary>Total memory in GB a managed queue may hand out.</summary>
    public int ManagedMemoryGb => _queue.ManagedMemoryGb;

    /// <summary>Number of GPUs on this host.</summary>
    public int ManagedGpus => _queue.ManagedGpus;

    /// <summary>True when Relay schedules this queue's jobs itself.</summary>
    public bool IsManaged => _queue.IsManaged;
```

- [ ] **Step 9: Run and watch them pass**

Run: `dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ManagedClusterQueueTests"`
Expected: PASS, 6 tests.

- [ ] **Step 10: Run the whole suite and build the app**

```bash
dotnet test Refund.Tests/Refund.Tests.csproj --nologo -v q
dotnet build Relay/Relay.csproj --nologo -v q
```
Expected: all tests pass; build succeeds with 0 errors.

- [ ] **Step 11: Commit**

```bash
git add Refund/DataModel/JobQueue.cs Refund/JobQueues/ClusterQueue.cs \
        Refund/JobQueues/ReadOnly/ReadOnlyClusterQueue.cs \
        Refund.Tests/JobQueues/ManagedClusterQueueTests.cs
git commit -m "$(cat <<'EOF'
feat: add the Managed scheduler to ClusterQueue

Managed means no external scheduler: Relay launches the job and accounts for
the host itself. Script composition is reused unchanged — a managed template
is the existing one without the #SBATCH/#FLUX header.

Totals are read fresh on every use rather than snapshotted, because
ClusterQueue is constructed before ReadFromJson hydrates it and the editor can
change them later.

The job ID parser throws for a managed queue rather than returning something:
there is no scheduler output, so reaching it means a wiring mistake. Likewise
CanAdmit rejects when no executor is attached, which fails loudly instead of
spawning processes nobody is accounting for.

Managed is enum value 6; Custom already owns 5 and its persisted value must
not shift.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---
