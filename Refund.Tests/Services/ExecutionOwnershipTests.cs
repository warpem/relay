using System.Reflection;
using System.Text.Json.Nodes;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobExecution;
using Refund.JobQueues;
using Refund.Jobs.Common.Import.ImportMap;
using Refund.Jobs.Refinement.Masks.CreateMask;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public sealed class ExecutionOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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
            // QueueClusterJob reaches the held configuration gate before returning its Task.
            // A second API call must remain behind admission's data lock until ownership exists.
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
            // Dispose performs one final save and reschedules its timer before disposing it.
            // Keep that teardown path usable without allowing periodic saves during the test.
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
    public async Task PendingExecutionProtectsItsInputConnectionAndSourceFiles()
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
            fixture.Manager.ClearJob(fixture.User, fixture.Parent));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.DeleteJob(fixture.User, fixture.Parent));

        Assert.Equal("retained source data", await File.ReadAllTextAsync(sourceFile));
        Assert.Single(fixture.Target.PortsIn[CreateMask.PortInMap].Edges);
        Assert.NotNull(fixture.Space.FindJob(fixture.Parent.Id));
        fixture.AssertWaitingForDependency();
    }

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
