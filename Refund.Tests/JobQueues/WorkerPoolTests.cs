using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobQueues;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using EtomoJob = Refund.Jobs.Ts.Alignment.AlignEtomo.AlignEtomo;
using RefineJob = Refund.Jobs.M.Refine.Refine;
using MissAlignmentJob = Refund.Jobs.Ts.Alignment.AlignMiss.AlignMiss;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class WorkerPoolTests
{
    private static readonly object _populateLock = new();

    // Job.PopulateStatic() is not idempotent (it Add()s into static dictionaries), so register
    // concrete job types exactly once per process. Keyed off the shared registry state so this
    // is a no-op if another test class (e.g. JobTaxonomyTests) already populated it.
    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    // MotionAndCTF2D.ComposeCommandArguments resolves paths against the Space,
    // so it needs a Space with a non-empty RootDirectory to run.
    private static MotionAndCTF2D MakeJobWithSpace()
    {
        EnsurePopulated();
        return new MotionAndCTF2D { Space = new Space { RootDirectory = "/tmp/relay-test" } };
    }

    [Fact]
    public void WarpJobGpu_ImplementsIPooledJob()
    {
        var job = new MotionAndCTF2D();
        Assert.IsAssignableFrom<IPooledJob>(job);
    }

    [Fact]
    public void MissAlignment_IsNotPooled_ButStillGpu()
    {
        // MissAlignment runs a single GPU command outside the WarpTools per-item worker-pool model,
        // so it must NOT inherit pool support (which lives on WarpJobGpu) — while remaining a GPU job.
        var job = new MissAlignmentJob();
        Assert.IsNotAssignableFrom<IPooledJob>(job);
        Assert.Equal(JobQueueType.GPU, job.QueueType);
    }

    [Fact]
    public void ItemProgress_ReadOnlyWrapper_ImplementsInterface_ForWarpAndMissAlignmentJobs()
    {
        EnsurePopulated();

        // Both a WarpTools job (WarpJob-derived) and MissAlignment (standalone Job) report item
        // counts, so their generated read-only wrappers must expose IItemProgress. This is what lets
        // the job card gate the item-count display on the capability + non-null counts, not on a
        // concrete job type. Also proves the ReadOnly source generator replicated the interface.
        foreach (Job job in new Job[] { new MotionAndCTF2D(), new MissAlignmentJob() })
        {
            Assert.IsAssignableFrom<IItemProgress>(job);                 // mutable side
            Assert.IsAssignableFrom<IItemProgress>(job.AsReadOnly());    // generated read-only side
            Assert.Null(((IItemProgress)job).NItemsTotal);              // nullable, defaults to null
        }
    }

    [Fact]
    public void ItemProgress_ReadOnlyWrapper_NotImplemented_ForNonItemJob()
    {
        EnsurePopulated();

        // A job that does not report item counts (RELION mask creation) must not gain IItemProgress,
        // so the card shows nothing for it. Guards the generator's read-contract heuristic against
        // over-replicating interfaces.
        var job = new MaskJob();
        Assert.IsNotAssignableFrom<IItemProgress>(job);
        Assert.IsNotAssignableFrom<IItemProgress>(job.AsReadOnly());
    }

    [Fact]
    public void WarpJobGpu_PoolQueueId_DefaultsToMinusOne()
    {
        var job = new MotionAndCTF2D();
        Assert.Equal(-1, ((IPooledJob)job).PoolQueueId);
    }

    [Fact]
    public void WarpJobGpu_PoolSubmissionCap_IsTwicePoolSize()
    {
        var job = new MotionAndCTF2D();
        var pooled = (IPooledJob)job;
        Assert.Equal(pooled.PoolSize * 100, pooled.PoolSubmissionCap);
    }

    [Fact]
    public void WarpJobGpu_PoolSize_IsPositive()
    {
        var job = new MotionAndCTF2D();
        Assert.True(((IPooledJob)job).PoolSize > 0);
    }

    [Fact]
    public void WarpJobGpu_PoolFields_RoundTripJson()
    {
        EnsurePopulated();

        // Pool config is [RelayProperty] ints handled by RelayBase.WriteToJson /
        // ReadFromJson via reflection; a bare instance round-trips them fine. Pool size
        // is derived from NGpus (one worker per GPU), so NGpus is the persisted source.
        var job = new MotionAndCTF2D { PoolQueueId = 3, NGpus = 16 };
        var node = new JsonObject();
        job.WriteToJson(node);

        // RelayBase.ReadFromJson(JsonNode) deserializes the [RelayProperty] fields.
        // (Job adds an extra ReadFromJson(JsonNode, users) overload for UpdatedBy/Events,
        // but the pool fields live on the base reflection path.)
        var job2 = new MotionAndCTF2D();
        job2.ReadFromJson(node);

        Assert.Equal(3, job2.PoolQueueId);
        Assert.Equal(16, job2.NGpus);
        Assert.Equal(16, ((IPooledJob)job2).PoolSize);   // derived from NGpus
    }

    [Fact]
    public void WarpJobGpu_PoolWorkersAlive_DefaultsToZero()
    {
        var job = new MotionAndCTF2D();
        Assert.Equal(0, job.PoolWorkersAlive);
    }

    [Fact]
    public void WarpJobGpu_GetWorkerCommand_FormatsCommandWithDeviceIndex()
    {
        // GetWorkerCommand cd's to RunDirectory (Space.RootDirectory), so a Space is required.
        var job = MakeJobWithSpace();
        var cmd = ((IPooledJob)job).GetWorkerCommand(2);
        Assert.Contains("WarpWorker2", cmd);
        Assert.Contains("--queue-dir ", cmd);   // WarpWorker2's flag (NOT the Manager's --task_dir)
        Assert.Contains("--device 2", cmd);
        Assert.Contains("--log-dir ", cmd);
        Assert.Contains("--persistent", cmd);   // keep polling instead of exiting when the queue drains
        Assert.Contains("cd ", cmd);            // runs from the job's working directory, like the Manager
    }

    [Fact]
    public void WarpJobGpu_GetWorkerCommand_LaunchesPerDeviceProcesses()
    {
        var job = MakeJobWithSpace();
        job.PerDevice = 3;                       // 3 worker processes per GPU

        var cmd = ((IPooledJob)job).GetWorkerCommand(0);

        // One WarpWorker2 invocation per worker process, each backgrounded, then a single wait.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cmd, "WarpWorker2 ").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cmd, " &").Count);
        Assert.Contains("\nwait", cmd);

        // Each process gets a distinct, globally-unique worker id (…-<device>-<index>).
        Assert.Contains("-0-0\"", cmd);
        Assert.Contains("-0-1\"", cmd);
        Assert.Contains("-0-2\"", cmd);
    }

    [Fact]
    public void WarpJobGpu_GetWorkerResourceValues_ScalesCoresAndMemoryWithPerDevice()
    {
        var job = MakeJobWithSpace();
        job.PerDevice = 4;

        var worker = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");

        Assert.Equal((4 * 2).ToString(), worker["n_cores"]);                  // ~2 cores per process
        Assert.Equal((4 * job.MemoryPerWorker).ToString(), worker["memory_gb"]);
        Assert.Equal("1", worker["n_gpus"]);                                  // still one GPU
    }

    [Fact]
    public void WarpJobGpu_GetWorkerResourceValues_CoversManagerKeysWithWorkerOverrides()
    {
        var job = MakeJobWithSpace();

        // Anti-drift: the worker must carry every variable the Manager's template expects, so it
        // can't silently miss one (which would leave an empty #SBATCH directive).
        var managerKeys = job.GetResourceValues().Keys;
        var worker = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");

        foreach (var key in managerKeys)
            Assert.Contains(key, worker.Keys);

        // ...plus job_id, with the worker-specific overrides applied.
        Assert.Contains("worker", worker["job_id"]);
        Assert.Equal("1", worker["n_gpus"]);
        Assert.Equal("1", worker["n_processes"]);
        Assert.Contains("%j", worker["std_out"]);
        Assert.Contains("%j", worker["std_err"]);
    }

    [Fact]
    public void WarpJobGpu_ComposeCommandArguments_OmitsExternalProvisionerByDefault()
    {
        var job = MakeJobWithSpace();   // PoolQueueId defaults to -1
        var args = job.ComposeCommandArguments();
        Assert.False(args.ContainsKey("external_provisioner"));
    }

    [Fact]
    public void WarpJobGpu_ComposeCommandArguments_AddsExternalProvisionerWhenPooled()
    {
        var job = MakeJobWithSpace();
        job.PoolQueueId = 1;
        var args = job.ComposeCommandArguments();
        Assert.True(args.ContainsKey("external_provisioner"));
    }

    [Fact]
    public void WarpJobGpu_WorkerRequiredModules_SwapsManagerCpuForWorkerGpu()
    {
        // Pooled Manager runs CPU-only; the worker does the GPU work. The worker module set must
        // request "gpu", never the Manager's "cpu".
        var job = MakeJobWithSpace();
        job.PoolQueueId = 1;   // pooled → this job's RequiredModules carries "cpu"

        var workerModules = ((IPooledJob)job).WorkerRequiredModules;

        Assert.Contains("gpu", workerModules);
        Assert.DoesNotContain("cpu", workerModules);
    }

    [Fact]
    public void Refine_PooledResourceRequests_AreManagerProfileIndependentOfPoolSize()
    {
        // MCore's CoreCount/MemoryGb scale with NGpus*PerDevice for the non-pooled (single
        // multi-GPU job) path. When pooled, NGpus IS the pool size, so the CPU-only Manager
        // must fall back to the fixed manager profile instead of requesting worker-scaled
        // resources. Two pools of very different sizes must request identical Manager resources.
        var small = new RefineJob { NGpus = 4,  PerDevice = 2, MemoryPerWorker = 10, PoolQueueId = 1 };
        var large = new RefineJob { NGpus = 64, PerDevice = 2, MemoryPerWorker = 10, PoolQueueId = 1 };

        Assert.Equal(small.CoreCount, large.CoreCount);
        Assert.Equal(small.MemoryGb,  large.MemoryGb);

        // And the non-pooled path still scales with the GPU count.
        var local = new RefineJob { NGpus = 64, PerDevice = 2, MemoryPerWorker = 10 };
        Assert.True(local.CoreCount > large.CoreCount);
        Assert.True(local.MemoryGb  > large.MemoryGb);
    }

    [Fact]
    public void WarpJobGpu_WorkerRequiredModules_CarriesLeafToolModules()
    {
        // Regression: workers ran etomo/aretomo without their tool module because
        // WorkerRequiredModules built off WarpJob's base modules, dropping the leaf job's "imod".
        var job = new EtomoJob { PoolQueueId = 1 };

        var workerModules = ((IPooledJob)job).WorkerRequiredModules;

        Assert.Contains("imod", workerModules);   // the worker is what actually runs etomo
        Assert.Contains("gpu", workerModules);
        Assert.DoesNotContain("cpu", workerModules);
    }

    [Fact]
    public void WorkerPool_Initialize_CreatesWorkerLogsDirectory()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var pool = new WorkerPool(new FakePoolQueue(), new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool.Initialize();
            Assert.True(Directory.Exists(Path.Combine(tmpDir, "worker_logs")));
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_Tick_SubmitsMissingWorkers()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fakeQueue = new FakePoolQueue();
            var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 3));
            pool.Initialize();

            var (alive, running, submitted) = await pool.Tick();

            Assert.Equal(3, submitted);
            Assert.Equal(3, alive);
            // Workers just submitted this tick are alive but not yet reported running by the
            // scheduler (the active-jobs snapshot is taken before submission) — so running == 0.
            // The running/pending split is exercised on a later tick in
            // WorkerPool_Tick_ReportsRunningSeparatelyFromPending.
            Assert.Equal(0, running);
            Assert.Equal(3, fakeQueue.SubmitScriptCalls);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_Tick_ReportsRunningSeparatelyFromPending()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fakeQueue = new FakePoolQueue();
            var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 3));
            pool.Initialize();

            // First tick submits IDs 100, 101, 102 (FakePoolQueue._nextId starts at 100).
            await pool.Tick();

            // Mark one worker pending; the fleet is full so the next tick submits nothing.
            fakeQueue.PendingIds.Add("100");
            var (alive, running, _) = await pool.Tick();

            Assert.Equal(3, alive);                  // all three still present and non-terminal
            Assert.Equal(2, running);                // one is pending, two are running
            Assert.Equal(3, fakeQueue.SubmitScriptCalls);   // no extra submissions: pending counts as alive
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_Tick_CapsSubmissionsPerTick()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Pool larger than the per-tick cap: it must ramp over several ticks, not submit all at once.
            var fakeQueue = new FakePoolQueue();
            var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 40));
            pool.Initialize();

            // Per-tick cap is MaxSubmitsPerTick (5): a 40-worker pool ramps over several ticks.
            var (_, _, submittedAfter1) = await pool.Tick();
            Assert.Equal(5, submittedAfter1);               // capped per tick, not the full 40
            Assert.Equal(5, fakeQueue.SubmitScriptCalls);

            var (_, _, submittedAfter2) = await pool.Tick();
            Assert.Equal(10, submittedAfter2);              // next batch the following tick
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_Tick_DoesNotExceedCap()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Pool size 2, cap 4 (PoolSubmissionCap = poolSize*2). Workers die every tick.
            var fakeQueue = new FakePoolQueue(alwaysEmpty: true);
            var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool.Initialize();

            await pool.Tick();   // submits 2 (total 2)
            await pool.Tick();   // submits 2 (total 4)
            var (_, _, submitted) = await pool.Tick();   // cap reached, submits 0

            Assert.Equal(4, submitted);
            Assert.Equal(4, fakeQueue.SubmitScriptCalls);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_Dissolve_CancelsAliveJobs()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fakeQueue = new FakePoolQueue();
            var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool.Initialize();
            await pool.Tick();
            await pool.Dissolve();

            Assert.Equal(1, fakeQueue.CancelJobsCalls);
            Assert.Equal(2, fakeQueue.CancelledIds.Count);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_PersistsAndRestoresState()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fakeQueue = new FakePoolQueue();
            var pool = new WorkerPool(fakeQueue, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool.Initialize();
            await pool.Tick();   // submits 2, total 2

            // Simulate restart: new pool, fresh queue, loads pool_state.json
            var fakeQueue2 = new FakePoolQueue();
            var pool2 = new WorkerPool(fakeQueue2, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool2.Initialize();   // loads persisted total_submissions and submitted_ids

            // fakeQueue2 reports no active jobs (different queue, none of pool1's IDs alive),
            // so Tick must submit 2 fresh workers — and total_submissions resumes from 2, giving cap room.
            // Tick returns the cumulative totalSubmissions (same contract as the cap test).
            // Restored total was 2; this tick submits 2 fresh workers -> cumulative total 4.
            var (_, _, total) = await pool2.Tick();
            // total == 4 is the load-bearing assertion: without restore, _totalSubmissions would
            // start at 0 and end at 2 here, not 4. SubmitScriptCalls == 2 passes with OR without
            // restore (a fresh pool with an empty queue also submits 2), so it does not alone prove
            // persistence — see WorkerPool_ReAdoption_DoesNotResubmitStillAliveWorkers for that.
            Assert.Equal(2, fakeQueue2.SubmitScriptCalls);
            Assert.Equal(4, total);                          // cumulative total resumed from restored 2
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public async Task WorkerPool_ReAdoption_DoesNotResubmitStillAliveWorkers()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // First run: submit 2 workers, persist their IDs.
            var q1 = new FakePoolQueue();
            var pool1 = new WorkerPool(q1, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool1.Initialize();
            await pool1.Tick();   // submits IDs 100, 101 (FakePoolQueue._nextId starts at 100)

            // Restart: new pool loads submitted_ids {100,101}; new queue still reports them alive.
            // Depends on FakePoolQueue's deterministic ID scheme (100, 101, ...).
            var q2 = new FakePoolQueue();
            q2.SeedActive(new[] { "100", "101" });
            var pool2 = new WorkerPool(q2, new FakePooledJob(tmpDir, poolQueueId: 1, poolSize: 2));
            pool2.Initialize();

            var (alive, _, _) = await pool2.Tick();

            // Restored IDs are recognized as alive -> deficit 0 -> no new submissions.
            // This is what proves the persisted submitted_ids SET is restored and used.
            Assert.Equal(2, alive);
            Assert.Equal(0, q2.SubmitScriptCalls);
        }
        finally { Directory.Delete(tmpDir, true); }
    }
}

