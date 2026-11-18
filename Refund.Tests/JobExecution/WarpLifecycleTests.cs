using System.Text.Json;
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
    public async Task ClearingKeepsSettingsAndFilterThresholdsButNotResults()
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root, Pixel = 2, TargetPixelSize = 2, ParticleBounds = new(.3, .9, 0, null) });
        await f.Manager.UpdateWarpSession(f.User, f.Job, s => { s.FilterMetric("ctf-max", 3.5); s.FilterMetric("ctf-signal-min", 7); s.FilterMetric("astigmatism", 2); });
        var session = f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings);
        Field<List<WarpMovie>>(session, "_movies").Add(new WarpMovie { Index = 0, Id = "movie", Name = "movie.mrc", InputPath = Path.Combine(f.Root, "movie.mrc") });
        session.SetDisposition([0], WarpDisposition.Exclude);
        await f.Manager.ClearJob(f.User, f.Job);
        var cleared = f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings);
        Assert.NotSame(session, cleared);
        Assert.Empty(cleared.Movies);
        Assert.Equal((3.5, 7.0, 2.0), (cleared.MaxCtf, cleared.MinCtfSignal, cleared.MaxAstigmatismSigma));
        Assert.Equal((2m, f.Root), (cleared.Settings.Pixel, cleared.Settings.InputPath));
        Assert.Equal(new WarpParticleBounds(.3, .9, 0, null), cleared.Settings.ParticleBounds);
        // A second clear keeps them too, now from the checkpoint the cleared session saved.
        await f.Manager.ClearJob(f.User, f.Job);
        Assert.Equal(3.5, f.Manager.WarpSessions.GetOrCreate(f.Job.Identity, f.Job.Settings).MaxCtf);
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
    public async Task ExportWithNothingToExportCreatesNoJob()
    {
        await using var f = await Fixture.Create();
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Manager.ExportWarpJob(f.User, f.Job, true));
        Assert.Equal(WarpSession.NothingToExport(true), error.Message);
        Assert.DoesNotContain(f.Job.Space.Jobs, j => j is ReadOnlyWarpExportJob);
        Assert.False(f.Manager.WarpSessions.GetOrCreate(f.Job.Identity).Exporting);
        await f.Manager.ClearJob(f.User, f.Job);
    }

    [Fact]
    public async Task ExportJobWritesTheSessionAsItIsWhenItRuns()
    {
        await using var f = await Fixture.Create(execute: true);
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        var session = f.Manager.WarpSessions.GetOrCreate(f.Job.Identity);
        var first = AddExportableMovie(f, session, 0);
        var exported = await f.Manager.ExportWarpJob(f.User, f.Job, false);
        var job = (WarpExportJob)await UntilSettled(f, exported);
        Assert.True(job.Status == JobStatus.Finished, Log(job));
        Assert.Equal(1, job.Manifest.MovieCount);
        // The job saves what it wrote relative to the space, and its STAR names files the same way.
        Assert.False(Path.IsPathRooted(job.Manifest.StarPath));
        var written = job.Manifest.ResolvedIn(job.Space.RootDirectory);
        Assert.Equal(job.StarPath, written.StarPath);
        Assert.Equal(session.Settings.OutputPath, written.ProcessingDirectory);
        Assert.Equal(job.Space.GetRelativePath(first.AveragePath), Assert.Single(new Star(job.StarPath, "micrographs").GetColumn("rlnMicrographName")));
        Assert.Equal(job.Space.GetRelativePath(Path.Combine(session.Settings.OutputPath, "input", Path.GetFileName(first.ProcessingPath))), Assert.Single(File.ReadAllLines(written.InputListPath)));
        Assert.Equal(1, new Star(job.StarPath, "micrographs").RowCount);
        Assert.Contains("Exported from Warp session J" + f.Job.Id, File.ReadAllText(job.LifecycleFilePath));
        Assert.Empty(session.Exports); // The session no longer compiles exports itself.
        DateTime firstRun = job.Manifest.Created;

        // The session moves on; clearing the export removes its links, never what they point to, and a new run
        // writes the selection as it is then.
        var second = AddExportableMovie(f, session, 1);
        await f.Manager.ClearJob(f.User, job.AsReadOnly());
        Assert.Null(job.Manifest);
        Assert.False(File.Exists(job.StarPath));
        Assert.True(File.Exists(first.AveragePath)); Assert.True(File.Exists(first.InputPath));
        await f.Manager.QueueLocalJob(f.User, job.AsReadOnly());
        job = (WarpExportJob)await UntilSettled(f, exported);
        Assert.Equal(JobStatus.Finished, job.Status);
        Assert.Equal(2, job.Manifest.MovieCount);
        Assert.Equal(2, new Star(job.StarPath, "micrographs").RowCount);
        Assert.True(job.Manifest.Created > firstRun);
        Assert.True(File.Exists(second.AveragePath));
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("job")]
    [InlineData("space")]
    [InlineData("project")]
    public async Task DestructiveActionsWaitForPendingExports(string operation)
    {
        await using var f = await Fixture.Create(execute: true);
        await f.Manager.ApplyWarpSettings(f.User, f.Job, new() { InputPath = f.Root });
        AddExportableMovie(f, f.Manager.WarpSessions.GetOrCreate(f.Job.Identity), 0);
        var job = await UntilSettled(f, await f.Manager.ExportWarpJob(f.User, f.Job, false));
        Assert.True(job.Status == JobStatus.Finished, Log(job));
        job.Status = JobStatus.Waiting; // As if queued again: it would read the session's files.
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
        finally { job.Status = JobStatus.Finished; }
        await f.Manager.ClearJob(f.User, f.Job);
    }

    private static string Log(Job job) => string.Join("\n", new[] { job.ErrorFilePath, job.LifecycleFilePath }.Where(File.Exists).Select(File.ReadAllText));

    private static WarpMovie AddExportableMovie(Fixture f, WarpSession session, int index)
    {
        string name = $"source{index}.mrc", input = Path.Combine(f.Root, name);
        File.WriteAllText(input, "input");
        string processing = Path.Combine(session.Settings.OutputPath, name); Directory.CreateDirectory(session.Settings.OutputPath);
        var metadata = new Movie(processing, f.Root, []) { OptionsCTF = new() { Dimensions = new float3(128, 128, 4) }, CTFResolutionEstimate = 3, GridCTFDefocus = new(new int3(1), [1.5f]) };
        metadata.SaveMeta(); Directory.CreateDirectory(metadata.AverageDir); File.WriteAllText(metadata.AveragePath, "average");
        var row = new WarpMovie { Index = index, Id = $"source{index}", Name = name, InputPath = input, ProcessingPath = processing, MetadataPath = metadata.XMLPath, AveragePath = metadata.AveragePath,
            Ctf = 3, Motion = 1, Done = WarpStage.Motion | WarpStage.Ctf, Completed = new(Field<Dictionary<string, ulong>>(session, "_stageKeys")) };
        Field<List<WarpMovie>>(session, "_movies").Add(row); session.SetDisposition([index], WarpDisposition.Auto);
        return row;
    }

    private static async Task<Job> UntilSettled(Fixture f, ReadOnlyJob job)
    {
        var repository = Field<DataRepository>(f.Manager, "_dataRepository");
        for (int i = 0; i < 2000; i++)
        {
            var current = repository.FindJob(job.Space.Project.Id, job.Space.Id, job.Id);
            if (current.Status is JobStatus.Finished or JobStatus.Failed or JobStatus.Aborted or JobStatus.Interrupted
                && !Field<QueueRepository>(f.Manager, "_queueRepository").HasExecutionAttempt(current)) return current;
            await Task.Delay(10);
        }
        throw new TimeoutException("The export job did not finish within twenty seconds.");
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
    public void ExportJobsPublishWhatTheirRunWroteAsStandardResources()
    {
        JobRegistry.EnsurePopulated();
        string root = Path.Combine(Path.GetTempPath(), "warp-export-" + Guid.NewGuid());
        var job = new WarpParticlesExportJob { Id = 3, Space = new Space { RootDirectory = root }, SourceWarpJobId = 1 };
        Directory.CreateDirectory(job.DirectoryPath);
        try
        {
            Assert.Empty(job.PortsIn); // No input edge: the Warp session need not finish for its export to run.
            Assert.False(job.IsResumable);
            Assert.Null(job.PortsOut["Particles"].GetResourceDescription()); // Nothing until it has run.
            Assert.Throws<InvalidOperationException>(() => job.RunLocal(CancellationToken.None));
            File.WriteAllText(job.StarPath, "data_particles\n\nloop_\n_rlnImageName #1\n000001@/data/particles.mrcs\n");
            File.WriteAllText(job.ProcessedItemsJson, "[{\"Path\":\"movie.eer\",\"Resolution\":3.1}]");
            string list = Path.Combine(root, "movies.txt");
            var manifest = new WarpExportManifest(job.StarPath, true, 1, 1, new(), root, DateTime.UtcNow, job.ProcessedItemsJson, true, root, Path.Combine(root, "input"),
                new(), list, new(false, 1, 0));
            job.Summarize(manifest);
            job.Manifest = manifest.RelativeTo(root);
            Assert.Equal(Path.GetRelativePath(root, job.ProcessedItemsJson), job.Manifest.ProcessedItemsJson);
            Assert.Equal("input", job.Manifest.DataDirectory);
            var particles = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResourceDescription());
            Assert.Equal(job.StarPath, particles.ParticlesSingleStarPath);
            Assert.True(particles.HasData); Assert.True(particles.HasCtf);
            Assert.Equal(1L, particles.ItemCount);
            // Its outputs resolve what it saved against the space, wherever the space is now.
            Assert.Equal(job.ProcessedItemsJson, particles.PickedInMicrographs.ProcessedItemsJson);
            Assert.Equal(Path.Combine(root, "input"), particles.PickedInMicrographs.DataSetFs.DataDirectory);
            var restored = new WarpParticlesExportJob(); restored.ReadFromJson(job.ToJson());
            Assert.Equal(job.Manifest, restored.Manifest);
            Assert.Equal(1, restored.SourceWarpJobId);
            // Without positions or a readable average the card has numbers and no samples; the log still describes the export.
            var card = JsonSerializer.Deserialize<WarpExportCard>(File.ReadAllText(job.CardPath))!;
            Assert.Empty(card.Samples); Assert.Equal(3.1, card.MedianResolution);
            Assert.Contains("Exported from Warp session J1", File.ReadAllText(job.LifecycleFilePath));
            // Clearing forgets what the run wrote, so a cleared export shows nothing stale.
            job.ClearProperties();
            Assert.Null(job.Manifest);
        }
        finally { Directory.Delete(root, true); }
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
        /// <param name="execute">Keep the execution daemon, so queued local jobs (exports) run.</param>
        public static async Task<Fixture> Create(bool execute = false)
        {
            JobRegistry.EnsurePopulated(); var f = new Fixture(); Directory.CreateDirectory(f.Root);
            f.Manager = new(new RelayConfiguration { UsersPath = Path.Combine(f.Root,"users.json"), ProjectsPath = Path.Combine(f.Root,"projects.json"), QueuesPath = Path.Combine(f.Root,"queues.json") });
            var queues = Field<QueueRepository>(f.Manager, "_queueRepository");
            if (!execute)
            {
                Field<CancellationTokenSource>(queues, "_daemonCancellation").Cancel();
                try { await Field<Task>(queues, "_daemonTask"); } catch (OperationCanceledException) { }
            }
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
