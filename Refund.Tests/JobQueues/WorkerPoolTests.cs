using Refund.DataModel;
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
}
