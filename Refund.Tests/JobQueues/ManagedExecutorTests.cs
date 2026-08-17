using Warp.Tools;
using Refund.DataModel;
using Refund.JobQueues;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Class2DJob = Refund.Jobs.Refinement.Classes2D.Class2D.Class2D;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class ManagedExecutorTests : IDisposable
{
    private static readonly object _populateLock = new();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-executor-" + Guid.NewGuid());

    public ManagedExecutorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ManagedProcessRegistry NewRegistry() =>
        new(Path.Combine(_dir, "managed-processes.json"));

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    private static readonly ResourceTotals Host = new(Cores: 8, MemoryGb: 32, Gpus: 2);

    /// <summary>CreateMask: 1 process x 1 core, 8 GB, no GPU. Asserted outright in RequestFor tests.</summary>
    private static Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Status = JobStatus.Waiting };
    }

    /// <summary>Class2D with a GPU: 1 process x 1 core, 16 GB, 1 GPU.</summary>
    private static Job NewGpuJob()
    {
        EnsurePopulated();
        return new Class2DJob { UseGpu = true, Status = JobStatus.Waiting };
    }

    /// <summary>Stands in for a real OS process so liveness can be driven deterministically.</summary>
    private sealed class FakeProcess : IManagedProcess
    {
        public int Pid { get; init; } = 4242;
        public DateTime StartTime { get; init; } = new(2026, 1, 1);
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public bool WasKilled => KillCount > 0;
        public int KillCount { get; private set; }

        public void Exit(int code) { ExitCode = code; HasExited = true; }
        public void KillTree() { KillCount++; }
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A Job whose declared footprint the test controls, including nonsense values. It lives in the
    /// test assembly, so Job.PopulateTypes() — which scans only the Refund assembly — never sees it.
    /// </summary>
    private sealed class ConfigurableResourceJob : Job
    {
        public int Processes { get; init; } = 1;
        public int CoresPerProcess { get; init; } = 1;
        public int Memory { get; init; } = 1;
        public int Gpus { get; init; }

        public override int ProcessCount => Processes;
        public override int CoreCount => CoresPerProcess;
        public override int MemoryGb => Memory;
        public override int GpuCount => Gpus;

        public override int2 CardSquareCount { get; set; } = new int2(1, 1);
        public override string TypeGuid => "00000000-0000-0000-0000-000000000001";
        public override string TypeCategory => "Test.Stub";
        public override string TypeName => "Configurable stub";
        public override string TypeNameShort => "Stub";
        public override string TypeDescription => "Test stub with a caller-controlled footprint";
        public override JobQueueType QueueType => JobQueueType.CPU;
        public override Type ExpandedViewType => typeof(object);
    }

    #region RequestFor

    [Fact]
    public void RequestFor_ForASingleProcessCpuJob_MatchesItsDeclaredFootprint()
    {
        var job = NewJob();

        var request = ManagedExecutor.RequestFor(job);

        // Concrete numbers, not a restatement of the formula: CreateMask is 1 process of
        // NThreads=1 cores, a flat 8 GB, and no GPU.
        Assert.Equal(new ResourceRequest(Cores: 1, MemoryGb: 8, Gpus: 0), request);
    }

    [Fact]
    public void RequestFor_MultipliesCoresByProcessCount_ButTakesMemoryAsATotal()
    {
        // CoreCount is documented as cores *per process* (Job.cs:347); MemoryGb is already a total
        // in every override. Conflating them silently over- or under-books the host.
        EnsurePopulated();
        var job = new Class2DJob
        {
            Algorithm = Class2DAlgorithm.VDAM,   // the branch that actually launches MPI
            NProcesses = 4,
            NThreads = 6,
            UseGpu = true,
        };

        var request = ManagedExecutor.RequestFor(job);

        // 4 ranks x 6 threads. Distinguishes the product from ProcessCount alone (4),
        // CoreCount alone (6) and their sum (10).
        Assert.Equal(24, request.Cores);
        // 3 working ranks x 16 GB, already a total: not multiplied again by ProcessCount (192).
        Assert.Equal(48, request.MemoryGb);
        Assert.Equal(1, request.Gpus);

        Assert.Equal(job.ProcessCount * job.CoreCount, request.Cores);
        Assert.Equal(job.MemoryGb, request.MemoryGb);
        Assert.Equal(job.GpuCount, request.Gpus);
    }

    [Fact]
    public void RequestFor_ClampsNegativeDeclarationsToZero()
    {
        // ResourceLedger is deliberately permissive and will happily fit a negative request,
        // handing back an allocation that *adds* capacity to the live set. This is where that
        // is stopped. Both core factors are negative, so clamping only the product would let
        // -2 x -3 through as 6.
        var job = new ConfigurableResourceJob
        {
            Processes = -2, CoresPerProcess = -3, Memory = -32, Gpus = -1
        };

        Assert.Equal(new ResourceRequest(Cores: 0, MemoryGb: 0, Gpus: 0),
                     ManagedExecutor.RequestFor(job));
    }

    [Fact]
    public void NegativeRequest_CannotManufactureCapacity()
    {
        // Unclamped, this job's allocation of -4 cores / -32 GB would make the host report
        // *more* free capacity than it has, and the one-core host would admit two mask jobs.
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 8, Gpus: 0);

        var nonsense = new ConfigurableResourceJob
        {
            Processes = 1, CoresPerProcess = -4, Memory = -32, Gpus = 0, Status = JobStatus.Waiting
        };
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(nonsense, oneCore));
        executor.Attach(nonsense, new FakeProcess());

        var real = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(real, oneCore));
        executor.Attach(real, new FakeProcess());

        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), oneCore));
    }

    #endregion

    #region Admission

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
    public void TryAdmit_RejectionReasonNamesBothTheRequestAndTheHost()
    {
        // The daemon fails the job with this string and it is all the user gets, so it must not
        // report the request where it means the totals.
        var executor = new ManagedExecutor();
        var greedy = new ConfigurableResourceJob
        {
            Processes = 1, CoresPerProcess = 99, Memory = 7, Gpus = 0, Status = JobStatus.Waiting
        };

        var reject = Assert.IsType<AdmissionResult.Reject>(executor.TryAdmit(greedy, Host));

        // Ordered, so swapping the request and the totals in the message does not slip through:
        // the request is what the job "needs", the totals are what the queue "has".
        Assert.Matches(@"needs 99 cores\b.*\bhas 8 cores\b", reject.Reason);
        Assert.Matches(@"needs\b.*\b7 GB\b.*\bhas\b.*\b32 GB\b", reject.Reason);
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
    public void TryAdmit_ForAnAlreadyAdmittedJob_IsIdempotent()
    {
        // The daemon may re-offer the same Waiting job before it manages to launch. A second
        // reservation for one job would book the host twice and never be released.
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var job = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, oneCore));
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, oneCore));

        Assert.Single(executor.LiveAllocations());
    }

    #endregion

    #region Reconciliation

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

        Assert.Empty(executor.LiveAllocations());
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), oneCore));
    }

    [Fact]
    public void TerminalJobWithALiveProcess_KeepsItsAllocationAndIsKilled()
    {
        // HandleAbortingState force-marks a job Aborted after 30s whether or not the kill landed.
        // Freeing here would hand a still-computing job's GPU to someone else.
        var executor = new ManagedExecutor();
        var oneGpu = new ResourceTotals(Cores: 8, MemoryGb: 32, Gpus: 1);

        var running = NewGpuJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(running, oneGpu));
        var process = new FakeProcess();
        executor.Attach(running, process);

        running.Status = JobStatus.Aborted;
        executor.Reap();

        Assert.True(process.WasKilled);
        Assert.Single(executor.LiveAllocations());              // still held
        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewGpuJob(), oneGpu));

        process.Exit(137);
        executor.Reap();

        Assert.Empty(executor.LiveAllocations());               // released only after exit
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewGpuJob(), oneGpu));
    }

    [Fact]
    public void ActiveJobWithALiveProcess_IsLeftAlone()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        job.Status = JobStatus.Running;

        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, Host));
        var process = new FakeProcess();
        executor.Attach(job, process);

        executor.Reap();
        executor.Reap();

        Assert.False(process.WasKilled);
        Assert.Single(executor.LiveAllocations());
    }

    [Fact]
    public void ExitedProcess_FreesResourcesEvenWhileTheJobIsStillActive()
    {
        // The daemon has not polled yet, so the job is still Running while its process is gone.
        // Holding the allocation until the status catches up would idle the host for a whole tick.
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var job = NewJob();
        job.Status = JobStatus.Running;
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, oneCore));
        var process = new FakeProcess();
        executor.Attach(job, process);

        process.Exit(0);
        executor.Reap();

        Assert.Empty(executor.LiveAllocations());
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), oneCore));

        // ...but the entry survives, so the daemon's next poll can still read the exit code.
        Assert.Equal(ClusterJobStatus.Finished, executor.GetStatus(job));
    }

    [Fact]
    public void SettledJobWithAnExitedProcess_IsForgottenEntirely()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        process.Exit(0);
        job.Status = JobStatus.Finished;
        executor.Reap();

        Assert.False(executor.HasEntries(_ => true));
        Assert.Equal(ClusterJobStatus.Failed, executor.GetStatus(job));   // untracked again
    }

    [Fact]
    public void ACondemnedProcessIsSignalledOnce_NotOnEveryPass()
    {
        // Reconcile runs on every daemon poll and on every admission decision, and KillTree is
        // called under the host-wide lock. Re-signalling each pass means an unbounded number of
        // blocking-ish calls holding that lock for one dying job.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        job.Status = JobStatus.Aborted;
        executor.Reap();
        executor.Reap();
        executor.Reap();

        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public void RequeueingAJobWhoseProcessOutlivedTheAbort_NeitherOrphansItNorReusesItsBooking()
    {
        // The reachable double-process path. A job is force-marked Aborted after 30s whether or
        // not the kill landed; Aborted -> Building -> Waiting is a legal re-queue (Job.cs:1346).
        // If the old entry were handed back, the relaunch would inherit a stale allocation and
        // stale GPU indices, and attaching would overwrite — and so orphan — the live process.
        var executor = new ManagedExecutor();
        var oneGpu = new ResourceTotals(Cores: 8, MemoryGb: 32, Gpus: 1);

        var job = NewGpuJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, oneGpu));
        var stubborn = new FakeProcess();
        Assert.True(executor.Attach(job, stubborn));

        job.Status = JobStatus.Aborted;
        executor.Reap();
        Assert.True(stubborn.WasKilled);

        // The user re-queues it while the old process is still alive.
        job.Status = JobStatus.Building;
        executor.Reap();
        job.Status = JobStatus.Waiting;

        // Not admitted onto the stale booking...
        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(job, oneGpu));
        // ...the live process is not displaced...
        Assert.False(executor.Attach(job, new FakeProcess()));
        // ...it still holds its GPU, and the job cannot read those devices for a new launch.
        Assert.Single(executor.LiveAllocations());
        Assert.Empty(executor.GpuIndicesFor(job));

        // Once it finally dies the entry is retired and the job gets a fresh booking.
        stubborn.Exit(137);
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, oneGpu));

        var relaunched = new FakeProcess();
        Assert.True(executor.Attach(job, relaunched));
        Assert.Equal(new[] { 0 }, executor.GpuIndicesFor(job));
        Assert.Equal(ClusterJobStatus.Running, executor.GetStatus(job));
    }

    [Fact]
    public void RequeueingWithSpareCapacity_StillRefusesToDisplaceTheTrackedProcess()
    {
        // The one-GPU version of this scenario cannot pin the refusal: there, Busy comes from
        // resource exhaustion, so letting the stale entry fall through to a fresh allocation looks
        // identical. With headroom it does not. _entries is keyed by Job, so the fresh entry would
        // *replace* the one tracking the live process — the same orphan as an Attach overwrite,
        // reached through the ledger instead.
        var executor = new ManagedExecutor();
        var roomy = new ResourceTotals(Cores: 32, MemoryGb: 256, Gpus: 4);

        var job = NewGpuJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, roomy));
        var stubborn = new FakeProcess();
        Assert.True(executor.Attach(job, stubborn));

        job.Status = JobStatus.Aborted;
        executor.Reap();
        Assert.Equal(1, stubborn.KillCount);

        job.Status = JobStatus.Building;
        executor.Reap();
        job.Status = JobStatus.Waiting;

        // Three free GPUs and 31 free cores, and it still must not be admitted.
        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(job, roomy));
        Assert.False(executor.Attach(job, new FakeProcess()));
        Assert.Single(executor.LiveAllocations());

        // The entry is still the one bound to the original process: when that process dies the
        // entry retires. Had it been replaced by a fresh reservation, the job being Waiting would
        // keep the replacement alive and stubborn's exit would go unnoticed.
        stubborn.Exit(137);
        executor.Reap();
        Assert.False(executor.HasEntries(_ => true));
    }

    [Fact]
    public void CondemnationSurvivesTheJobBecomingActiveAgain()
    {
        // Isolates the stickiness: without it, the re-queued job's Waiting status makes the entry
        // "active" again, Reconcile stops killing it, and the leftover process is immortal.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        executor.Kill(job);
        job.Status = JobStatus.Waiting;
        executor.Reap();

        process.Exit(0);
        executor.Reap();

        Assert.False(executor.HasEntries(_ => true));   // retired despite the job being active
    }

    #endregion

    #region Self-reconciliation

    [Fact]
    public void TryAdmit_ReconcilesBeforeDeciding()
    {
        // The daemon's Waiting handler calls TryAdmit with no Reap in between, so the one core
        // held by a dead process has to be seen as free on this very call.
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var done = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(done, oneCore));
        var process = new FakeProcess();
        executor.Attach(done, process);
        process.Exit(0);

        // No Reap(), no LiveAllocations().
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), oneCore));
    }

    [Fact]
    public void TryAdmit_ReconcilesAwayAnAbandonedReservation()
    {
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var stuck = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(stuck, oneCore));
        stuck.Status = JobStatus.Failed;

        // No Reap().
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), oneCore));
    }

    [Fact]
    public void GetStatus_ReconcilesBeforeAnswering()
    {
        // Task 5's wait loop polls GetStatus with no Reap between iterations. Without the internal
        // reconcile an exited process reports Running forever — exactly the silent hang this class
        // exists to prevent.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        process.Exit(0);

        Assert.Equal(ClusterJobStatus.Finished, executor.GetStatus(job));   // no Reap()
    }

    [Fact]
    public void GpuIndicesFor_ReconcilesBeforeAnswering()
    {
        var executor = new ManagedExecutor();
        var job = NewGpuJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        process.Exit(0);
        job.Status = JobStatus.Finished;

        Assert.Empty(executor.GpuIndicesFor(job));   // no Reap(); the entry is gone
    }

    #endregion

    #region Status

    [Theory]
    [InlineData(0, ClusterJobStatus.Finished)]
    [InlineData(3, ClusterJobStatus.Failed)]
    [InlineData(137, ClusterJobStatus.Failed)]
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

    #endregion

    #region Kill, GPUs and queries

    [Fact]
    public void Kill_TerminatesTheTreeOfATrackedJob()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        executor.Kill(job);

        Assert.True(process.WasKilled);
    }

    [Fact]
    public void Kill_ForAnUntrackedJob_IsANoOp()
    {
        new ManagedExecutor().Kill(NewJob());
    }

    [Fact]
    public void Kill_ForAnUnlaunchedReservation_ReleasesItEvenWhileTheJobIsActive()
    {
        // Killing during Staging: the reservation exists but no process ever will, because Attach
        // refuses a condemned entry. Nothing else would ever retire it while the job stays active,
        // so the booking would be held for the lifetime of the host.
        var executor = new ManagedExecutor();
        var oneCore = new ResourceTotals(Cores: 1, MemoryGb: 64, Gpus: 0);

        var reserved = NewJob();
        reserved.Status = JobStatus.Staging;
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(reserved, oneCore));

        executor.Kill(reserved);

        // A process that was already starting when the kill landed must not be adopted — asserted
        // before anything reconciles the entry away, so this is the condemned refusal and not the
        // untracked one. Condemn has already fired, so an adopted process would never be signalled
        // and its booking would stand until it happened to exit on its own.
        Assert.False(executor.Attach(reserved, new FakeProcess()));

        Assert.Empty(executor.LiveAllocations());
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), oneCore));
    }

    [Fact]
    public void Attach_ReportsWhetherTheReservationWasStillThere()
    {
        // A reservation can be retired while its process is starting (an abort during staging).
        // The caller has to learn about it, or the process runs on outside the ledger forever.
        var executor = new ManagedExecutor();
        var admitted = NewJob();
        executor.TryAdmit(admitted, Host);

        Assert.True(executor.Attach(admitted, new FakeProcess()));
        Assert.False(executor.Attach(NewJob(), new FakeProcess()));
    }

    [Fact]
    public void Attach_RefusesToDisplaceALiveProcess()
    {
        // The second orphan route: overwriting Entry.Process makes the old one unreachable, so
        // Reconcile never sees it, nothing kills it, and it holds its resources forever.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);
        var first = new FakeProcess();
        Assert.True(executor.Attach(job, first));

        var second = new FakeProcess();
        Assert.False(executor.Attach(job, second));

        executor.Kill(job);
        Assert.True(first.WasKilled);       // still the tracked one
        Assert.False(second.WasKilled);
    }

    [Fact]
    public void Attach_RefusesASpentReservation()
    {
        // The process ran and exited but the job has not settled, so the entry survives to carry
        // the exit code. It is not a booking anyone may launch into again.
        var executor = new ManagedExecutor();
        var job = NewJob();
        job.Status = JobStatus.Running;
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        process.Exit(0);
        executor.Reap();

        Assert.False(executor.Attach(job, new FakeProcess()));
        Assert.Equal(ClusterJobStatus.Finished, executor.GetStatus(job));   // exit code intact
    }

    [Fact]
    public void GpuIndicesFor_ReturnsNothingForASpentReservation()
    {
        // Same rule as Attach: you may only read the devices of a booking you could still launch
        // into. These belong to a run that is over.
        var executor = new ManagedExecutor();
        var job = NewGpuJob();
        job.Status = JobStatus.Running;
        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Attach(job, process);

        Assert.Equal(new[] { 0 }, executor.GpuIndicesFor(job));

        process.Exit(0);

        Assert.Empty(executor.GpuIndicesFor(job));
    }

    [Fact]
    public void GpuIndicesFor_HandsOutDisjointDevices()
    {
        var executor = new ManagedExecutor();

        var first = NewGpuJob();
        var second = NewGpuJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(first, Host));
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(second, Host));

        var a = executor.GpuIndicesFor(first);
        var b = executor.GpuIndicesFor(second);

        Assert.Equal(new[] { 0 }, a);
        Assert.Equal(new[] { 1 }, b);
        Assert.Empty(executor.GpuIndicesFor(NewGpuJob()));   // untracked
    }

    [Fact]
    public void HasEntries_SeesOnlyTrackedJobs()
    {
        var executor = new ManagedExecutor();
        var tracked = NewJob();
        var untracked = NewJob();
        executor.TryAdmit(tracked, Host);

        Assert.True(executor.HasEntries(j => ReferenceEquals(j, tracked)));
        Assert.False(executor.HasEntries(j => ReferenceEquals(j, untracked)));
    }

    [Fact]
    public void LiveAllocations_ReportsWhatEachJobWasGiven()
    {
        var executor = new ManagedExecutor();
        var job = NewGpuJob();
        executor.TryAdmit(job, Host);

        var allocation = Assert.Single(executor.LiveAllocations());

        Assert.Equal(1, allocation.Cores);
        Assert.Equal(16, allocation.MemoryGb);
        Assert.Equal(new[] { 0 }, allocation.GpuIndices);
    }

    #endregion

    #region Shutdown

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

    [Fact]
    public void ShutdownRefusesAdmission_AsBusyRatherThanAsAPermanentRejection()
    {
        // Reject is terminal: the daemon fails the job with the reason string. A job that simply
        // arrived while Relay was stopping must still be Waiting when Relay comes back.
        var executor = new ManagedExecutor();
        executor.BeginShutdown();

        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), Host));
    }

    [Fact]
    public void OnceShutdownBegins_NoProcessCanAttachEither()
    {
        // The third door. TryAdmit and Launch are guarded, but a process could still be adopted
        // through Attach after KillAllAsync had taken its snapshot — and would then never be
        // signalled by anything. False is the right answer: the caller's contract is to kill a
        // process it cannot get accounted for.
        var executor = new ManagedExecutor();
        var job = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, Host));

        executor.BeginShutdown();

        Assert.False(executor.Attach(job, new FakeProcess()));
        Assert.Equal(ClusterJobStatus.Pending, executor.GetStatus(job));   // nothing was adopted
    }

    [Fact]
    public async Task KillAllAsync_ClosesAdmissionBeforeItKillsAnything()
    {
        // The ordering is the whole point. An entry admitted but not yet launched has no process
        // for the sweep to find, so a kill-then-return would let its in-flight staging task spawn
        // one *after* the sweep had already passed. Probed from inside the kill, which is the
        // latest instant at which admission could still be found open.
        var executor = new ManagedExecutor();
        var job = NewJob();
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(job, Host));

        AdmissionResult? duringTheKill = null;
        Exception? launchDuringTheKill = null;

        var process = new ProbeProcess(() =>
        {
            duringTheKill = executor.TryAdmit(NewJob(), Host);
            launchDuringTheKill = Record.Exception(
                () => executor.Launch(NewJob(), "/tmp/does-not-matter.sh", "/tmp"));
        });
        Assert.True(executor.Attach(job, process));

        await executor.KillAllAsync();

        Assert.IsType<AdmissionResult.Busy>(duringTheKill);
        Assert.IsType<InvalidOperationException>(launchDuringTheKill);
    }

    /// <summary>A process that runs a caller-supplied probe at the moment it is killed.</summary>
    private sealed class ProbeProcess : IManagedProcess
    {
        private readonly Action _onKill;

        public ProbeProcess(Action onKill) => _onKill = onKill;

        public int Pid => 4242;
        public DateTime StartTime => new(2026, 1, 1);
        public bool HasExited { get; private set; }
        public int ExitCode => 137;

        public void KillTree()
        {
            _onKill();
            HasExited = true;
        }

        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task KillAllAsync_KillsEveryTrackedProcess_AndForgetsEveryEntry()
    {
        var executor = new ManagedExecutor();

        var first = NewJob();
        var second = NewJob();
        executor.TryAdmit(first, Host);
        executor.TryAdmit(second, Host);

        var running = new FakeProcess();
        var alsoRunning = new FakeProcess();
        executor.Attach(first, running);
        executor.Attach(second, alsoRunning);

        await executor.KillAllAsync();

        Assert.Equal(1, running.KillCount);
        Assert.Equal(1, alsoRunning.KillCount);
        Assert.False(executor.HasEntries(_ => true));
        Assert.Empty(executor.LiveAllocations());
    }

    [Fact]
    public async Task KillAllAsync_ToleratesAnEntryThatWasNeverLaunched()
    {
        // Admitted, still staging: there is no process to kill, and the null must not throw.
        var executor = new ManagedExecutor();
        executor.TryAdmit(NewJob(), Host);

        await executor.KillAllAsync();

        Assert.False(executor.HasEntries(_ => true));
    }

    [Fact]
    public async Task KillAllAsync_KillsTheRestEvenIfOneProcessThrows()
    {
        var executor = new ManagedExecutor();

        var thrower = NewJob();
        var ordinary = NewJob();
        executor.TryAdmit(thrower, Host);
        executor.TryAdmit(ordinary, Host);

        executor.Attach(thrower, new ProbeProcess(() => throw new InvalidOperationException("gone")));
        var survivor = new FakeProcess();
        executor.Attach(ordinary, survivor);

        await executor.KillAllAsync();

        Assert.Equal(1, survivor.KillCount);
    }

    [Fact]
    public async Task KillAllAsync_KeepsTheRecordOfAProcessItCouldNotConfirmDead()
    {
        // Wiping the registry unconditionally was only sound if every kill and every wait had
        // succeeded. When one did not, the process may still be alive — and with its record gone
        // the next startup's sweep has nothing to find, leaving a GPU held by something nothing
        // tracks. The record is the only handle left, so it stays.
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var stubborn = InSpace(NewJob(), spaceId: 1, jobId: 1);
        var ordinary = InSpace(NewJob(), spaceId: 1, jobId: 2);
        executor.TryAdmit(stubborn, Host);
        executor.TryAdmit(ordinary, Host);

        executor.Launch(stubborn, _ => new ProbeProcess(() => throw new InvalidOperationException("gone")));
        executor.Launch(ordinary, _ => new FakeProcess { Pid = 777 });
        Assert.Equal(2, registry.Load().Count);

        await executor.KillAllAsync();

        // The one that died is forgotten; the one that could not be confirmed is left to sweep.
        var leftover = Assert.Single(registry.Load());
        Assert.Equal(stubborn.Id, leftover.JobId);
    }

    [Fact]
    public void ALaunchThatFinishesSpawningAfterShutdownBegan_KillsTheProcessAndThrows()
    {
        // The other half of the ordering. The guard at the top of Launch was passed before shutdown
        // started, so the spawn goes ahead; by the time it returns, KillAllAsync may already have
        // taken its snapshot and walked past this entry. Nothing else would ever signal it.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);

        var late = new FakeProcess();

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, _ =>
        {
            executor.BeginShutdown();
            return late;
        }));

        Assert.Equal(1, late.KillCount);
    }

    #endregion

    #region The leftover registry

    [Fact]
    public void Launching_RecordsTheProcess_AndSettlingForgetsIt()
    {
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var job = NewJob();
        job.Status = JobStatus.Running;
        executor.TryAdmit(job, Host);

        var process = new FakeProcess { Pid = 5150, StartTime = new DateTime(1234) };
        executor.Launch(job, _ => process);

        var record = Assert.Single(registry.Load());
        Assert.Equal(job.Id, record.JobId);
        Assert.Equal(5150, record.Pid);
        // UTC, not the raw Local ticks: a DST or timezone change between the crash and the restart
        // would otherwise move a recorded start time by an hour and hide the process from the sweep.
        Assert.Equal(ManagedProcessRegistry.UtcTicksOf(new DateTime(1234)), record.StartTimeTicks);

        process.Exit(0);
        job.Status = JobStatus.Finished;
        executor.Reap();

        Assert.Empty(registry.Load());   // no longer a leftover: it is dead and accounted for
    }

    [Fact]
    public void ANonSystemProcessIsRecordedWithNoProcessGroup()
    {
        // Only SystemManagedProcess can have a group of ours. Anything else must persist null, or
        // the startup sweep would group-kill a number that was never a group we created.
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var job = NewJob();
        executor.TryAdmit(job, Host);
        executor.Launch(job, _ => new FakeProcess());

        Assert.Null(Assert.Single(registry.Load()).Pgid);
    }

    [Fact]
    public void ALaunchThatFailsToAttach_LeavesNoRecordBehind()
    {
        // The process is killed on this path, so recording it would put a dead pid in range of the
        // next sweep — and, worse, clobber the record of whichever run replaced this reservation.
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var job = NewJob();
        executor.TryAdmit(job, Host);

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, _ =>
        {
            executor.Kill(job);
            executor.Reap();
            return new FakeProcess();
        }));

        Assert.Empty(registry.Load());
    }

    [Fact]
    public async Task KillAllAsync_ClearsTheRegistry()
    {
        // A clean shutdown leaves nothing for the next startup to sweep. Records left behind would
        // point the next Relay's sweep at pids that have since been recycled.
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var job = NewJob();
        executor.TryAdmit(job, Host);
        executor.Launch(job, _ => new FakeProcess());
        Assert.Single(registry.Load());

        await executor.KillAllAsync();

        Assert.Empty(registry.Load());
    }

    [Fact]
    public void TwoSpacesRunningTheirOwnJobFive_KeepSeparateRecords()
    {
        // Job.Id is per-space, so this is an ordinary Tuesday, not an edge case. Keyed on Job.Id
        // alone, space 1's job settling would call Forget and delete space 2's *live* record —
        // a running process quietly un-registered by a completely unrelated, successful job.
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var first = InSpace(NewJob(), spaceId: 1, jobId: 5);
        var second = InSpace(NewJob(), spaceId: 2, jobId: 5);
        first.Status = JobStatus.Running;
        second.Status = JobStatus.Running;

        executor.TryAdmit(first, Host);
        executor.TryAdmit(second, Host);

        var firstProcess = new FakeProcess { Pid = 111 };
        executor.Launch(first, _ => firstProcess);
        executor.Launch(second, _ => new FakeProcess { Pid = 222 });

        Assert.Equal(2, registry.Load().Count);

        // Space 1's job finishes normally.
        firstProcess.Exit(0);
        first.Status = JobStatus.Finished;
        executor.Reap();

        var survivor = Assert.Single(registry.Load());
        Assert.Equal(2, survivor.SpaceId);
        Assert.Equal(222, survivor.Pid);          // space 2's process is still registered
    }

    private static Job InSpace(Job job, int spaceId, int jobId)
    {
        job.Space = new Space { Id = spaceId, Project = new Project { Id = 7 } };
        job.Id = jobId;
        return job;
    }

    [Fact]
    public void ARecordCarriesTheJobsProjectAndSpace()
    {
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var job = InSpace(NewJob(), spaceId: 3, jobId: 9);
        executor.TryAdmit(job, Host);
        executor.Launch(job, _ => new FakeProcess());

        var record = Assert.Single(registry.Load());
        Assert.Equal(7, record.ProjectId);
        Assert.Equal(3, record.SpaceId);
        Assert.Equal(9, record.JobId);
    }

    [Fact]
    public void AJobWithNoSpace_IsStillRecordedConsistently()
    {
        // Refund is nullable-disabled and a template job need not be filed anywhere yet. The
        // placeholder identity has to round-trip, or the record could never be forgotten again.
        var registry = NewRegistry();
        var executor = new ManagedExecutor(registry);

        var job = NewJob();
        job.Status = JobStatus.Running;
        Assert.Null(job.Space);

        executor.TryAdmit(job, Host);
        var process = new FakeProcess();
        executor.Launch(job, _ => process);

        var record = Assert.Single(registry.Load());
        Assert.Equal(-1, record.ProjectId);
        Assert.Equal(-1, record.SpaceId);

        process.Exit(0);
        job.Status = JobStatus.Finished;
        executor.Reap();

        Assert.Empty(registry.Load());
    }

    #region Unconfirmed leftovers from a crashed Relay

    private static ManagedProcessRecord Leftover(int pid = 4242) =>
        new(ProjectId: 1, SpaceId: 1, JobId: 1, Pid: pid, Pgid: pid, StartTimeTicks: 999);

    [Fact]
    public void ASurvivingLeftover_MakesAdmissionBusy_NotRejected()
    {
        // A leftover the startup sweep could not confirm dead may still be holding a GPU this
        // ledger believes is free, so nothing may be admitted onto it. Busy rather than Reject
        // because the condition is transient: the job stays Waiting and starts by itself.
        var executor = new ManagedExecutor(unconfirmedLeftovers: new[] { Leftover() },
                                           containLeftover: _ => false);

        Assert.True(executor.HasUnconfirmedLeftovers);
        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), Host));

        // And no reservation was booked behind the refusal.
        Assert.Empty(executor.LiveAllocations());
    }

    [Fact]
    public void ASurvivorThatDiesLater_ReleasesTheBlockOnTheNextReapTick()
    {
        // The self-heal, and the reason the verdict is Busy. A permanent wedge that needed a Relay
        // restart to clear is the worst failure this feature can have.
        bool dead = false;
        var executor = new ManagedExecutor(unconfirmedLeftovers: new[] { Leftover() },
                                           containLeftover: _ => dead);

        executor.Reap();
        Assert.IsType<AdmissionResult.Busy>(executor.TryAdmit(NewJob(), Host));

        dead = true;                       // the process finally exits
        executor.Reap();

        Assert.False(executor.HasUnconfirmedLeftovers);
        Assert.IsType<AdmissionResult.Admit>(executor.TryAdmit(NewJob(), Host));
    }

    [Fact]
    public void TheReapTick_RetriesTheKill_RatherThanOnlyProbing()
    {
        // "Retry the kill", not "wait and see": a signal that did not land the first time is worth
        // sending again, and containment is one operation.
        int attempts = 0;
        var executor = new ManagedExecutor(unconfirmedLeftovers: new[] { Leftover() },
                                           containLeftover: _ => { attempts++; return false; });

        executor.Reap();
        executor.Reap();

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void AContainmentRetryThatThrows_KeepsTheBlock_RatherThanEscapingTheReap()
    {
        // Reap runs from the daemon tick; an escape there would stop every other entry being
        // reconciled. And an unreadable verdict is not a confirmation, so the record stays.
        var executor = new ManagedExecutor(
            unconfirmedLeftovers: new[] { Leftover() },
            containLeftover: _ => throw new InvalidOperationException("probe failed"));

        executor.Reap();

        Assert.True(executor.HasUnconfirmedLeftovers);
    }

    [Fact]
    public void OnceEveryLeftoverIsGone_TheRegistryFileIsCleared()
    {
        // The survivors were retained on disk for the next startup to sweep. Contained, they must
        // not stay there: their pids will be recycled.
        var registry = NewRegistry();
        registry.Record(Leftover());

        var executor = new ManagedExecutor(registry, new[] { Leftover() }, _ => true);
        executor.Reap();

        Assert.Empty(registry.Load());
    }

    #endregion

    [Fact]
    public void AnExecutorWithNoRegistry_StillLaunchesAndReconciles()
    {
        // The registry is optional; every accounting-only test constructs the executor without one.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, Host);

        var process = new FakeProcess();
        Assert.Same(process, executor.Launch(job, _ => process));

        process.Exit(0);
        job.Status = JobStatus.Finished;
        executor.Reap();

        Assert.False(executor.HasEntries(_ => true));
    }

    #endregion
}
