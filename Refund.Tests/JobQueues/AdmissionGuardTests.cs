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
    private static Job NewJob()
    {
        JobRegistry.EnsurePopulated();
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
    public void NothingIsAdmittedWhileAQueueEditIsBeingJudgedAndApplied()
    {
        // ValidateManagedQueueChange asked the executor whether it had entries, released its lock,
        // and only then applied the mutation. CanAdmit could reserve in that window, so a totals or
        // scheduler edit that should have been refused went through — and if the scheduler was
        // switched, the job admitted in the window took the external submission branch while
        // leaving a managed reservation stranded. Admission is closed for the whole operation.
        var executor = new ManagedExecutor();
        var queue = ManagedQueue(cores: 8, gpus: 0, executor);

        AdmissionResult raced = null;

        executor.WithAdmissionSuspended(
            _ => true,
            hasLiveEntries =>
            {
                Assert.False(hasLiveEntries());          // the verdict the edit would act on

                // What the daemon does concurrently in the real window.
                raced = queue.CanAdmit(NewJob());

                // And the verdict still holds when the mutation is about to be applied.
                Assert.False(hasLiveEntries());
            });

        Assert.IsType<AdmissionResult.Busy>(raced);      // Busy: the edit takes milliseconds
        Assert.Empty(executor.LiveAllocations());        // and nothing was booked behind it

        // Admission reopens once the edit is over.
        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(NewJob()));
    }

    [Fact]
    public void AnEditThatThrows_StillReopensAdmission()
    {
        // The refusal path is the common one — that is what the rules are for — so a suspension
        // that leaked on a throw would wedge the host on the user's first rejected edit.
        var executor = new ManagedExecutor();
        var queue = ManagedQueue(cores: 8, gpus: 0, executor);

        Assert.Throws<InvalidOperationException>(() =>
            executor.WithAdmissionSuspended(_ => true, _ => throw new InvalidOperationException("refused")));

        Assert.IsType<AdmissionResult.Admit>(queue.CanAdmit(NewJob()));
    }
}

/// <summary>
/// Shared fixture for the two daemon-level classes below: a scratch directory, a repository whose
/// update callbacks run inline, and a managed queue registered on it.
/// </summary>
public abstract class DaemonTestBase : IDisposable
{
    protected readonly string _dir;

    protected DaemonTestBase(string prefix)
    {
        _dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    protected QueueRepository NewRepository() =>
        new(Path.Combine(_dir, "queues.json"),
            (job, action) => action(job),
            (job, action) => { action(job); return Task.CompletedTask; });

    protected Job NewJob()
    {
        JobRegistry.EnsurePopulated();
        return new MaskJob { Id = 1, Space = new Space { RootDirectory = _dir }, Status = JobStatus.Waiting };
    }

    protected ClusterQueue ManagedQueue(QueueRepository repository, int cores = 8)
    {
        var queue = (ClusterQueue)repository.CreateClusterQueue();
        queue.Alias           = "managed";
        queue.SchedulerType   = ClusterScheduler.Managed;
        queue.ManagedCores    = cores;
        queue.ManagedMemoryGb = 1024;
        queue.ManagedGpus     = 0;
        return queue;
    }

    /// <summary>The daemon's state handlers are private; nothing else drives them without the timer.</summary>
    protected static Task InvokeHandler(QueueRepository repository, string name, params object[] args) =>
        (Task)typeof(QueueRepository)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, args)!;
}

/// <summary>
/// The daemon-side half: that HandleWaitingState asks <em>before</em> it writes Staging. Placed
/// after the transition, a Busy job would strand in Staging with nothing running it, and the whole
/// reason Busy is not an exception is that the daemon must be able to retry from Waiting.
/// </summary>
[Collection("JobRegistry")]
public class WaitingStateAdmissionTests : DaemonTestBase
{
    public WaitingStateAdmissionTests() : base("relay-admission-") { }

    private static Task Invoke(QueueRepository repository, Job job, JobQueue queue) =>
        InvokeHandler(repository, "HandleWaitingState", job, queue);

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
        JobRegistry.EnsurePopulated();

        var repository = NewRepository();
        var queue = ManagedQueue(repository, cores: 8);

        // Deliberately *not* populating ListJobsTemplate and CancelManyJobsTemplate. The editor
        // hides both fields for a managed queue, so a realistically-configured one has them empty —
        // and populating them here let the guard pass the template checks and reach the managed
        // rejection by luck, hiding that a real managed queue was told to "add a List Jobs
        // template" instead. See PoolPreflightError.
        Assert.True(string.IsNullOrWhiteSpace(queue.ListJobsTemplate));
        Assert.True(string.IsNullOrWhiteSpace(queue.CancelManyJobsTemplate));

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

        // Not the hidden-field advice: the user cannot act on it, and it is not the real problem.
        Assert.DoesNotContain("List Jobs template", error);
        Assert.DoesNotContain("Cancel Many Jobs template", error);

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
        JobRegistry.EnsurePopulated();

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
public class ManagedContainmentTests : DaemonTestBase
{
    public ManagedContainmentTests() : base("relay-containment-") { }

    private string RegistryPath => Path.Combine(_dir, "managed-processes.json");

    private static Task InvokeAborting(QueueRepository repository, Job job, JobQueue queue) =>
        InvokeHandler(repository, "HandleAbortingState", job, queue, false);

    private static Task InvokeDaemon(QueueRepository repository) =>
        InvokeHandler(repository, "RunDaemonAsync");

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
