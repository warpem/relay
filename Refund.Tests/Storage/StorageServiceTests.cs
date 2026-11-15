using System.Reflection;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Notes.Note;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.Classes3D.Class3DSelect;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;
using Refund.Services.Storage;

namespace Refund.Tests.Storage;

[Collection("JobRegistry")]
public sealed class StorageServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-storage-svc-" + Guid.NewGuid().ToString("N"));
    private DataManager _manager = null!;
    private StorageService _service = null!;
    private ReadOnlyUser _user = null!, _admin = null!, _viewer = null!;
    private ReadOnlySpace _space = null!;
    private ReadOnlyView _view = null!;
    private DateTime _now = DateTime.UtcNow;

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private DataRepository Repository => Field<DataRepository>(_manager, "_dataRepository");

    public async Task InitializeAsync()
    {
        JobRegistry.EnsurePopulated();
        Directory.CreateDirectory(_root);
        _manager = new(new RelayConfiguration
        {
            UsersPath = Path.Combine(_root, "users.json"), ProjectsPath = Path.Combine(_root, "projects.json"), QueuesPath = Path.Combine(_root, "queues.json"),
        });
        var queues = Field<QueueRepository>(_manager, "_queueRepository");
        Field<CancellationTokenSource>(queues, "_daemonCancellation").Cancel();
        try { await Field<Task>(queues, "_daemonTask"); } catch (OperationCanceledException) { }

        _user = await _manager.CreateUser(new User { Name = "Member", Username = "member" });
        _admin = await _manager.CreateUser(new User { Name = "Admin", Username = "admin", Role = UserRole.Admin });
        _viewer = await _manager.CreateUser(new User { Name = "Viewer", Username = "viewer", Role = UserRole.Viewer });
        var project = await _manager.CreateProject(_user);
        _space = await _manager.CreateSpace(_user, project, new Space { RootDirectory = Path.Combine(_root, "space") });
        _view = await _manager.CreateView(_user, _space);
        _service = new StorageService(_manager, new StorageOptions { MaxEntriesPerSecond = 1_000_000 }) { UtcNow = () => _now };
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        await _manager.ShutdownAsync();
        Repository.Dispose();
        Field<UserRepository>(_manager, "_userRepository").Dispose();
        Directory.Delete(_root, true);
    }

    private async Task<ReadOnlyJob> AddJob(Job template = null) =>
        await _manager.CreateJob(_user, _view, (template ?? new Note()).TypeGuid, template ?? new Note());

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private void MeasureAll()
    {
        while (_service.MeasureNext()) { }
    }

    private ReadOnlyJob Current(ReadOnlyJob job) => _manager.Projects.Single().Spaces.Single().Jobs.Single(j => j.Id == job.Id);

    [Fact]
    public async Task MeasuresJobsQuietlyAndRemembersTheMeasurementInTheSpaceFile()
    {
        var job = await AddJob();
        Write(Path.Combine(job.DirectoryPath, "a.mrc"), 10_000);
        Write(Path.Combine(job.DirectoryPath, "sub", "b.star"), 100);
        var updatedBefore = Current(job).UpdateDate;
        int jobUpdates = 0, storageUpdates = 0;
        using var _1 = new Subscriptions(_manager.JobUpdated.Add(GroupName.Job(null, null, null), _ => { Interlocked.Increment(ref jobUpdates); return Task.CompletedTask; }));
        using var _2 = new Subscriptions(_manager.StorageUpdated.Add(GroupName.Space(null, null), _ => { Interlocked.Increment(ref storageUpdates); return Task.CompletedTask; }));
        await SettleSaves();

        _service.Start(0);
        MeasureAll();

        var storage = Current(job).Storage;
        Assert.NotNull(storage);
        Assert.Equal(2, storage!.Total.Files);
        Assert.Equal(1, storage.Total.Folders);
        Assert.Equal(10_100, storage.Total.Bytes);
        Assert.Equal(updatedBefore, Current(job).UpdateDate);
        await WaitUntil(() => Volatile.Read(ref storageUpdates) > 0);
        Assert.Equal(0, Volatile.Read(ref jobUpdates));

        // Saved with the space, but only once the deferred save is due (or flushed).
        string spaceFile = Path.Combine(_space.RootDirectory, "space.relay");
        Assert.DoesNotContain("\"Ms\"", File.ReadAllText(spaceFile));
        _manager.FlushChanges();
        Assert.Contains("\"Ms\"", File.ReadAllText(spaceFile));
    }

    [Fact]
    public async Task ARegularSaveCarriesPendingMeasurementsAlong()
    {
        var job = await AddJob();
        Repository.DeferredSaveDelay = TimeSpan.FromHours(1);
        await SettleSaves();
        _service.Start(0);
        MeasureAll();
        string spaceFile = Path.Combine(_space.RootDirectory, "space.relay");
        await Task.Delay(1200);
        Assert.DoesNotContain("\"Ms\"", File.ReadAllText(spaceFile));
        Assert.Equal(1, Repository.DeferredSpaceCount);

        await _manager.UpdateJob(_user, job, j => j.Alias = "renamed");
        await WaitUntil(() => File.ReadAllText(spaceFile).Contains("\"Ms\""));
        Assert.Equal(0, Repository.DeferredSpaceCount);
    }

    [Fact]
    public async Task StatusChangesTriggerANewMeasurementAfterTheJobSettles()
    {
        var job = await AddJob();
        _service.Start(0);
        MeasureAll();
        Assert.False(Current(job).Storage!.Exists);

        Write(Path.Combine(job.DirectoryPath, "result.mrc"), 5000);
        await _manager.UpdateJob(_user, job, j => j.Status = JobStatus.Finished);
        await WaitUntil(() => _service.Scheduler.Pending.ContainsKey(StorageClass.Changed));
        Assert.False(_service.MeasureNext());
        _now += StorageScheduler.ChangeDelay;
        Assert.True(_service.MeasureNext());

        var storage = Current(job).Storage!;
        Assert.True(storage.Exists);
        Assert.Equal(JobStatus.Finished, storage.Status);
        Assert.Equal(1, storage.Total.Files);
    }

    [Fact]
    public async Task DeletingAJobTakesItsMeasurementAndItsIdStartsFresh()
    {
        await AddJob();
        var last = await AddJob();
        Write(Path.Combine(last.DirectoryPath, "x.bin"), 100);
        _service.Start(0);
        MeasureAll();
        Assert.NotNull(Current(last).Storage);

        await _manager.DeleteJob(_user, last);
        var again = await AddJob();
        Assert.Equal(last.Id, again.Id);
        Assert.Null(Current(again).Storage);
    }

    [Fact]
    public async Task ReconnectingASpaceKeepsItsMeasurements()
    {
        var job = await AddJob();
        Write(Path.Combine(job.DirectoryPath, "x.bin"), 100);
        _service.Start(0);
        MeasureAll();
        _manager.FlushChanges();

        var project = _manager.Projects.Single();
        await _manager.DeleteSpace(_user, _space);
        var reconnected = await _manager.ReconnectSpace(_user, project, Path.Combine(_space.RootDirectory, "space.relay"));
        Assert.Equal(1, reconnected.Jobs.Single().Storage!.Total.Files);
    }

    [Fact]
    public async Task FindsLeftoverFoldersThatOnlyAdminsCanDelete()
    {
        await AddJob();
        Write(Path.Combine(_space.RootDirectory, "99", "old.mrc"), 2000);
        Write(Path.Combine(_space.RootDirectory, "notes", "mine.txt"), 10);
        _service.Start(0);
        MeasureAll();

        var leftovers = _manager.Projects.Single().Spaces.Single().Storage!.Leftovers;
        Assert.Equal(["99", "notes"], leftovers.Select(l => l.Name));
        Assert.Equal(99, leftovers[0].JobId);
        Assert.Equal(1, leftovers[0].Total.Files);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _manager.DeleteLeftover(_user, _space, "99"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.DeleteLeftover(_admin, _space, "notes"));
        await _manager.DeleteLeftover(_admin, _space, "99");
        Assert.False(Directory.Exists(Path.Combine(_space.RootDirectory, "99")));
        Assert.True(Directory.Exists(Path.Combine(_space.RootDirectory, "notes")));
        Assert.DoesNotContain(_manager.Projects.Single().Spaces.Single().Storage!.Leftovers, l => l.Name == "99");
        // A job's own folder is never a leftover.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.DeleteLeftover(_admin, _space, "1"));
    }

    [Fact]
    public async Task ViewersCanNotClearJobs()
    {
        var job = await AddJob();
        await _manager.UpdateJob(_user, job, j => j.Status = JobStatus.Finished);
        var result = await _manager.ClearJobs(_viewer, [Current(job)]);
        Assert.Empty(result.Cleared);
        Assert.Equal("Viewers can't clear jobs", result.Skipped.Single().Reason);
        result = await _manager.ClearJobs(_user, [Current(job)]);
        Assert.Single(result.Cleared);
    }

    [Fact]
    public async Task CleaningKeepsTheFinalIterationAndWhatChildrenUse()
    {
        var job = await AddJob(new Class3D());
        for (int it = 0; it <= 5; it++)
        {
            Write(Path.Combine(job.DirectoryPath, $"run_it{it:D3}_data.star"), 1000);
            Write(Path.Combine(job.DirectoryPath, $"run_it{it:D3}_class001.mrc"), 4000);
        }
        Write(Path.Combine(job.DirectoryPath, "run_class001.mrc"), 4000);
        Write(Path.Combine(job.DirectoryPath, "pool", "w1", "log.txt"), 10);
        Write(Path.Combine(job.DirectoryPath, ".relay", "card_0003.png"), 10);
        var select = await AddJob(new Class3DSelect { SelectedIteration = 2 });
        await _manager.CreateEdge(_space, Current(job).PortsOut[Class3D.PortOutMaps], Current(select).PortsIn[Class3DSelect.PortInMaps]);

        Assert.Equal("Only finished jobs can be cleaned", _manager.CleanBlocker(_user, Current(job)));
        await _manager.UpdateJob(_user, job, j => { j.Status = JobStatus.Finished; j.VisAvailableIteration = 5; j.LogsAvailableIteration = 5; });
        _service.Start(0);
        MeasureAll();
        Assert.Equal(4 * 2 + 3, DataManager.Cleanable(Current(job)).Entries); // iterations 0, 1, 3, 4 and the pool
        Assert.Equal("Viewers can't clean jobs", _manager.CleanBlocker(_viewer, Current(job)));
        Assert.Null(_manager.CleanBlocker(_user, Current(job)));

        var result = await _manager.CleanIntermediate(_user, Current(job));

        var left = Directory.EnumerateFileSystemEntries(job.DirectoryPath).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal([".relay", "run_class001.mrc", "run_it002_class001.mrc", "run_it002_data.star", "run_it005_class001.mrc", "run_it005_data.star"], left);
        Assert.Equal([2, 5], result.KeptIterations);
        var cleaned = Current(job);
        Assert.True(cleaned.HasBeenCleaned);
        Assert.Equal(JobStatus.Finished, cleaned.Status);
        Assert.True(cleaned.HasResultFilesForIteration(5));
        Assert.True(cleaned.HasResultFilesForIteration(2));
        Assert.False(cleaned.HasResultFilesForIteration(3));
        Assert.False(cleaned.HasResultFilesForIteration(0));

        // The service measures it again: nothing left to clean.
        await WaitUntil(() => _service.Scheduler.Pending.ContainsKey(StorageClass.Changed));
        _now += StorageScheduler.ChangeDelay;
        MeasureAll();
        Assert.Equal([2, 5], Current(job).Storage!.Iterations.Select(i => i.Iteration));
        Assert.Equal(0, DataManager.Cleanable(Current(job)).Entries);
        Assert.Equal("Nothing to clean", _manager.CleanBlocker(_user, Current(job)));
    }

    [Fact]
    public async Task CleaningABeamRefinementRemovesEarlierIterationFoldersAndStagingLeftovers()
    {
        var job = await AddJob(new BeamRefine3D());
        foreach (var it in new[] { 1, 2, 3 })
            Write(Path.Combine(job.DirectoryPath, "beam", $"it{it:D3}", $"run_it{it:D3}_data.star"), 100);
        Write(Path.Combine(job.DirectoryPath, "beam", ".it004.staging-x", "partial"), 100);
        Write(Path.Combine(job.DirectoryPath, "beam", "run_mask.mrc"), 100);
        await _manager.UpdateJob(_user, job, j =>
        {
            j.Status = JobStatus.Finished; j.VisAvailableIteration = 3; ((BeamRefine3D)j).LastCommittedIteration = 3;
        });

        await _manager.CleanIntermediate(_user, Current(job));

        var left = Directory.EnumerateFileSystemEntries(Path.Combine(job.DirectoryPath, "beam")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["it003", "run_mask.mrc"], left);
    }

    [Fact]
    public async Task CleaningIsRefusedWhileAChildIsActive()
    {
        var job = await AddJob(new Class3D());
        var select = await AddJob(new Class3DSelect());
        await _manager.CreateEdge(_space, Current(job).PortsOut[Class3D.PortOutMaps], Current(select).PortsIn[Class3DSelect.PortInMaps]);
        await _manager.UpdateJob(_user, job, j => j.Status = JobStatus.Finished);
        await _manager.UpdateJob(_user, select, j => j.Status = JobStatus.Running);

        Assert.Equal("A job that uses its results is active", _manager.CleanBlocker(_user, Current(job)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.CleanIntermediate(_user, Current(job)));
    }

    [Fact]
    public async Task RunningJobsAreMeasuredAgainLater()
    {
        var job = await AddJob();
        await _manager.UpdateJob(_user, job, j => j.Status = JobStatus.Running);
        _service.Start(0);
        MeasureAll();
        Assert.Equal(JobStatus.Running, Current(job).Storage!.Status);

        Write(Path.Combine(job.DirectoryPath, "growing.bin"), 100);
        Assert.False(_service.MeasureNext());
        _now += TimeSpan.FromHours(1.01);
        Assert.True(_service.MeasureNext());
        Assert.Equal(1, Current(job).Storage!.Total.Files);
    }

    // Edits so far are written by the regular autosave (every 500 ms), which would carry measurements along.
    private async Task SettleSaves()
    {
        await Task.Delay(700);
        _manager.FlushChanges();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }

    private sealed class Subscriptions(GroupEventSubscription subscription) : IDisposable
    {
        public void Dispose() => subscription.Unsubscribe();
    }
}