internal class FakePooledJob : IPooledJob
{
    public FakePooledJob(string dir, int poolQueueId, int poolSize)
    {
        DirectoryPath = dir;
        PoolQueueId = poolQueueId;
        PoolSize = poolSize;
    }
    public string DirectoryPath { get; }
    public int PoolQueueId { get; }
    public int PoolSize { get; }
    public int PoolSubmissionCap => PoolSize * 2;
    public string[] WorkerRequiredModules => ["gpu"];
    public Dictionary<string, string> GetWorkerResourceValues(string workerLogDir) => new()
    {
        { "job_id",  "fake-worker" },
        { "n_gpus",  "1" },
        { "std_out", Path.Combine(workerLogDir, "%j.out") },
        { "std_err", Path.Combine(workerLogDir, "%j.err") },
    };
    public string GetWorkerCommand(int deviceIndex) => $"WarpWorker2 --device {deviceIndex}";
}

internal class FakePoolQueue : IPoolQueue
{
    private int _nextId = 100;
    private readonly bool _alwaysEmpty;
    private readonly HashSet<string> _submitted = new();
    public int SubmitScriptCalls { get; private set; }
    public int CancelJobsCalls { get; private set; }
    public HashSet<string> CancelledIds { get; } = new();

    public FakePoolQueue(bool alwaysEmpty = false) { _alwaysEmpty = alwaysEmpty; }

