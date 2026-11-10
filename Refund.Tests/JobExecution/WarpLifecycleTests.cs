using System.Reflection;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobExecution;
using Refund.JobQueues;
using Refund.JobResources;
using Refund.Jobs.Common.Notes.Note;
using Refund.Jobs.Fs.Warp;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;
using Refund.Services.Warp;
using Warp;
using Warp.Tools;

namespace Refund.Tests.JobExecution;

[Collection("JobRegistry")]
public sealed class WarpLifecycleTests
{
    [Fact]
    public void WarpSessionDoesNotStarveExportsOnASingleSlotLocalQueue()
    {
        var local = new LocalQueue();
        var warpResources = QueueRepository.RequestFor(new WarpJob(), local);
        var exportResources = QueueRepository.RequestFor(new WarpParticlesExportJob(), local);
        Assert.Equal(ResourceVector.None, warpResources);
        Assert.Equal(new ResourceVector(1, 0, 0), exportResources);
        Assert.Equal(exportResources, QueueRepository.RequestFor(new Note(), local));
        Assert.Throws<InvalidOperationException>(() => QueueRepository.RequestFor(new InvalidSlotJob(), local));
        var coordinator = new ExecutionCoordinator([new ExecutionQueuePolicy(-1, ExecutionBackendKind.Local, new ResourceVector(1, 0, 0))]);
        var warpRun = coordinator.RequestRun(new(1, 1, 1), -1, warpResources, true);
        coordinator.PreparationCompleted(warpRun.Id);
        Assert.Contains(coordinator.PlanEffects(), effect => effect is StartExecution { AttemptId: var id } && id == warpRun.Id);
        coordinator.StartCompleted(warpRun.Id, new BackendReceipt("warp"), isRunning: true);
        var export = coordinator.RequestRun(new(1, 1, 2), -1, exportResources, true);
        coordinator.PreparationCompleted(export.Id);
        Assert.Contains(coordinator.PlanEffects(), effect => effect is StartExecution { AttemptId: var id } && id == export.Id);
        coordinator.StartCompleted(export.Id, new BackendReceipt("export"), isRunning: true);
        var ordinary = coordinator.RequestRun(new(1, 1, 3), -1, exportResources, true);
        coordinator.PreparationCompleted(ordinary.Id);
        Assert.DoesNotContain(coordinator.PlanEffects(), effect => effect is StartExecution { AttemptId: var id } && id == ordinary.Id);
    }

