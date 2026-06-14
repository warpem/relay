using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobQueues;
using Refund.Jobs.Preprocessing.MotionAndCTF2D;

namespace Refund.Tests.JobQueues;

public class WorkerPoolTests
{
    private static readonly object _populateLock = new();
    private static bool _populated;

    // Job.PopulateStatic() is not idempotent (it Add()s into static dictionaries),
    // so register concrete job types exactly once per process.
    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (_populated)
                return;
            Job.PopulateStatic();
            _populated = true;
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
            Assert.Equal(2, fakeQueue2.SubmitScriptCalls);   // 2 fresh workers submitted this tick
            Assert.Equal(4, total);                          // cumulative total resumed from restored 2
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
