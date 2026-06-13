using Refund.DataModel;
using Refund.Jobs.Preprocessing.MotionAndCTF2D;

namespace Refund.Tests.JobQueues;

public class WorkerPoolTests
{
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
}