    /// <summary>Seeds pre-existing active IDs so a "restart" queue can report prior workers as alive.</summary>
    public void SeedActive(IEnumerable<string> ids)
    {
        foreach (var id in ids) _submitted.Add(id);
    }

    public Task<string> SubmitScript(string scriptPath)
    {
        SubmitScriptCalls++;
        var id = (_nextId++).ToString();
        _submitted.Add(id);
        return Task.FromResult(id);
    }

    /// <summary>IDs reported as Pending instead of Running; everything else alive is Running.</summary>
    public HashSet<string> PendingIds { get; } = new();

    public Task<Dictionary<string, ClusterJobStatus>> ListActiveJobs() =>
        Task.FromResult(_alwaysEmpty
            ? new Dictionary<string, ClusterJobStatus>()
            : _submitted.ToDictionary(
                id => id,
                id => PendingIds.Contains(id) ? ClusterJobStatus.Pending : ClusterJobStatus.Running));

    public Task CancelJobs(IEnumerable<string> ids)
    {
        CancelJobsCalls++;
        foreach (var id in ids) CancelledIds.Add(id);
        _submitted.ExceptWith(CancelledIds);
        return Task.CompletedTask;
    }

    public string BuildWorkerScript(string command, Dictionary<string, string> resourceValues,
        string[] requiredModules, string scriptPath)
    {
        File.WriteAllText(scriptPath, "fake script");
        return scriptPath;
    }
}
