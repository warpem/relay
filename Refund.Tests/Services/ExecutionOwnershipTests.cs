using System.Reflection;
using System.Text.Json.Nodes;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobExecution;
using Refund.JobQueues;
using Refund.Jobs.Common.Import.ImportMap;
using Refund.Jobs.Common.Notes.Note;
using Refund.Jobs.Refinement.Masks.CreateMask;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public sealed class ExecutionOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ParentCanClearWhileChildIsClearingButDuplicateClearIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new BlockingClearJob(entered, release) { Id = 99, Status = JobStatus.Finished };
        fixture.MutableTarget.Space.AddJob(child, null);
        var readOnlyChild = new TestReadOnlyJob(child);
        // Supply a wrapper for this test-only subclass without registering a production job type.
        var wrappers = (System.Runtime.CompilerServices.ConditionalWeakTable<Job, ReadOnlyJob>)typeof(Job)
            .GetField("ReadOnlyCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        wrappers.Add(child, readOnlyChild);
        await fixture.Manager.CreateEdge(fixture.Space,
            fixture.Parent.PortsOut[ImportMap.PortOutMap], readOnlyChild.PortsIn[CreateMask.PortInMap]);
        await fixture.Manager.UpdateJob(fixture.User, fixture.Parent, job => job.Status = JobStatus.Finished);
        var clearing = fixture.Manager.ClearJob(fixture.User, readOnlyChild);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.Equal(JobStatus.Clearing, child.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Manager.ClearJob(fixture.User, readOnlyChild));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Manager.DeleteJob(fixture.User, fixture.Parent));

            await fixture.Manager.ClearJob(fixture.User, fixture.Parent).WaitAsync(Timeout);

            Assert.Equal(JobStatus.Building, fixture.Parent.Status);
            Assert.Equal(JobStatus.Clearing, child.Status);
            Assert.False(clearing.IsCompleted);
        }
        finally
        {
            release.Set();
            await clearing.WaitAsync(Timeout);
        }

        Assert.Equal(JobStatus.Building, fixture.Parent.Status);
        Assert.Equal(JobStatus.Building, child.Status);
    }

    [Theory]
    [InlineData(JobStatus.Waiting)]
    [InlineData(JobStatus.Staging)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Finalizing)]
    [InlineData(JobStatus.Aborting)]
    public async Task ParentClearStillRejectsExecutingDependents(JobStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MutableTarget.Status = status;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.ClearJob(fixture.User, fixture.Parent));

        Assert.Equal(status, fixture.Target.Status);
        Assert.DoesNotContain(fixture.MutableTarget.Space.FindJob(fixture.Parent.Id).Events, evt => evt.Type == EventType.ClearingStarted);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Aborted)]
    [InlineData(JobStatus.Interrupted)]
    public async Task UnfinishedParentCanClearWithoutAbortingWaitingDescendants(JobStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UpdateJob(fixture.User, fixture.Parent, job => job.Status = status);
        var otherChild = await fixture.Manager.CreateJob(fixture.User, fixture.Space.Views[0], new CreateMask().TypeGuid);
        await fixture.Manager.CreateEdge(fixture.Space,
            fixture.Parent.PortsOut[ImportMap.PortOutMap], otherChild.PortsIn[CreateMask.PortInMap]);
        await fixture.QueueAsync().WaitAsync(Timeout);
        await fixture.Manager.QueueClusterJob(fixture.User, otherChild, fixture.Queue).WaitAsync(Timeout);
        var runtime = Field<ExecutionRuntime>(fixture.Queues, "_runtime");
        var attemptIds = runtime.Attempts.Select(attempt => attempt.Id).Order().ToArray();
        Directory.CreateDirectory(fixture.Parent.DirectoryPath);
        var output = Path.Combine(fixture.Parent.DirectoryPath, "partial.mrc");
        await File.WriteAllTextAsync(output, "partial output");

        await fixture.Manager.ClearJob(fixture.User, fixture.Parent).WaitAsync(Timeout);
        await runtime.TickAsync().WaitAsync(Timeout);

        Assert.Equal(JobStatus.Building, fixture.Parent.Status);
        Assert.False(File.Exists(output));
        Assert.Equal(JobStatus.Waiting, fixture.Target.Status);
        Assert.Equal(JobStatus.Waiting, otherChild.Status);
        Assert.Equal(attemptIds, runtime.Attempts.Select(attempt => attempt.Id).Order());
        Assert.All(runtime.Attempts, attempt => Assert.Equal(ExecutionPhase.WaitingForDependencies, attempt.Phase));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.ClearJob(fixture.User, fixture.Target));
    }

    [Fact]
    public async Task FinishedParentStillProtectsOutputsFromAWaitingAttemptThatCanAdvance()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueAsync().WaitAsync(Timeout);
        await fixture.Manager.UpdateJob(fixture.User, fixture.Parent, job => job.Status = JobStatus.Finished);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.ClearJob(fixture.User, fixture.Parent));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("parameters")]
    public async Task BuildingJobRemainsEditableWithDependencyWaitingChild(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var child = await fixture.Manager.CreateJob(fixture.User, fixture.Space.Views[0],
            new Refund.Jobs.Refinement.PostProcess.PostProcess3D.PostProcess().TypeGuid);
        await fixture.Manager.CreateEdge(fixture.Space, fixture.Target.PortsOut[CreateMask.PortOutMask],
            child.PortsIn[Refund.Jobs.Refinement.PostProcess.PostProcess3D.PostProcess.PortInMask]);
        if (mutation == "create")
            await fixture.Manager.DeleteEdge(fixture.Edge);
        var runtime = Field<ExecutionRuntime>(fixture.Queues, "_runtime");
        await runtime.RequestRunAsync(new JobAddress(fixture.Space.Project.Id, fixture.Space.Id, child.Id),
            fixture.Queue.Id, ResourceVector.None, dependenciesReady: false);
        var attemptId = Assert.Single(runtime.Attempts).Id;

        switch (mutation)
        {
            case "delete":
                await fixture.Manager.DeleteEdge(fixture.Edge);
                Assert.Empty(fixture.Target.PortsIn[CreateMask.PortInMap].Edges);
                break;
            case "create":
                await fixture.Manager.CreateEdge(fixture.Space,
                    fixture.Parent.PortsOut[ImportMap.PortOutMap], fixture.Target.PortsIn[CreateMask.PortInMap]);
                Assert.Single(fixture.Target.PortsIn[CreateMask.PortInMap].Edges);
                break;
            case "update":
                bool updated = false;
                await fixture.Manager.UpdateEdge(fixture.Edge, _ => updated = true);
                Assert.True(updated);
                break;
            case "parameters":
                await fixture.Manager.UpdateJobParameters(fixture.User, fixture.Target,
                    job => ((CreateMask)job).NThreads = 7);
                Assert.Equal(7, ((CreateMask)fixture.MutableTarget).NThreads);
                break;
        }

        await runtime.TickAsync().WaitAsync(Timeout);
        var attempt = Assert.Single(runtime.Attempts);
        Assert.Equal(attemptId, attempt.Id);
        Assert.Equal(ExecutionPhase.WaitingForDependencies, attempt.Phase);
        Assert.Equal(JobStatus.Building, fixture.Target.Status);
        Assert.Equal(JobStatus.Waiting, child.Status);
    }

    [Fact]
    public async Task FailedClearReportsTheOriginalErrorAndLeavesTheJobRetryable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failure = new IOException("Directory not empty");
        var job = new FailingClearJob(failure)
        {
            Id = 99,
            Status = JobStatus.Finished,
            VisAvailableIteration = 7
        };
        fixture.MutableTarget.Space.AddJob(job, null);

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Manager.ClearJob(fixture.User, new TestReadOnlyJob(job)));

        Assert.Same(failure, thrown);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.False(job.Status.IsUnsettled());
        Assert.True(job.CanTransitionState(JobStatus.Clearing));
        Assert.Equal(7, job.VisAvailableIteration);
        Assert.Equal(new[] { EventType.ClearingStarted, EventType.Failed },
            job.Events.Select(evt => evt.Type));
        Assert.Contains("Directory not empty", await File.ReadAllTextAsync(job.ErrorFilePath));
    }

    [Fact]
    public async Task SuccessfulClearResetsResultsAndPreservesParameters()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = (CreateMask)fixture.MutableTarget;
        job.Status = JobStatus.Finished;
        job.NThreads = 7;
        job.VisAvailableIteration = 3;
        Directory.CreateDirectory(job.DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(job.DirectoryPath, "result.mrc"), "old result");

        await fixture.Manager.ClearJob(fixture.User, fixture.Target);

        Assert.Equal(JobStatus.Building, job.Status);
        Assert.Equal(7, job.NThreads);
        Assert.Equal(-1, job.VisAvailableIteration);
        Assert.Empty(Directory.EnumerateFileSystemEntries(job.DirectoryPath));
        Assert.Equal(EventType.ClearingFinished, job.Events.Last().Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PooledJobsAppearOnlyInTheirManagerQueueWhileBothQueuesRemainInUse(bool sharedQueue)
    {
        await using var fixture = await Fixture.CreateAsync();
        var workers = sharedQueue ? fixture.Queue : await fixture.Manager.CreateClusterQueue(
            new ClusterQueue { Alias = "Worker scheduler", SchedulerType = ClusterScheduler.Slurm });
        var runtime = Field<ExecutionRuntime>(fixture.Queues, "_runtime");
        await runtime.RequestRunAsync(
            new JobAddress(fixture.Space.Project.Id, fixture.Space.Id, fixture.Target.Id),
            fixture.Queue.Id, ResourceVector.None, dependenciesReady: false,
            new WorkerGroupRequest(workers.Id, 2, 200));

        Assert.Same(fixture.Target, Assert.Single(fixture.Queue.QueuedJobs));
        if (!sharedQueue)
            Assert.Empty(workers.QueuedJobs);
        Assert.Single(fixture.Manager.ClusterQueues.SelectMany(queue => queue.QueuedJobs));

        foreach (int queueId in new[] { fixture.Queue.Id, workers.Id }.Distinct())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Queues.DeleteClusterQueueAsync((ClusterQueue)fixture.Queues.FindQueue(queueId)));
    }

    [Fact]
    public async Task DataCommandsReturnTheirTaskBeforeSynchronousRepositoryWorkFinishes()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Task.Run(() =>
        {
            var command = fixture.Manager.UpdateJob(fixture.User, fixture.Target, job =>
            {
                started.SetResult();
                release.Wait(TimeSpan.FromSeconds(10));
                job.Alias = "Updated in the background";
            });
            returned.SetResult(command);
        });
        try
        {
            await started.Task.WaitAsync(Timeout);
            var command = await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(command.IsCompleted);
        }
        finally
        {
            release.Set();
            await caller.WaitAsync(Timeout);
            await (await returned.Task).WaitAsync(Timeout);
        }
        Assert.Equal("Updated in the background", fixture.Target.Alias);
    }

    [Fact]
    public async Task CloningResetsResultsWithoutTouchingTheFilesystem()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MutableTarget.VisAvailableIteration = 7;
        string cloneDirectory = Path.Combine(fixture.Space.RootDirectory,
            (fixture.Space.Jobs.Max(job => job.Id) + 1).ToString());
        Directory.CreateDirectory(cloneDirectory);
        string existingFile = Path.Combine(cloneDirectory, "retained.txt");
        await File.WriteAllTextAsync(existingFile, "Only execution preparation may clear this directory");

        var clone = await fixture.Manager.CloneJob(fixture.User, fixture.Target, fixture.Space.Views[0]);

        Assert.Equal(JobStatus.Building, clone.Status);
        Assert.Equal(-1, clone.VisAvailableIteration);
        Assert.Equal(7, fixture.Target.VisAvailableIteration);
        Assert.True(File.Exists(existingFile));
        Assert.Single(clone.GetParents());
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("delete")]
    [InlineData("parameters")]
    public async Task AdmissionOwnsTheJobBeforeConcurrentMutationsCanProceed(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var configurationGate = Field<SemaphoreSlim>(fixture.Queues, "_configurationGate");
        await configurationGate.WaitAsync();
        Task? queueing = null;
        Task? changing = null;
        try
        {
            // Admission claims the command lock before returning its Task. A second
            // API call must remain behind it until execution ownership exists.
            queueing = fixture.QueueAsync();
            Assert.False(queueing.IsCompleted);
            changing = mutation switch
            {
                "clear" => fixture.Manager.ClearJob(fixture.User, fixture.Target),
                "delete" => fixture.Manager.DeleteJob(fixture.User, fixture.Target),
                _ => fixture.Manager.UpdateJobParameters(fixture.User, fixture.Target,
                    job => ((CreateMask)job).NThreads = 99)
            };
            Assert.False(changing.IsCompleted);
        }
        finally
        {
            configurationGate.Release();
        }

        await queueing!.WaitAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changing!.WaitAsync(Timeout));

        fixture.AssertWaitingForDependency();
        Assert.NotNull(fixture.Space.FindJob(fixture.Target.Id));
        Assert.Equal(1, ((CreateMask)fixture.MutableTarget).NThreads);
    }

    [Fact]
    public async Task ResizeRejectsJobsWithoutARunningPool()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.ResizeJobPool(fixture.User, fixture.Target, 2));
        await fixture.QueueAsync().WaitAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.ResizeJobPool(fixture.User, fixture.Target, 2));

        fixture.AssertWaitingForDependency();
        Assert.Null(fixture.Target.PoolDesiredSize);
    }

    [Fact]
    public async Task MetadataRemainsEditableWhileExecutionParametersAreOwned()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.QueueAsync().WaitAsync(Timeout);

        await fixture.Manager.UpdateJob(fixture.User, fixture.Target, job =>
        {
            job.Alias = "A useful label";
            job.Notes = "Review after completion";
        }).WaitAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.UpdateJobParameters(fixture.User, fixture.Target,
                job => ((CreateMask)job).NThreads = 99));

        Assert.Equal("A useful label", fixture.Target.Alias);
        Assert.Equal("Review after completion", fixture.Target.Notes);
        Assert.Equal(1, ((CreateMask)fixture.MutableTarget).NThreads);
        fixture.AssertWaitingForDependency();
    }

    [Fact]
    public async Task AdmissionPersistsTheFrozenDefinitionWithoutWaitingForAutosave()
    {
        await using var fixture = await Fixture.CreateAsync();
        var data = Field<DataRepository>(fixture.Manager, "_dataRepository");
        data.StopAutoSave();
        try
        {
            data.SaveSpaceImmediately(fixture.MutableTarget.Space);
            await fixture.Manager.UpdateJobParameters(fixture.User, fixture.Target,
                job => ((CreateMask)job).NThreads = 7);
            Assert.Equal(1, await SavedThreadCount());

            await fixture.QueueAsync().WaitAsync(Timeout);

            Assert.Equal(7, await SavedThreadCount());
            fixture.AssertWaitingForDependency();
        }
        finally
        {
            data.StartAutoSave(System.Threading.Timeout.Infinite);
        }

        async Task<int> SavedThreadCount()
        {
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Space.FilePath))!;
            var job = saved["Jobs"]!.AsArray().Select(entry => entry!["Job"]!)
                .Single(entry => entry["Id"]!.GetValue<int>() == fixture.Target.Id);
            return job["NThreads"]!.GetValue<int>();
        }
    }

    [Fact]
    public async Task PendingExecutionProtectsItsInputConnectionAndSourceFromDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        Directory.CreateDirectory(fixture.Parent.DirectoryPath);
        string sourceFile = Path.Combine(fixture.Parent.DirectoryPath, "input.mrc");
        await File.WriteAllTextAsync(sourceFile, "retained source data");
        await fixture.QueueAsync().WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.DeleteEdge(fixture.Edge));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.CreateEdge(fixture.Space,
                fixture.Parent.PortsOut[ImportMap.PortOutMap],
                fixture.Target.PortsIn[CreateMask.PortInMap]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.DeleteJob(fixture.User, fixture.Parent));

        Assert.Equal("retained source data", await File.ReadAllTextAsync(sourceFile));
        Assert.Single(fixture.Target.PortsIn[CreateMask.PortInMap].Edges);
        Assert.NotNull(fixture.Space.FindJob(fixture.Parent.Id));
        fixture.AssertWaitingForDependency();
    }

    private sealed class BlockingClearJob(TaskCompletionSource entered, ManualResetEventSlim release) : CreateMask
    {
        public override void Clear()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release the clear operation.");
        }
    }

    private sealed class FailingClearJob(IOException failure) : Note
    {
        public override void Clear() => throw failure;
    }

    private sealed class TestReadOnlyJob(Job job) : ReadOnlyJob(job);

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target) ?? throw new InvalidOperationException($"Field {name} was not found."));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;

        private Fixture(string directory)
        {
            _directory = directory;
            Manager = new DataManager(new RelayConfiguration
            {
                UsersPath = Path.Combine(directory, "users.json"),
                ProjectsPath = Path.Combine(directory, "projects.json"),
                QueuesPath = Path.Combine(directory, "queues.json")
            });
        }

        public DataManager Manager { get; }
        public ReadOnlyUser User { get; private set; } = null!;
        public ReadOnlySpace Space { get; private set; } = null!;
        public ReadOnlyJob Parent { get; private set; } = null!;
        public ReadOnlyJob Target { get; private set; } = null!;
        public ReadOnlyEdge Edge { get; private set; } = null!;
        public ReadOnlyJobQueue Queue { get; private set; } = null!;
        public QueueRepository Queues => Field<QueueRepository>(Manager, "_queueRepository");
        public Job MutableTarget => Field<DataRepository>(Manager, "_dataRepository")
            .FindJob(Space.Project.Id, Space.Id, Target.Id);

        public static async Task<Fixture> CreateAsync()
        {
            JobRegistry.EnsurePopulated();
            string directory = Path.Combine(Path.GetTempPath(), "relay-ownership-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory);
            try
            {
                fixture.User = await fixture.Manager.CreateUser(new User { Name = "Ownership test" });
                var project = await fixture.Manager.CreateProject(fixture.User);
                fixture.Space = await fixture.Manager.CreateSpace(fixture.User, project,
                    new Space { RootDirectory = directory });
                var view = await fixture.Manager.CreateView(fixture.User, fixture.Space);
                fixture.Parent = await fixture.Manager.CreateJob(fixture.User, view, new ImportMap().TypeGuid);
                fixture.Target = await fixture.Manager.CreateJob(fixture.User, view, new CreateMask().TypeGuid);
                fixture.Edge = await fixture.Manager.CreateEdge(fixture.Space,
                    fixture.Parent.PortsOut[ImportMap.PortOutMap],
                    fixture.Target.PortsIn[CreateMask.PortInMap]);
                fixture.Queue = await fixture.Manager.CreateClusterQueue(new ClusterQueue
                {
                    Alias = "Test scheduler",
                    QueueType = JobQueueType.CPU,
                    SchedulerType = ClusterScheduler.Slurm,
                    SubmissionScriptTemplate = "{{ command }}",
                    SubmitJobTemplate = "exit 99",
                    StatusJobTemplate = "exit 99",
                    AbortJobTemplate = "exit 99"
                });
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public Task QueueAsync() => Manager.QueueClusterJob(User, Target, Queue);

        public void AssertWaitingForDependency()
        {
            var runtime = Field<ExecutionRuntime>(Queues, "_runtime");
            var attempt = Assert.Single(runtime.Attempts);
            Assert.Equal(ExecutionPhase.WaitingForDependencies, attempt.Phase);
            Assert.Null(attempt.Receipt);
            Assert.False(File.Exists(Path.Combine(Target.DirectoryPath, "submit.sh")));
            Assert.Equal(JobStatus.Waiting, Target.Status);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Manager.ShutdownAsync().WaitAsync(Timeout);
            }
            finally
            {
                Field<DataRepository>(Manager, "_dataRepository").Dispose();
                Field<UserRepository>(Manager, "_userRepository").Dispose();
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
