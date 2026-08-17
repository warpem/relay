using System.Reflection;
using Refund.DataModel;
using Refund.JobQueues;
using Refund.Services.Core.Repositories;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

/// <summary>
/// The queue-side half of the admission guard: what HandleWaitingState switches on. CreateMask
/// asks for 1 process x 1 core, 8 GB and no GPU.
/// </summary>
[Collection("JobRegistry")]
public class AdmissionGuardTests
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

    private static Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Space = new Space { RootDirectory = "/tmp/relay-test" },
                             Status = JobStatus.Waiting };
    }

    private static ClusterQueue ManagedQueue(int cores, int gpus, ManagedExecutor executor) =>
        new ClusterQueue((_, _) => { })
        {
            SchedulerType = ClusterScheduler.Managed,
            ManagedCores = cores,
            ManagedMemoryGb = 1024,
            ManagedGpus = gpus,
            Executor = executor,
        };

    [Fact]
    public void ABusyQueue_ReportsBusy_SoTheJobStaysWaiting()
    {
        var executor = new ManagedExecutor();
        var queue = ManagedQueue(cores: 1, gpus: 0, executor);

        var first = NewJob();
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(first));
        executor.Attach(first, null);   // admitted; process attaches later

        Assert.IsType<AdmissionResult.Busy>(queue.CanAdmit(NewJob()));
    }

    [Fact]
    public void AnImpossibleRequest_IsRejectedWithAReasonNamingBothSides()
    {
        // cores: 0, not 1. CreateMask asks for exactly 1 core and 8 GB, so against a 1-core,
        // 1024 GB, 0-GPU queue every dimension fits and CanEverFit would admit it.
        var queue = ManagedQueue(cores: 0, gpus: 0, new ManagedExecutor());

        var reject = Assert.IsType<AdmissionResult.Reject>(queue.CanAdmit(NewJob()));

        Assert.Contains("never", reject.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 cores", reject.Reason);      // the request side
        Assert.Contains("0 cores", reject.Reason);      // the queue side
    }

    [Fact]
    public void AdmissionIsIdempotent_SoARetriedTickDoesNotDoubleBook()
    {
        // The daemon can ask twice for the same job before it launches — once per tick. The second
        // ask must not book the host again, or a one-core queue would report itself full.
        var executor = new ManagedExecutor();
        var queue = ManagedQueue(cores: 1, gpus: 0, executor);

        var job = NewJob();
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(job));
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(job));

        Assert.Single(executor.LiveAllocations());
    }
}

/// <summary>
/// The daemon-side half: that HandleWaitingState asks <em>before</em> it writes Staging. Placed
/// after the transition, a Busy job would strand in Staging with nothing running it, and the whole
/// reason Busy is not an exception is that the daemon must be able to retry from Waiting.
/// </summary>
[Collection("JobRegistry")]
public class WaitingStateAdmissionTests : IDisposable
{
    private static readonly object _populateLock = new();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-admission-" + Guid.NewGuid());

    public WaitingStateAdmissionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    private QueueRepository NewRepository() =>
        new(Path.Combine(_dir, "queues.json"),
            (job, action) => action(job),
            (job, action) => { action(job); return Task.CompletedTask; });

    private Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Id = 1, Space = new Space { RootDirectory = _dir }, Status = JobStatus.Waiting };
    }

    /// <summary>HandleWaitingState is private; nothing else drives it without the daemon timer.</summary>
    private static Task Invoke(QueueRepository repository, Job job, JobQueue queue) =>
        (Task)typeof(QueueRepository)
            .GetMethod("HandleWaitingState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, new object[] { job, queue })!;

    private ClusterQueue ManagedQueue(QueueRepository repository, int cores)
    {
        var queue = (ClusterQueue)repository.CreateClusterQueue();
        queue.Alias           = "managed";
        queue.SchedulerType   = ClusterScheduler.Managed;
        queue.ManagedCores    = cores;
        queue.ManagedMemoryGb = 1024;
        queue.ManagedGpus     = 0;
        return queue;
    }

    [Fact]
    public void CreateClusterQueue_AttachesTheHostWideExecutor()
    {
        var repository = NewRepository();

        var first  = (ClusterQueue)repository.CreateClusterQueue();
        var second = (ClusterQueue)repository.CreateClusterQueue();

        // One host, one ledger. Two executors would let both queues hand out CUDA device 0.
        Assert.NotNull(repository.ManagedExecutor);
        Assert.Same(repository.ManagedExecutor, first.Executor);
        Assert.Same(repository.ManagedExecutor, second.Executor);
    }

    [Fact]
    public async Task ABusyQueue_LeavesTheJobWaiting_NotStranded_InStaging()
    {
        var repository = NewRepository();
        var queue = ManagedQueue(repository, cores: 1);

        var holder = NewJob();
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(holder));   // the one core is taken

        var waiting = NewJob();
        await Invoke(repository, waiting, queue);

        Assert.Equal(JobStatus.Waiting, waiting.Status);
    }

    [Fact]
    public async Task ARejectedJob_FailsOnce_RatherThanRepeatingEveryTick()
    {
        var repository = NewRepository();
        var queue = ManagedQueue(repository, cores: 0);   // CreateMask needs 1; can never fit

        var job = NewJob();
        await Invoke(repository, job, queue);

        Assert.Equal(JobStatus.Failed, job.Status);
    }
}