    [Fact]
    public async Task ResumePreservesResultsButDiscardsPreviousAttemptTasks()
    {
        string root = Path.Combine(Path.GetTempPath(), "warp-resume-" + Guid.NewGuid());
        var space = new Space { Id = 2, RootDirectory = root, Project = new Project { Id = 1 } };
        var job = new WarpJob { Id = 3, Space = space };
        Directory.CreateDirectory(job.Identity.QueueDirectory);
        File.WriteAllText(Path.Combine(job.DirectoryPath, "warp-mini.json"), "saved state");
        File.WriteAllText(Path.Combine(job.Identity.QueueDirectory, "stale-task.json"), "old work");
        Assert.NotNull(job.PortsIn);
        var restored = new WarpJob { Space = space };
        restored.ReadFromJson(job.ToJson());
        Assert.NotNull(restored.PortsIn);
        var operations = Operations(restored);
        try
        {
            await operations.PrepareAsync(Attempt(), CancellationToken.None);
            Assert.Equal("saved state", File.ReadAllText(Path.Combine(job.DirectoryPath, "warp-mini.json")));
            Assert.Empty(Directory.EnumerateFiles(job.Identity.QueueDirectory));
            Assert.True(restored.ControlsOwnExecution);
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OrdinaryLocalPreparationStillResetsItsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-normal-" + Guid.NewGuid());
        var job = new Note { Id = 3, Space = new Space { RootDirectory = root } };
        Directory.CreateDirectory(job.DirectoryPath);
        string stale = Path.Combine(job.DirectoryPath, "old.result");
        File.WriteAllText(stale, "old result");
        var operations = Operations(job);
        try { await operations.PrepareAsync(Attempt(), CancellationToken.None); Assert.False(File.Exists(stale)); }
        finally { await operations.ShutdownAsync(CancellationToken.None); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InjectedAsyncLocalRunnerIsOwnedAndCanceledByExecution()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-warp-runner-" + Guid.NewGuid());
        var job = new WarpJob { Id = 3, Space = new Space { RootDirectory = root, Project = new Project() } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RelayExecutionOperations(_ => job, (j, action) => { action(j); return Task.CompletedTask; },
            async (_, token) => { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { exited.SetResult(); } });
        try
        {
            await operations.StartAsync(Attempt(), [], CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BackendObservationKind.Running, (await operations.ObserveAsync(Attempt(), CancellationToken.None)).Kind);
            await operations.ShutdownAsync(CancellationToken.None);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OwnedSessionRejectsSetupAndDuplicateStartWithoutDiscardingState()
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        var checkpoint = Path.Combine(f.Job.DirectoryPath, "warp-mini.json");
        string saved = File.ReadAllText(checkpoint);
        await f.Manager.StartWarpJob(f.User, f.Job, f.Queue.Id, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root, Pixel = 2, TargetPixelSize = 2 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.StartWarpJob(f.User, f.Job, f.Queue.Id, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.ConfigureWarpWorkers(f.User, f.Job, f.Queue.Id, 2, 1, 8));
        var activeSession = f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.ClearJob(f.User, f.Job));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.DeleteJob(f.User, f.Job));
        Assert.Same(activeSession, f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings));
        Assert.Equal(1m, f.Job.Settings.Pixel);
        Assert.True(File.Exists(checkpoint));
        await f.Manager.StopWarpJob(f.User, f.Job);
        Assert.True(File.Exists(checkpoint));
    }

    [Theory]
    [InlineData(UserRole.Viewer)]
    [InlineData(UserRole.User)]
    public async Task ReadOnlyAndUnrelatedUsersCannotMutateWarpSessions(UserRole role)
    {
        await using var f = await Fixture.Create();
        var other = await f.Manager.CreateUser(new User { Name = "Other", Role = role });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Manager.StartWarpJob(other, f.Job, f.Queue.Id, 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Manager.StopWarpJob(other, f.Job));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Manager.ResizeWarpJob(other, f.Job, 2));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Manager.ApplyWarpSettings(other, f.Job, new()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Manager.UpdateWarpSession(other, f.Job, session => session.Train()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Manager.ExportWarpJob(other, f.Job, false));
    }

    [Fact]
    public async Task StoppedPoolConfigurationPersistsWithoutStartingWorkers()
    {
        await using var f = await Fixture.Create();
        await f.Manager.ConfigureWarpWorkers(f.User, f.Job, f.Queue.Id, 3, 2, 12);
        await f.Manager.ResizeWarpJob(f.User, f.Job, 5);
        Assert.Equal(5, f.Job.NGpus);
        Assert.Equal(2, f.Job.PerDevice);
        Assert.Equal(f.Queue.Id, f.Job.PoolQueueId);
        Assert.Equal(JobStatus.Building, f.Job.Status);
    }

    [Fact]
    public async Task FailedExportReleasesItsDestructiveOperationGuard()
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.ExportWarpJob(f.User, f.Job, true));
        Assert.Empty(Field<HashSet<Job>>(f.Manager, "_warpExportOperations"));
        Assert.False(f.Manager.WarpSessions.GetOrCreate(f.Job.Identity).Exporting);
        await f.Manager.ClearJob(f.User, f.Job);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedExportBusySpansJobCreationAndLifetimeGuard(bool failCreation)
    {
        await using var f=await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User,f.Job,new(){InputPath=f.Root});
        var session=f.Manager.WarpSessions.GetOrCreate(f.Job.Identity);
        string input=Path.Combine(f.Root,"source.mrc");File.WriteAllText(input,"input");
        string processing=Path.Combine(session.Settings.OutputPath,"source.mrc");Directory.CreateDirectory(session.Settings.OutputPath);
        var metadata=new Movie(processing,f.Root,[]){OptionsCTF=new(){Dimensions=new float3(128,128,4)},CTFResolutionEstimate=3,GridCTFDefocus=new(new int3(1),[1.5f])};
        metadata.SaveMeta();Directory.CreateDirectory(metadata.AverageDir);File.WriteAllText(metadata.AveragePath,"average");
        var row=new WarpMovie{Index=0,Id="source",Name="source.mrc",InputPath=input,ProcessingPath=processing,MetadataPath=metadata.XMLPath,AveragePath=metadata.AveragePath,
            Ctf=3,Motion=1,Done=WarpStage.Motion|WarpStage.Ctf,Completed=new(Field<Dictionary<string,ulong>>(session,"_stageKeys"))};
        Field<List<WarpMovie>>(session,"_movies").Add(row);session.SetDisposition([0],WarpDisposition.Auto);
        var source=Field<DataRepository>(f.Manager,"_dataRepository").FindJob(f.Job.Space.Project.Id,f.Job.Space.Id,f.Job.Id);
        var views=Field<List<View>>(source.Space,"_Views");var savedViews=views.ToArray();if(failCreation)views.Clear();
        var saveGate=Field<SemaphoreSlim>(session,"_saveGate");var commandGate=Field<SemaphoreSlim>(f.Manager,"_globalLock");
        await saveGate.WaitAsync();bool saveHeld=true,commandHeld=false;
        Task<ReadOnlyJob>? operation=null;
        try
        {
            operation=f.Manager.ExportWarpJob(f.User,f.Job,false);
            await WaitUntil(()=>session.Exports.Count==1); // Compilation is done; checkpoint is deliberately paused.
            await commandGate.WaitAsync();commandHeld=true;
            saveGate.Release();saveHeld=false;
            await WaitUntil(()=>!Field<bool>(session,"_dirty")&&saveGate.CurrentCount==1);
            // The queued job cannot be created while the command gate is held. All clients
            // must still see the shared busy state throughout this handoff.
            Assert.True(session.Exporting);Assert.False(operation.IsCompleted);
            Assert.Contains(source,Field<HashSet<Job>>(f.Manager,"_warpExportOperations"));
            commandGate.Release();commandHeld=false;
            if(failCreation)await Assert.ThrowsAsync<InvalidOperationException>(()=>operation);
            else await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(session.Exporting);Assert.Empty(Field<HashSet<Job>>(f.Manager,"_warpExportOperations"));
            if(!failCreation)
            {
                await f.Manager.ExportWarpJob(f.User,f.Job,false).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(session.Exporting);Assert.Equal(2,session.Exports.Count);
            }
        }
        finally
        {
            if(saveHeld)saveGate.Release();if(commandHeld)commandGate.Release();
            if(operation!=null)try{await operation.WaitAsync(TimeSpan.FromSeconds(5));}catch(InvalidOperationException){}
            if(failCreation)views.AddRange(savedViews);
        }
        static async Task WaitUntil(Func<bool> condition)
        {
            for(int i=0;i<500&&!condition();i++)await Task.Delay(10);
            Assert.True(condition(),"Export did not reach the expected handoff within five seconds.");
        }
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("job")]
    [InlineData("space")]
    [InlineData("project")]
    public async Task DestructiveActionsWaitForInProgressExportCompilation(string operation)
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        var source = Field<DataRepository>(f.Manager, "_dataRepository").FindJob(f.Job.Space.Project.Id, f.Job.Space.Id, f.Job.Id);
        var leases = Field<HashSet<Job>>(f.Manager, "_warpExportOperations");
        leases.Add(source); // Pause after export acquires its lifetime lease, before disk compilation completes.
        try
        {
            Func<Task> action = operation switch
            {
                "clear" => () => f.Manager.ClearJob(f.User, f.Job),
                "job" => () => f.Manager.DeleteJob(f.User, f.Job),
                "space" => () => f.Manager.DeleteSpace(f.User, f.Job.Space),
                _ => () => f.Manager.DeleteProject(f.Job.Space.Project)
            };
            await Assert.ThrowsAsync<InvalidOperationException>(action);
            Assert.True(File.Exists(Path.Combine(f.Job.DirectoryPath, "warp-mini.json")));
        }
        finally { leases.Remove(source); }
    }

    [Fact]
    public async Task ClearEvictsCatalogAndRetainsConfiguredSettings()
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root, Pixel = 2, TargetPixelSize = 2 });
        var before = f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings);
        before.FilterMetric("ctf", 3);
        await f.Manager.ClearJob(f.User, f.Job);
        var after = f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings);
        Assert.NotSame(before, after);
        Assert.Equal(5, after.MaxCtf);
        Assert.Equal(2m, after.Settings.Pixel);
        Assert.Equal(f.Root, after.Settings.InputPath);
        Assert.Empty(after.Movies);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("space")]
    [InlineData("project")]
    public async Task DeletingContainersEvictsTheirStoppedSession(string scope)
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        var sessions = (System.Collections.ICollection)Field<object>(f.Manager.WarpSessions, "_sessions");
        Assert.Equal(1, sessions.Count);
        switch (scope)
        {
            case "job": await f.Manager.DeleteJob(f.User, f.Job); break;
            case "space": await f.Manager.DeleteSpace(f.User, f.Job.Space); break;
            default: await f.Manager.DeleteProject(f.Job.Space.Project); break;
        }
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public async Task ExportJobsPublishCapturedFilesAndStandardResources()
    {
        JobRegistry.EnsurePopulated();
        string root = Path.Combine(Path.GetTempPath(), "warp-export-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string star = Path.Combine(root, "captured.star"), mini = Path.Combine(root, "captured.json");
        File.WriteAllText(star, "data_particles\n\nloop_\n_rlnImageName #1\n000001@/data/particles.mrcs\n");
        File.WriteAllText(mini, "[{\"Path\":\"movie.eer\",\"Resolution\":3.1}]");
        var manifest = new WarpExportManifest(star, true, 1, 1, new(), root, DateTime.UtcNow, mini, true, root, root);
        var job = new WarpParticlesExportJob { Id = 3, Space = new Space { RootDirectory = root }, Manifest = manifest, SourceWarpJobId = 1 };
        var operations = Operations(job);
        try
        {
            await operations.PrepareAsync(Attempt(), CancellationToken.None);
            job.RunLocal(CancellationToken.None);
            var particles = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResourceDescription());
            Assert.Equal(File.ReadAllText(star), File.ReadAllText(particles.ParticlesSingleStarPath));
            Assert.True(particles.HasData);
            Assert.True(particles.HasCtf);
            Assert.Equal(1L, particles.ItemCount);
            Assert.Equal(File.ReadAllText(mini), File.ReadAllText(particles.PickedInMicrographs.ProcessedItemsJson));
            Assert.Empty(job.PortsIn); // The Warp session need not finish for this captured export to run.
            Assert.False(job.IsResumable);
            var restored = new WarpParticlesExportJob(); restored.ReadFromJson(job.ToJson());
            Assert.Equal(manifest, restored.Manifest);
            Assert.Equal(1, restored.SourceWarpJobId);
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); Directory.Delete(root, true); }
    }

    private sealed class InvalidSlotJob : Note { public override int LocalQueueSlotCount => -1; }

    private static RelayExecutionOperations Operations(Job job) => new(_ => job, (j, action) => { action(j); return Task.CompletedTask; });
    private static readonly Guid AttemptId = Guid.NewGuid();
    private static ExecutionAttemptSnapshot Attempt()
    {
        var c = new ExecutionCoordinator([new ExecutionQueuePolicy(1, ExecutionBackendKind.Local)]);
        return c.RequestRun(new JobAddress(1, 2, 3), 1, ResourceVector.None, true).CreateSnapshot() with { Id = AttemptId };
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "warp-facade-" + Guid.NewGuid());
        public DataManager Manager { get; private set; } = null!;
        public ReadOnlyUser User { get; private set; } = null!;
        public ReadOnlyWarpJob Job { get; private set; } = null!;
        public ReadOnlyJobQueue Queue { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            JobRegistry.EnsurePopulated(); var f = new Fixture(); Directory.CreateDirectory(f.Root);
            f.Manager = new(new RelayConfiguration { UsersPath = Path.Combine(f.Root,"users.json"), ProjectsPath = Path.Combine(f.Root,"projects.json"), QueuesPath = Path.Combine(f.Root,"queues.json") });
            var queues = Field<QueueRepository>(f.Manager, "_queueRepository");
            Field<CancellationTokenSource>(queues, "_daemonCancellation").Cancel();
            try { await Field<Task>(queues, "_daemonTask"); } catch (OperationCanceledException) { }
            f.User = await f.Manager.CreateUser(new User { Name = "Owner" });
            var project = await f.Manager.CreateProject(f.User);
            var space = await f.Manager.CreateSpace(f.User, project, new Space { RootDirectory = Path.Combine(f.Root,"space") });
            var view = await f.Manager.CreateView(f.User, space);
            f.Job = (ReadOnlyWarpJob)await f.Manager.CreateJob(f.User, view, WarpJob.GuidString);
            f.Queue = await f.Manager.CreateClusterQueue(new ClusterQueue { Alias="Test GPU", QueueType=JobQueueType.GPU, SchedulerType=ClusterScheduler.Slurm, SubmissionScriptTemplate="{{ command }}", SubmitJobTemplate="exit 99", StatusJobTemplate="exit 99", AbortJobTemplate="exit 99" });
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            await Manager.ShutdownAsync(); Manager.WarpSessions.Dispose();
            Field<DataRepository>(Manager,"_dataRepository").Dispose(); Field<UserRepository>(Manager,"_userRepository").Dispose();
            Directory.Delete(Root,true);
        }
    }
}
