using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Storage.Walking;
using Serilog;

namespace Refund.Services.Storage;

/// <summary>One folder being measured right now.</summary>
public sealed record StorageWalk(StorageTarget Target, long Entries, TimeSpan Elapsed);

/// <summary>What the storage service is doing, for the Storage overlay and the State page.</summary>
public sealed record StorageProgress(
    bool Enabled, bool Paused, int Workers, int Jobs, int Measured, IReadOnlyDictionary<StorageClass, int> Pending,
    IReadOnlyList<StorageWalk> Walking, double EntriesPerSecond, double Rate, double Ceiling, bool SlowedDown,
    (double Usual, double Recent) Latency, TimeSpan? Remaining, bool NativeWalker);

/// <summary>Capacity of the file system holding some spaces (for GPFS filesets, the fileset's quota).</summary>
public sealed record FileSystemUsage(string Label, long TotalBytes, long FreeBytes, long TotalFiles, long FreeFiles,
    IReadOnlyList<ReadOnlySpace> Spaces)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public long UsedFiles => Math.Max(0, TotalFiles - FreeFiles);
    public bool HasFiles => TotalFiles > 0;
}

/// <summary>
/// Measures every job folder (bytes on disk, file count, biggest folders and file types, per-iteration files) in the
/// background and stores the results on the jobs, plus leftover folders on their spaces. Low-priority dedicated
/// threads share one throttle that backs off when the file system gets slow, so the measurements never compete with
/// anything that matters more.
/// </summary>
public sealed class StorageService : IHostedService, IDisposable
{
    private static readonly TimeSpan RunningRefresh = TimeSpan.FromHours(1), ListingInterval = TimeSpan.FromHours(1),
        FileSystemCache = TimeSpan.FromMinutes(1), NotifyInterval = TimeSpan.FromSeconds(1);

    private readonly DataManager _dataManager;
    private readonly StorageOptions _options;
    private readonly ILogger _log;
    private readonly StorageScheduler _scheduler = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _workers = [];
    private readonly List<GroupEventSubscription> _subscriptions = [];
    private readonly Dictionary<StorageTarget, JobStatus> _status = new();
    private readonly Dictionary<StorageTarget, (WalkState State, long Started)> _walking = new();
    private readonly HashSet<(int, int)> _dirtySpaces = [];
    private readonly object _spaceStorageGate = new();
    private readonly Dictionary<string, (DateTime At, FileSystemSample Sample)> _fileSystems = new();
    private Timer _notifyTimer, _hourlyTimer;
    private bool _started, _disposed;

    private readonly record struct FileSystemSample(ulong Id, long TotalBytes, long FreeBytes, long TotalFiles, long FreeFiles, bool Ok);

    [ThreadStatic] private static WalkState t_state;

    public StorageService(DataManager dataManager, StorageOptions options, ILogger log = null)
    {
        _dataManager = dataManager;
        _options = options ?? new StorageOptions();
        _log = log ?? Log.ForContext<StorageService>();
        Throttle = new StorageThrottle(_options.MaxEntriesPerSecond);
    }

