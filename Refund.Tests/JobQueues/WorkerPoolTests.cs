using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobQueues;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;

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
        Assert.Equal(pooled.PoolSize * 2, pooled.PoolSubmissionCap);
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
        var job = new MotionAndCTF2D();
        var cmd = ((IPooledJob)job).GetWorkerCommand(2);
        Assert.Contains("WarpWorker2", cmd);
        Assert.Contains("--device 2", cmd);
        Assert.Contains("tasks", cmd);
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

            var (alive, submitted) = await pool.Tick();

            Assert.Equal(3, submitted);
            Assert.Equal(3, alive);
            Assert.Equal(3, fakeQueue.SubmitScriptCalls);
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
            var (_, submitted) = await pool.Tick();   // cap reached, submits 0

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
            var (_, total) = await pool2.Tick();
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

            var (alive, _) = await pool2.Tick();

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
    public int WorkerMemoryGb => 12;
    public int WorkerCoreCount => 2;
    public string[] WorkerRequiredModules => ["gpu"];
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

    public Task<HashSet<string>> ListActiveJobIds() =>
        Task.FromResult(_alwaysEmpty ? new HashSet<string>() : new HashSet<string>(_submitted));

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
