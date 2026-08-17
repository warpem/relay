using System.Diagnostics;
using Refund.DataModel;
using Refund.JobQueues;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

/// <summary>
/// Limbo: the window between a job's script being written and the scheduler handing back an id.
/// The bookkeeping for it was broken from the initial release, on every cluster queue — SLURM and
/// Flux in production, not only managed. The removal was guarded by an inverted condition, so the
/// set was never cleared; and the abort path spun on that set, unbounded, on the daemon thread.
/// </summary>
[Collection("JobRegistry")]
public class ClusterQueueLimboTests : IDisposable
{
    private static readonly object PopulateLock = new();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-limbo-" + Guid.NewGuid());

    public ClusterQueueLimboTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void EnsurePopulated()
    {
        lock (PopulateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    private Job NewJob(int id = 1)
    {
        EnsurePopulated();
        return new MaskJob { Id = id, Space = new Space { RootDirectory = _dir },
                             Status = JobStatus.Waiting };
    }

    private static ClusterQueue Queue() => new((job, action) => action(job))
    {
        Id = 1,
        SchedulerType = ClusterScheduler.Slurm,
    };

    [Fact]
    public void SettleStaging_ActuallyClearsLimbo()
    {
        // `if (!JobsInLimbo.Contains(job)) JobsInLimbo.Remove(job)` removed the job precisely when
        // it was not there, so a job that reached limbo stayed in it for the life of the process.
        var queue = Queue();
        var job = NewJob();

        queue.EnterLimbo(job);
        Assert.True(queue.IsInLimbo(job));

        queue.SettleStaging(job);

        Assert.False(queue.IsInLimbo(job));
    }

    [Fact]
    public void SettleStaging_OnAJobThatNeverEnteredLimbo_IsHarmless()
    {
        // Why the removal needs no guard of its own: HashSet.Remove is already a no-op.
        var queue = Queue();

        queue.SettleStaging(NewJob());          // must not throw

        Assert.False(queue.IsInLimbo(NewJob(2)));
    }

    [Fact]
    public void WaitForClusterJobId_GivesUp_RatherThanSpinningOnTheDaemonThread()
    {
        // The consequence of the stale entry, and the second half of the fix. A job that entered
        // limbo and then failed staging before the scheduler answered never gets an id, so the
        // condition the old `while` waited on could not become false — and it ran on the daemon
        // thread, so the whole host stopped with it.
        var queue = Queue();
        var job = NewJob();

        queue.EnterLimbo(job);

        var elapsed = Stopwatch.StartNew();
        bool gotId = queue.WaitForClusterJobId(job, TimeSpan.FromMilliseconds(150));
        elapsed.Stop();

        Assert.False(gotId);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                    $"the bounded wait took {elapsed.Elapsed.TotalSeconds:F1}s, which means it is " +
                    "not bounded");
    }

    [Fact]
    public void WaitForClusterJobId_ReturnsAsSoonAsTheIdArrives()
    {
        var queue = Queue();
        var job = NewJob();

        queue.EnterLimbo(job);

        _ = Task.Run(async () => { await Task.Delay(50); job.ClusterJobId = "12345"; });

        Assert.True(queue.WaitForClusterJobId(job, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WaitForClusterJobId_OnAJobThatLeftLimboWithoutAnId_ReportsThereIsNothingToAbort()
    {
        // Staging failed and settled. Reporting true here would send AbortJobTemplate to the
        // cluster with an empty id substituted in.
        var queue = Queue();
        var job = NewJob();

        Assert.False(queue.WaitForClusterJobId(job, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TheAbortWait_IsBoundedByAConstantThatIsNeitherZeroNorForever()
    {
        Assert.InRange(ClusterQueue.LimboJobIdWait, TimeSpan.FromSeconds(5),
                       TimeSpan.FromMinutes(10));
    }
}