    public StorageThrottle Throttle { get; }
    public StorageScheduler Scheduler => _scheduler;
    public bool Enabled => _options.Enabled;
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Whether measuring is paused (an admin switch on the State page).</summary>
    public bool Paused
    {
        get => Throttle.Paused;
        set
        {
            Throttle.Paused = value;
            if (!value) _signal.Set();
            _log.Information(value ? "Storage measurements paused" : "Storage measurements resumed");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
            Start(Math.Clamp(_options.Workers, 1, 8));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>Subscribes to job changes, queues every job and space, and starts <paramref name="workers"/> threads (0: none, for tests).</summary>
    public void Start(int workers)
    {
        if (_started) return;
        _started = true;
        Subscribe();
        Seed();
        _notifyTimer = new Timer(_ => RaiseNotifications(), null, NotifyInterval, NotifyInterval);
        _hourlyTimer = new Timer(_ => Hourly(), null, ListingInterval, ListingInterval);
        for (int i = 0; i < workers; i++)
        {
            var thread = new Thread(WorkerLoop) { IsBackground = true, Name = $"Storage walker {i + 1}", Priority = ThreadPriority.Lowest };
            _workers.Add(thread);
            thread.Start();
        }
        _log.Information("Storage measurements started with {Workers} workers ({Walker} walker, at most {Rate:N0} entries/s)",
            workers, LinuxFs.IsSupported ? "native" : "managed", _options.MaxEntriesPerSecond);
    }

    public void Stop()
    {
        if (!_started || _stop.IsCancellationRequested) return;
        _stop.Cancel();
        _signal.Set();
        Throttle.Paused = false;
        foreach (var thread in _workers)
            thread.Join(TimeSpan.FromSeconds(10));
        _notifyTimer?.Dispose();
        _hourlyTimer?.Dispose();
        _subscriptions.UnsubscribeAndClear();
        RaiseNotifications();
        _dataManager.FlushChanges();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _signal.Dispose();
        _stop.Dispose();
    }

    // ---- Queueing ----

    private void Subscribe()
    {
        string allJobs = GroupName.Job(null, null, null);
        _subscriptions.Add(_dataManager.JobUpdated.Add(allJobs, e => { OnJobUpdated(e.Object); return Task.CompletedTask; }));
        _subscriptions.Add(_dataManager.JobCreated.Add(GroupName.Job(null, null, null), e => { OnJobCreated(e.Object); return Task.CompletedTask; }));
        _subscriptions.Add(_dataManager.JobDeleted.Add(allJobs, e => { OnJobDeleted(e.Object); return Task.CompletedTask; }));
        _subscriptions.Add(_dataManager.JobFilesChanged.Add(allJobs, e => { Request(e.Object, StorageClass.Changed); return Task.CompletedTask; }));
        _subscriptions.Add(_dataManager.SpaceCreated.Add(GroupName.Project(null), e => { SeedSpace(e.Object); return Task.CompletedTask; }));
        _subscriptions.Add(_dataManager.SpaceDeleted.Add(GroupName.Space(null, null), e =>
        {
            _scheduler.RemoveSpace(e.Object.Project.Id, e.Object.Id);
            ForgetStatuses(t => t.ProjectId == e.Object.Project.Id && t.SpaceId == e.Object.Id);
            return Task.CompletedTask;
        }));
        _subscriptions.Add(_dataManager.ProjectDeleted.Add(GroupName.Project(null), e =>
        {
            _scheduler.RemoveProject(e.Object.Id);
            ForgetStatuses(t => t.ProjectId == e.Object.Id);
            return Task.CompletedTask;
        }));
    }

    private void ForgetStatuses(Func<StorageTarget, bool> match)
    {
        lock (_status)
            foreach (var key in _status.Keys.Where(match).ToList())
                _status.Remove(key);
    }

    /// <summary>Queues everything that isn't measured, changed since, running, or measured long ago.</summary>
    private void Seed()
    {
        var now = UtcNow();
        var catchUp = new List<(StorageTarget, string, long, DateTime)>();
        foreach (var project in _dataManager.Projects)
            foreach (var space in project.Spaces)
                SeedSpace(space, now, catchUp);
        _scheduler.SeedCatchUp(catchUp);
        _signal.Set();
    }

    private void SeedSpace(ReadOnlySpace space)
    {
        var catchUp = new List<(StorageTarget, string, long, DateTime)>();
        SeedSpace(space, UtcNow(), catchUp);
        _scheduler.SeedCatchUp(catchUp);
        _signal.Set();
    }

    private void SeedSpace(ReadOnlySpace space, DateTime now, List<(StorageTarget, string, long, DateTime)> catchUp)
    {
        var stale = now - TimeSpan.FromDays(Math.Max(1, _options.RescanDays));
        foreach (var job in space.Jobs)
        {
            if (job.Mutable.IsBlueprint) continue;
            var target = TargetOf(job);
            lock (_status) _status[target] = job.Status;
            var storage = job.Storage;
            if (storage == null)
                catchUp.Add((target, job.TypeGuid, OutputItems(job), job.GetMostRecentEvent(EventType.Created)?.Timestamp ?? job.UpdateDate));
            else
            {
                _scheduler.Learn(job.TypeGuid, storage.Total.Allocated, storage.Total.Entries);
                if (storage.Status != job.Status || storage.RunStartedAt != RunStart(job))
                    _scheduler.Enqueue(target, StorageClass.Changed, now - StorageScheduler.ChangeDelay);
                else if (IsActive(job.Status))
                    _scheduler.RefreshRunning(target, storage.MeasuredAt + RefreshAfter(storage));
                else if (storage.MeasuredAt < stale)
                    _scheduler.Enqueue(target, StorageClass.Stale, now);
            }
        }
        _scheduler.Enqueue(StorageTarget.Listing(space.Project.Id, space.Id), StorageClass.Background, now);
    }

    private void OnJobUpdated(ReadOnlyJob job)
    {
        if (job.Mutable.IsBlueprint) return;
        var target = TargetOf(job);
        JobStatus status = job.Status;
        JobStatus? previous;
        lock (_status)
        {
            previous = _status.TryGetValue(target, out var p) ? p : null;
            _status[target] = status;
        }
        if (previous == status) return;
        var now = UtcNow();
        if (IsActive(status))
        {
            if (previous is null || !IsActive(previous.Value))
                _scheduler.RefreshRunning(target, now + RunningRefresh);
        }
        else
        {
            _scheduler.StopRunning(target);
            if (status is JobStatus.Finished or JobStatus.Failed or JobStatus.Aborted or JobStatus.Interrupted or JobStatus.Building)
            {
                _scheduler.Enqueue(target, StorageClass.Changed, now);
                _signal.Set();
            }
        }
    }

    private void OnJobCreated(ReadOnlyJob job)
    {
        if (job.Mutable.IsBlueprint) return;
        var target = TargetOf(job);
        lock (_status) _status[target] = job.Status;
        _scheduler.Enqueue(target, StorageClass.CatchUp, UtcNow(), job.TypeGuid, 0, DateTime.UtcNow);
        _signal.Set();
    }

    private void OnJobDeleted(ReadOnlyJob job)
    {
        var target = TargetOf(job);
        _scheduler.Remove(target);
        lock (_status) _status.Remove(target);
        // Its folder is gone or now a leftover: list the space again soon.
        _scheduler.Enqueue(StorageTarget.Listing(target.ProjectId, target.SpaceId), StorageClass.Background, UtcNow());
    }

    /// <summary>Measures these jobs before anything else (the overlay's "Measure now").</summary>
    public void Request(IEnumerable<ReadOnlyJob> jobs)
    {
        foreach (var job in jobs)
            Request(job, StorageClass.Requested);
    }

    private void Request(ReadOnlyJob job, StorageClass cls)
    {
        if (job.Mutable.IsBlueprint) return;
        _scheduler.Enqueue(TargetOf(job), cls, cls == StorageClass.Changed ? UtcNow() - StorageScheduler.ChangeDelay + TimeSpan.FromSeconds(5) : UtcNow());
        _signal.Set();
    }

    /// <summary>Lists these spaces' roots again before other background work.</summary>
    public void RequestListing(IEnumerable<ReadOnlySpace> spaces)
    {
        foreach (var space in spaces)
            _scheduler.Enqueue(StorageTarget.Listing(space.Project.Id, space.Id), StorageClass.Requested, UtcNow());
        _signal.Set();
    }

    private void Hourly()
    {
        try
        {
            var now = UtcNow();
            var stale = now - TimeSpan.FromDays(Math.Max(1, _options.RescanDays));
            foreach (var project in _dataManager.Projects)
                foreach (var space in project.Spaces)
                {
                    _scheduler.Enqueue(StorageTarget.Listing(project.Id, space.Id), StorageClass.Background, now);
                    foreach (var job in space.Jobs)
                        if (job.Storage is { } s && s.MeasuredAt < stale && !IsActive(job.Status))
                            _scheduler.Enqueue(TargetOf(job), StorageClass.Stale, now);
                }
            _signal.Set();
        }
        catch (Exception e)
        {
            _log.Warning(e, "Couldn't queue the hourly storage work");
        }
    }

    // ---- Measuring ----

    private void WorkerLoop()
    {
        if (LinuxFs.IsSupported && !LinuxFs.LowerCurrentThreadPriority())
            _log.Warning("Couldn't lower the priority of a storage walker thread");
        var cancel = _stop.Token;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (Throttle.Paused)
                    _signal.WaitOne(TimeSpan.FromSeconds(5));
                else if (!MeasureNext(cancel, out var wait))
                    _signal.WaitOne(Clamp(wait));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.Error(e, "Storage walker failed; continuing");
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;

    /// <summary>Measures the next due target on the calling thread (tests drive the service this way).</summary>
    public bool MeasureNext() => MeasureNext(_stop.Token, out _);

    private bool MeasureNext(CancellationToken cancel, out TimeSpan wait)
    {
        if (!_scheduler.TryTake(UtcNow(), out var target, out _, out wait))
            return false;
        try
        {
            if (target.IsJob) MeasureJob(target, cancel);
            else if (target.IsListing) ListSpace(target);
            else MeasureLeftover(target, cancel);
        }
        finally
        {
            if (_scheduler.Done(target, UtcNow()))
                _signal.Set();
        }
        return true;
    }

    private void MeasureJob(StorageTarget target, CancellationToken cancel)
    {
        var job = FindJob(target);
        if (job == null || job.Mutable.IsBlueprint) return;
        string folder;
        try { folder = job.DirectoryPath; }
        catch (InvalidOperationException) { return; }

        var status = job.Status;
        var run = RunStart(job);
        var started = Stopwatch.GetTimestamp();
        var storage = Walk(target, folder, job.IntermediateRule, cancel) is { } state
            ? state.ToStorage(Truncate(UtcNow()), (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, status, run)
            : JobStorage.Missing(Truncate(UtcNow()), status, run);

        // The job ran, finished or was cleared meanwhile: its own status change has queued it again.
        if (job.Status != status || RunStart(job) != run)
            return;
        if (!_dataManager.SetJobStorage(job, storage))
            return;
        _scheduler.Learn(job.TypeGuid, storage.Total.Allocated, storage.Total.Entries);
        if (IsActive(status))
            _scheduler.RefreshRunning(target, UtcNow() + RefreshAfter(storage));
        MarkDirty(target);
    }

    /// <summary>Walks a folder; null if it doesn't exist (or isn't a folder).</summary>
    private WalkState Walk(StorageTarget target, string folder, IntermediateRule rule, CancellationToken cancel)
    {
        var state = t_state ??= new WalkState();
        state.Begin(rule, Throttle, cancel);
        lock (_walking) _walking[target] = (state, Stopwatch.GetTimestamp());
        try
        {
            return StorageWalker.Walk(folder, state) ? state : null;
        }
        finally
        {
            lock (_walking) _walking.Remove(target);
        }
    }

    /// <summary>Lists a space root: leftover folders (folders that belong to no job) and loose files.</summary>
    private void ListSpace(StorageTarget target)
    {
        var space = FindSpace(target);
        if (space == null || string.IsNullOrWhiteSpace(space.RootDirectory) || !Directory.Exists(space.RootDirectory))
            return;
        var jobIds = space.Jobs.Select(j => j.Id).ToHashSet();
        var previous = (space.Storage?.Leftovers ?? SpaceStorage.NoLeftovers).ToDictionary(l => l.Name);
        var now = UtcNow();
        var stale = now - TimeSpan.FromDays(Math.Max(1, _options.RescanDays));
        var leftovers = new List<SpaceLeftover>();
        UsageTotals loose = default;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        foreach (var info in new DirectoryInfo(space.RootDirectory).EnumerateFileSystemInfos("*", options))
        {
            if (info is DirectoryInfo && info.LinkTarget == null)
            {
                if (StorageLeftovers.JobIdOf(info.Name) is int id && jobIds.Contains(id))
                    continue;
                if (previous.TryGetValue(info.Name, out var known) && known.MeasuredAt >= stale && known.MeasuredAt >= info.LastWriteTimeUtc)
                    leftovers.Add(known);
                else
                    _scheduler.Enqueue(StorageTarget.LeftoverFolder(target.ProjectId, target.SpaceId, info.Name), StorageClass.Background, now);
            }
            else if (info is FileInfo file)
                loose += file.LinkTarget != null ? new UsageTotals(0, 0, 0, 0, 1, 0) : new UsageTotals(file.Length, file.Length, 1, 0, 0, 0);
            else
                loose += new UsageTotals(0, 0, 0, 0, 1, 0);
        }
        lock (_spaceStorageGate)
        {
            // Keep leftovers measured meanwhile by other workers; they are re-checked on the next listing.
            var current = FindSpace(target)?.Storage?.Leftovers ?? SpaceStorage.NoLeftovers;
            foreach (var l in current)
                if (!leftovers.Any(x => x.Name == l.Name) && Directory.Exists(Path.Combine(space.RootDirectory, l.Name)) &&
                    !(l.JobId is int id && jobIds.Contains(id)))
                    leftovers.Add(l);
            _dataManager.SetSpaceStorage(space, new SpaceStorage { ListedAt = Truncate(now), LooseFiles = loose, Leftovers = leftovers.OrderBy(l => l.Name).ToArray() });
        }
        MarkDirty(target);
    }

    private void MeasureLeftover(StorageTarget target, CancellationToken cancel)
    {
        var space = FindSpace(target);
        if (space == null) return;
        string folder = Path.Combine(space.RootDirectory, target.Leftover);
        if (StorageLeftovers.JobIdOf(target.Leftover) is int id && space.Jobs.Any(j => j.Id == id))
            return; // a new job took the folder
        var state = Walk(target, folder, null, cancel);
        lock (_spaceStorageGate)
        {
            var current = FindSpace(target);
            if (current == null) return;
            var storage = current.Storage ?? new SpaceStorage { ListedAt = Truncate(UtcNow()) };
            var leftovers = storage.Leftovers.Where(l => l.Name != target.Leftover).ToList();
            if (state != null)
                leftovers.Add(new(target.Leftover, StorageLeftovers.JobIdOf(target.Leftover), state.Totals, state.NewestWrite, Truncate(UtcNow())));
            _dataManager.SetSpaceStorage(current, new SpaceStorage
            {
                ListedAt = storage.ListedAt, LooseFiles = storage.LooseFiles, Leftovers = leftovers.OrderBy(l => l.Name).ToArray(),
            });
        }
        MarkDirty(target);
    }

    // ---- Notifications ----

    private void MarkDirty(StorageTarget target)
    {
        lock (_dirtySpaces) _dirtySpaces.Add((target.ProjectId, target.SpaceId));
    }

    private void RaiseNotifications()
    {
        (int, int)[] dirty;
        lock (_dirtySpaces)
        {
            if (_dirtySpaces.Count == 0) return;
            dirty = _dirtySpaces.ToArray();
            _dirtySpaces.Clear();
        }
        foreach (var (p, s) in dirty)
            if (FindSpace(new(p, s, 0)) is { } space)
                _ = _dataManager.RaiseStorageUpdated(space);
    }

    // ---- Progress and capacity ----

    private (long At, int Jobs, int Measured) _counts;

    public StorageProgress GetProgress()
    {
        var (jobs, measured) = CountJobs();
        List<StorageWalk> walking;
        lock (_walking)
            walking = _walking.Select(w => new StorageWalk(w.Key, w.Value.State.Visited, Stopwatch.GetElapsedTime(w.Value.Started))).ToList();
        double rate = Throttle.EntriesPerSecond;
        double remaining = _scheduler.EstimatedEntries();
        return new(_options.Enabled && _started, Throttle.Paused, _workers.Count, jobs, measured, _scheduler.Pending, walking, rate,
            Throttle.Rate, Throttle.Ceiling, Throttle.SlowedDown, Throttle.Latency,
            rate > 1 && remaining > 0 ? TimeSpan.FromSeconds(remaining / rate) : null, LinuxFs.IsSupported && StorageWalker.UseNative);
    }

    // Counting walks every job, so it is done at most once a second however many pages ask.
    private (int Jobs, int Measured) CountJobs()
    {
        long now = Stopwatch.GetTimestamp();
        var counts = _counts;
        if (counts.At != 0 && Stopwatch.GetElapsedTime(counts.At, now) < TimeSpan.FromSeconds(1))
            return (counts.Jobs, counts.Measured);
        int jobs = 0, measured = 0;
        foreach (var project in _dataManager.Projects)
            foreach (var space in project.Spaces)
                foreach (var job in space.Jobs)
                {
                    if (job.Mutable.IsBlueprint) continue;
                    jobs++;
                    if (job.Storage != null) measured++;
                }
        _counts = (now, jobs, measured);
        return (jobs, measured);
    }

    /// <summary>
    /// The file systems holding these spaces, with total and free bytes and files. On GPFS with per-fileset df this is
    /// the fileset's quota. Sampled at most once a minute per space root.
    /// </summary>
    public IReadOnlyList<FileSystemUsage> GetFileSystems(IEnumerable<ReadOnlySpace> spaces)
    {
        var groups = new Dictionary<FileSystemSample, List<ReadOnlySpace>>();
        foreach (var space in spaces)
        {
            if (string.IsNullOrWhiteSpace(space.RootDirectory)) continue;
            var sample = Sample(space.RootDirectory);
            if (!sample.Ok) continue;
            if (!groups.TryGetValue(sample, out var list)) groups[sample] = list = [];
            list.Add(space);
        }
        return groups.Select(g => new FileSystemUsage(CommonFolder(g.Value.Select(s => s.RootDirectory)), g.Key.TotalBytes, g.Key.FreeBytes,
            g.Key.TotalFiles, g.Key.FreeFiles, g.Value)).OrderByDescending(f => f.Spaces.Count).ToList();
    }

    private FileSystemSample Sample(string root)
    {
        var now = UtcNow();
        lock (_fileSystems)
            if (_fileSystems.TryGetValue(root, out var cached) && now - cached.At < FileSystemCache)
                return cached.Sample;
        var sample = SampleNow(root);
        lock (_fileSystems)
            _fileSystems[root] = (now, sample);
        return sample;
    }

    private static unsafe FileSystemSample SampleNow(string root)
    {
        try
        {
            if (LinuxFs.IsSupported)
            {
                LinuxFs.StatVfs v;
                if (LinuxFs.StatVfsOf(root, &v) != 0) return default;
                long unit = (long)(v.FragmentSize > 0 ? v.FragmentSize : v.BlockSize);
                return new(v.FsId, (long)v.Blocks * unit, (long)v.BlocksAvailable * unit, (long)v.Files, (long)v.FilesAvailable, true);
            }
            var drive = new DriveInfo(root);
            return new((ulong)drive.Name.GetHashCode(), drive.TotalSize, drive.AvailableFreeSpace, 0, 0, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }

    private static string CommonFolder(IEnumerable<string> paths)
    {
        string[] common = null;
        foreach (var path in paths)
        {
            var parts = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
            if (common == null) { common = parts; continue; }
            int n = 0;
            while (n < common.Length && n < parts.Length && common[n] == parts[n]) n++;
            common = common[..n];
        }
        if (common == null || common.Length == 0) return "";
        string joined = string.Join(Path.DirectorySeparatorChar, common);
        return joined.Length == 0 ? Path.DirectorySeparatorChar.ToString() : joined;
    }

    // ---- Helpers ----

    private static StorageTarget TargetOf(ReadOnlyJob job) => StorageTarget.Job(job.Space.Project.Id, job.Space.Id, job.Id);

    private ReadOnlyJob FindJob(StorageTarget t) => _dataManager.FindJobQuietly(t.ProjectId, t.SpaceId, t.JobId);

    private ReadOnlySpace FindSpace(StorageTarget t) => _dataManager.FindSpaceQuietly(t.ProjectId, t.SpaceId);

    private static bool IsActive(JobStatus status) => status is JobStatus.Staging or JobStatus.Running or JobStatus.Finalizing or JobStatus.Aborting;

    /// <summary>A running job is measured again after an hour, or ten times as long as its last walk took.</summary>
    private static TimeSpan RefreshAfter(JobStorage storage)
    {
        var tenWalks = TimeSpan.FromMilliseconds(storage.DurationMs * 10.0);
        return tenWalks > RunningRefresh ? tenWalks : RunningRefresh;
    }

    /// <summary>Start of the job's latest run (UTC, whole seconds), which tells measurements of different runs apart.</summary>
    internal static DateTime? RunStart(ReadOnlyJob job) =>
        job.GetMostRecentEvent(EventType.RunningStarted)?.Timestamp is { } t ? Truncate(t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime()) : null;

    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static long OutputItems(ReadOnlyJob job)
    {
        long sum = 0;
        foreach (var count in job.Mutable.OutputItemCounts.Values) sum += count;
        return sum;
    }
}

/// <summary>Picks the walker for this platform.</summary>
public static class StorageWalker
{
    /// <summary>Whether the native Linux walker is used (when supported); tests switch it off to compare walkers.</summary>
    public static bool UseNative { get; set; } = true;

    internal static bool Walk(string folder, WalkState state) =>
        UseNative && LinuxFs.IsSupported ? LinuxWalker.Walk(folder, state) : ManagedWalker.Walk(folder, state);
}
