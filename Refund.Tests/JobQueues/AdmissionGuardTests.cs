using System.Reflection;
using Refund.DataModel;
using Refund.JobQueues;
using Refund.Services.Core.Repositories;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;
using PooledJob = Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D.MotionAndCTF2D;

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

    /// <summary>
    /// A worker pool submits bare scripts with no resource request, so a managed queue cannot back
    /// one. That is a configuration fault, not a transient one: thrown rather than failed, the
    /// enclosing catch logged a stack trace to error.txt and left the job Waiting for the next tick
    /// to repeat — the exact log-and-stick-forever pathology Reject exists to prevent. It is the
    /// default path on a one-queue workstation, where the managed queue is the only selectable one.
    /// </summary>
    [Fact]
    public async Task APoolJobOnAManagedPoolQueue_Fails_WithOneMessage()
    {
        EnsurePopulated();

        var repository = NewRepository();
        var queue = ManagedQueue(repository, cores: 8);
        queue.ListJobsTemplate       = "squeue -u $USER -h -o \"%i,%T\"";
        queue.CancelManyJobsTemplate = "scancel {{job_ids}}";

        var job = new PooledJob
        {
            Id            = 2,
            Space         = new Space { RootDirectory = _dir },
            Status        = JobStatus.Waiting,
            UseWorkerPool = true,
            PoolQueueId   = queue.Id,           // the managed queue, as the only selectable one
        };

        await Invoke(repository, job, queue);
        Assert.Equal(JobStatus.Failed, job.Status);

        // What the daemon would do next tick if the job had been left Waiting.
        job.Status = JobStatus.Waiting;
        await Invoke(repository, job, queue);

        var error = File.ReadAllText(job.ErrorFilePath);
        Assert.Contains("managed queue", error);
        Assert.DoesNotContain("InvalidOperationException", error);      // a reason, not a stack trace

        // Two ticks, two failures — but the second was a deliberate re-run of the same guard, so
        // what matters is that one tick writes exactly one line.
        Assert.Equal(2, File.ReadAllLines(job.ErrorFilePath).Count(l => l.Contains("managed queue")));
    }

    /// <summary>
    /// The other two pool preflight guards have the same shape and wedge identically — a pool queue
    /// missing its ListJobsTemplate is no more transient than a managed one.
    /// </summary>
    [Fact]
    public async Task APoolQueueMissingItsTemplates_FailsTheJob_RatherThanRetryingForever()
    {
        EnsurePopulated();

        var repository = NewRepository();
        var queue = ManagedQueue(repository, cores: 8);

        // A plain cluster queue that would be a valid pool target but for its missing templates.
        var poolQueue = (ClusterQueue)repository.CreateClusterQueue();
        poolQueue.Alias = "slurm";

        var job = new PooledJob
        {
            Id            = 3,
            Space         = new Space { RootDirectory = _dir },
            Status        = JobStatus.Waiting,
            UseWorkerPool = true,
            PoolQueueId   = poolQueue.Id,
        };

        await Invoke(repository, job, queue);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("List Jobs template", File.ReadAllText(job.ErrorFilePath));
    }

    /// <summary>
    /// Busy is the steady state of a managed queue — that is the whole feature — so anything
    /// HandleWaitingState writes before the admission switch is written once per second, forever,
    /// into a file the UI tails.
    /// </summary>
    [Fact]
    public async Task ABusyJob_DoesNotAccumulateLifecycleLines()
    {
        var repository = NewRepository();
        var queue = ManagedQueue(repository, cores: 1);

        var holder = NewJob();
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(holder));   // the one core is taken

        var waiting = new MaskJob { Id = 2, Space = new Space { RootDirectory = _dir },
                                    Status = JobStatus.Waiting };

        for (int tick = 0; tick < 5; tick++)
            await Invoke(repository, waiting, queue);

        Assert.Equal(JobStatus.Waiting, waiting.Status);
        Assert.False(File.Exists(waiting.LifecycleFilePath),
                     "A job that never left Waiting has no staging history to record.");
    }
}

/// <summary>
/// The two ways a managed job's compute can outlive the job: an abort that never reaches the
/// executor, and a reconciliation that never happens because nothing asked.
/// </summary>
[Collection("JobRegistry")]
public class ManagedContainmentTests : IDisposable
{
    private static readonly object _populateLock = new();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-containment-" + Guid.NewGuid());

    public ManagedContainmentTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeProcess : IManagedProcess
    {
        public int Pid => 4242;
        public DateTime StartTime => new(2026, 1, 1);
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }

        public void Exit(int code) { ExitCode = code; HasExited = true; }
        public void KillTree() => HasExited = true;
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    private string RegistryPath => Path.Combine(_dir, "managed-processes.json");

    private QueueRepository NewRepository() =>
        new(Path.Combine(_dir, "queues.json"),
            (job, action) => action(job),
            (job, action) => { action(job); return Task.CompletedTask; });

    private Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Id = 1, Space = new Space { RootDirectory = _dir }, Status = JobStatus.Waiting };
    }

    private ClusterQueue ManagedQueue(QueueRepository repository)
    {
        var queue = (ClusterQueue)repository.CreateClusterQueue();
        queue.Alias           = "managed";
        queue.SchedulerType   = ClusterScheduler.Managed;
        queue.ManagedCores    = 8;
        queue.ManagedMemoryGb = 1024;
        queue.ManagedGpus     = 0;
        return queue;
    }

    private static Task InvokeAborting(QueueRepository repository, Job job, JobQueue queue) =>
        (Task)typeof(QueueRepository)
            .GetMethod("HandleAbortingState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, new object[] { job, queue, false })!;

    private static Task InvokeDaemon(QueueRepository repository) =>
        (Task)typeof(QueueRepository)
            .GetMethod("RunDaemonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, Array.Empty<object>())!;

    [Fact]
    public async Task AbortingAManagedJobBeforeItHasAPid_StillReachesTheExecutor()
    {
        var repository = NewRepository();
        var queue = ManagedQueue(repository);

        // Exactly the staging window: admitted and holding the host, but Launch has not returned
        // so nothing has written a ClusterJobId. Gated on one, the abort never fired at all and
        // the reservation — and, moments later, a real process — outlived the job.
        var job = NewJob();
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(job));
        Assert.True(string.IsNullOrWhiteSpace(job.ClusterJobId));
        Assert.Single(repository.ManagedExecutor.LiveAllocations());

        job.Status = JobStatus.Aborting;
        await InvokeAborting(repository, job, queue);

        Assert.Empty(repository.ManagedExecutor.LiveAllocations());
    }

    [Fact]
    public async Task TheDaemonReconciles_EvenWhenNoOtherJobIsLeftToAsk()
    {
        var repository = NewRepository();

        var job = NewJob();
        var process = new FakeProcess();
        Assert.IsType<AdmissionResult.Admit>(
            repository.ManagedExecutor.TryAdmit(job, new ResourceTotals(8, 64, 0)));
        repository.ManagedExecutor.Launch(job, _ => process);

        Assert.Single(new ManagedProcessRegistry(RegistryPath).Load());

        process.Exit(0);
        job.Status = JobStatus.Aborted;     // settled, and in no queue: nothing will ever ask again

        // Every queue is empty, so the daemon's queue loop does no work whatsoever. Only the
        // unconditional Reap can retire the entry and drop its leftover record.
        await InvokeDaemon(repository);

        Assert.Empty(new ManagedProcessRegistry(RegistryPath).Load());
    }
}
