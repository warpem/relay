using System.Reflection;
using Refund.DataModel;
using Refund.JobQueues;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

/// <summary>
/// Aborting a job while it is staging. The staging task used to be scheduled with the abort's own
/// cancellation token, so a cancel landing between the task being queued and its delegate starting
/// stopped the delegate running at all — including its finally. The job stayed in StagingJobs
/// forever and every requeue threw "already staging": one badly-timed abort made a job permanently
/// unrunnable, with no way back short of restarting Relay.
/// </summary>
[Collection("JobRegistry")]
public class ClusterQueueStagingCancellationTests : IDisposable
{
    private static readonly object _populateLock = new();

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "relay-staging-" + Guid.NewGuid());

    public ClusterQueueStagingCancellationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void EnsurePopulated()
    {
        lock (_populateLock)
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

    /// <summary>StagingJobs is private, and it is precisely what leaks.</summary>
    private static int StagingCount(ClusterQueue queue) =>
        ((System.Collections.ICollection)typeof(ClusterQueue)
            .GetField("StagingJobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!).Count;

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);

        Assert.True(condition(), what);
    }

    [Fact]
    public async Task AnAbortDuringStaging_EndsAtAborted_NotRewrittenToFailed()
    {
        // Deterministic: the queue's update callback parks the staging delegate until the abort has
        // been issued, so the cancellation is guaranteed to be pending when the delegate looks.
        var delegateStarted = new SemaphoreSlim(0);
        var abortIssued = new SemaphoreSlim(0);
        int callbacks = 0;

        var queue = new ClusterQueue((job, action) =>
        {
            action(job);

            if (Interlocked.Increment(ref callbacks) == 1)
            {
                delegateStarted.Release();
                abortIssued.Wait();
            }
        })
        {
            Id = 1,
            SubmissionScriptTemplate = "#!/bin/bash\n{{command}}\n",
        };

        var job = NewJob();
        queue.SubmitJob(job);

        Assert.True(await delegateStarted.WaitAsync(TimeSpan.FromSeconds(10)));

        queue.AbortJob(job);
        abortIssued.Release();

        await WaitUntil(() => job.Status is JobStatus.Aborted or JobStatus.Failed,
                        "the staging task never settled the job");

        // Failed is what the generic catch used to write, which made a job the user deliberately
        // stopped indistinguishable from one whose script could not be written.
        Assert.Equal(JobStatus.Aborted, job.Status);
    }

    [Fact]
    public async Task AnAbortDuringStaging_AlwaysReleasesTheJob_SoItCanBeQueuedAgain()
    {
        // The cleanup half. Whether the cancel is seen before the delegate starts or partway
        // through it, StagingJobs must end up empty — that dictionary is the only thing standing
        // between the job and a requeue.
        var queue = new ClusterQueue((job, action) => action(job))
        {
            Id = 1,
            SubmissionScriptTemplate = "#!/bin/bash\n{{command}}\n",
        };

        var job = NewJob();

        queue.SubmitJob(job);
        queue.AbortJob(job);            // as close to "before the delegate starts" as a test gets

        await WaitUntil(() => StagingCount(queue) == 0,
                        "the aborted job was never removed from StagingJobs, so it can never be " +
                        "queued again");

        // And the user-visible consequence: the requeue is accepted rather than throwing
        // "Job 1 is already staging!".
        job.Status = JobStatus.Waiting;
        queue.SubmitJob(job);

        await WaitUntil(() => StagingCount(queue) == 0, "the requeued job never settled");
    }

    [Fact]
    public async Task AbortingBeforeTheDelegateRuns_DoesNotLeaveTheJobStaging()
    {
        // Directly the reported window, forced: the staging tasks cannot start until the thread
        // pool frees up, so every abort below lands while its delegate is still queued. Under the
        // old scheduling token none of those delegates would ever run, and all ten jobs would be
        // stuck in StagingJobs permanently.
        var queue = new ClusterQueue((job, action) => action(job))
        {
            Id = 1,
            SubmissionScriptTemplate = "#!/bin/bash\n{{command}}\n",
        };

        var release = new SemaphoreSlim(0);
        var hogs = Occupy(release);

        try
        {
            var jobs = Enumerable.Range(1, 10).Select(i => NewJob(i)).ToList();

            foreach (var job in jobs)
                queue.SubmitJob(job);

            foreach (var job in jobs)
                queue.AbortJob(job);
        }
        finally
        {
            release.Release(hogs);
        }

        await WaitUntil(() => StagingCount(queue) == 0,
                        "jobs aborted before their staging delegate started were never released");
    }

    /// <summary>
    /// Fills every thread-pool worker so nothing queued afterwards can begin. Returns how many
    /// permits the caller must release.
    /// </summary>
    private static int Occupy(SemaphoreSlim release)
    {
        ThreadPool.GetMinThreads(out int workers, out _);
        ThreadPool.GetAvailableThreads(out int available, out _);

        int hogs = Math.Max(workers, available) + 2;

        for (int i = 0; i < hogs; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(), null);

        return hogs;
    }
}
