using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Storage;
using Refund.Utils;
using Serilog;

namespace Refund.Services.Core.DataManager;

/// <summary>What a bulk clear did with each job.</summary>
public sealed record StorageClearResult(IReadOnlyList<ReadOnlyJob> Cleared, IReadOnlyList<(ReadOnlyJob Job, string Reason)> Skipped);

/// <summary>What cleaning a job's intermediate files removed.</summary>
public sealed record StorageCleanResult(int Removed, UsagePart Estimated, int[] KeptIterations);

public partial class DataManager
{
    private readonly HashSet<(int, int, int)> _cleaning = [];

    /// <summary>
    /// Raised when measurements in a space change (coalesced by the storage service, at most about once a second per
    /// space). Valid groups are "P*_S*", "P{projectId}_S*", "P{projectId}_S{spaceId}".
    /// </summary>
    public GroupEvent<ReadOnlySpace> StorageUpdated { get; } = new();

    /// <summary>
    /// Raised when a job's files changed without a status change: intermediate files were cleaned, or files were deleted
    /// in its folder through the file browser. The storage service re-measures the job.
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobFilesChanged { get; } = new();

    /// <summary>Records a measurement of a job's folder. Returns false if the job no longer exists.</summary>
    public bool SetJobStorage(ReadOnlyJob job, JobStorage storage)
    {
        var original = _dataRepository.FindJob(job.Space.Project.Id, job.Space.Id, job.Id);
        if (original == null || !ReferenceEquals(original, job.Mutable))
            return false;
        _dataRepository.SetJobStorage(original, storage);
        return true;
    }

    /// <summary>Records a listing of a space's root. Returns false if the space is no longer connected.</summary>
    public bool SetSpaceStorage(ReadOnlySpace space, SpaceStorage storage)
    {
        var original = _dataRepository.FindSpace(space.Project.Id, space.Id);
        if (original == null || original.RootDirectory != space.RootDirectory)
            return false;
        _dataRepository.SetSpaceStorage(original, storage);
        return true;
    }

    /// <summary>Looks a job up without copying collections or logging misses (the storage service does this a lot).</summary>
    internal ReadOnlyJob FindJobQuietly(int projectId, int spaceId, int jobId) =>
        FindSpaceModel(projectId, spaceId)?.Jobs.FirstOrDefault(j => j.Id == jobId)?.AsReadOnly();

    internal ReadOnlySpace FindSpaceQuietly(int projectId, int spaceId) => FindSpaceModel(projectId, spaceId)?.AsReadOnly();

    private Space FindSpaceModel(int projectId, int spaceId) =>
        _dataRepository.Projects.FirstOrDefault(p => p.Id == projectId)?.Spaces.FirstOrDefault(s => s.Id == spaceId);

    internal Task RaiseStorageUpdated(ReadOnlySpace space) =>
        StorageUpdated.InvokeHierarchy(space, GroupName.SpaceHierarchy(space.Project.Id, space.Id));

    /// <summary>Writes every pending change, deferred measurements included.</summary>
    public void FlushChanges() => _dataRepository.FlushChanges();

    /// <summary>Whether the user may clear and clean jobs in the project: any member, owner or admin, but no viewer.</summary>
    public static bool CanModifyStorage(ReadOnlyUser user, ReadOnlyProject project) =>
        user != null && project != null && user.Role != UserRole.Viewer &&
        (user.Role == UserRole.Admin || project.Owner?.Id == user.Id || project.Members.Any(m => m.Id == user.Id));

    /// <summary>How many folders bulk clearing and cleaning delete at the same time.</summary>
    public const int CleanupParallelism = 4;
    private readonly SemaphoreSlim _cleanupSlots = new(CleanupParallelism);

    /// <summary>
    /// Clears jobs together: every job that can be cleared moves to Clearing first (then <paramref name="started"/>
    /// runs), and their folders are deleted a few at a time. Jobs that can't be cleared are skipped with the reason.
    /// </summary>
    public async Task<StorageClearResult> ClearJobs(ReadOnlyUser user, IEnumerable<ReadOnlyJob> jobs, Func<Task> started = null)
    {
        var cleared = new List<ReadOnlyJob>();
        var skipped = new List<(ReadOnlyJob, string)>();
        var clearing = new List<(ReadOnlyJob Job, Job Mutable)>();
        foreach (var job in jobs)
        {
            string reason = ClearBlocker(user, job);
            if (reason != null) { skipped.Add((job, reason)); continue; }
            try { clearing.Add((job, await StartClearing(user, job))); }
            catch (Exception e) { skipped.Add((job, e.Message)); }
        }
        if (started != null)
            await started();

        await Task.WhenAll(clearing.Select(async c =>
        {
            await _cleanupSlots.WaitAsync();
            try
            {
                await FinishClearing(user, c.Job, c.Mutable);
                lock (cleared) cleared.Add(c.Job);
            }
            catch (Exception e)
            {
                lock (skipped) skipped.Add((c.Job, e.Message));
            }
            finally
            {
                _cleanupSlots.Release();
            }
        }));
        return new(cleared, skipped);
    }

    /// <summary>Why the user can't clear the job right now, or null if they can.</summary>
    public string ClearBlocker(ReadOnlyUser user, ReadOnlyJob job)
    {
        if (!CanModifyStorage(user, job.Space?.Project))
            return user?.Role == UserRole.Viewer ? "Viewers can't clear jobs" : "Not a member of the project";
        if (job.Status == JobStatus.Waiting || job.Status.IsUnsettled())
            return "The job is active";
        if (!job.CanTransitionState(JobStatus.Clearing))
            return "Nothing to clear";
        if (IsCleaning(job))
            return "Its intermediate files are being cleaned";
        return null;
    }

    /// <summary>Why the user can't clean the job's intermediate files right now, or null if they can.</summary>
    public string CleanBlocker(ReadOnlyUser user, ReadOnlyJob job)
    {
        if (job.IntermediateRule == null)
            return "This job type has no intermediate files";
        if (!CanModifyStorage(user, job.Space?.Project))
            return user?.Role == UserRole.Viewer ? "Viewers can't clean jobs" : "Not a member of the project";
        if (job.Status == JobStatus.Waiting || job.Status.IsUnsettled())
            return "The job is active";
        if (job.Status != JobStatus.Finished)
            return "Only finished jobs can be cleaned";
        if (job.GetChildren().Any(c => c.Status == JobStatus.Waiting || c.Status.IsUnsettled()))
            return "A job that uses its results is active";
        if (IsCleaning(job))
            return "Already being cleaned";
        if (job.Storage is { } measured && Cleanable(job).Entries == 0 && measured.Status == JobStatus.Finished)
            return "Nothing to clean";
        return null;
    }

    /// <summary>What cleaning would remove according to the latest measurement (zero if not measured).</summary>
    public static UsagePart Cleanable(ReadOnlyJob job)
    {
        if (job.IntermediateRule is not { } rule || job.Storage is not { } storage)
            return default;
        var onDisk = storage.Iterations.Select(i => i.Iteration).ToArray();
        return storage.Cleanable(rule.KeepSet(job, onDisk));
    }

    public bool IsCleaning(ReadOnlyJob job)
    {
        lock (_cleaning)
            return _cleaning.Contains((job.Space.Project.Id, job.Space.Id, job.Id));
    }

    /// <summary>
    /// Removes the files of every iteration the job no longer needs — all but the final one, those Class3DSelect children
    /// picked, and what a continuation reads — plus regenerable leftovers. The job stays Finished; its other iterations
    /// keep their logs and figures.
    /// </summary>
    public async Task<StorageCleanResult> CleanIntermediate(ReadOnlyUser user, ReadOnlyJob job, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(job);
        var key = (job.Space.Project.Id, job.Space.Id, job.Id);
        Job mutable = null;
        await ExecuteWithLock(async () =>
        {
            var actor = ResolveUser(user.Id);
            mutable = ResolveJob(key.Item1, key.Item2, key.Item3);
            string reason = CleanBlocker(actor.AsReadOnly(), mutable.AsReadOnly());
            if (reason != null && reason != "Nothing to clean")
                throw new InvalidOperationException(reason);
            EnsureNoPendingExecutions([mutable], $"Job {mutable.QualifiedName}");
            lock (_cleaning)
                if (!_cleaning.Add(key))
                    throw new InvalidOperationException("Already being cleaned");
            await Task.CompletedTask;
        });

        // Marked as cleaning right away (so a batch shows as such), but only a few folders are worked on at a time.
        bool slot = false;
        try
        {
            await _cleanupSlots.WaitAsync(cancellation);
            slot = true;
            var rule = mutable.IntermediateRule;
            string folder = mutable.DirectoryPath;
            var (candidates, onDisk) = await Task.Run(() => IntermediateCleaner.Find(folder, rule), cancellation);
            var keep = rule.KeepSet(mutable, onDisk);
            var estimated = mutable.Storage?.Cleanable(keep) ?? default;
            int removed = await Task.Run(() => IntermediateCleaner.Delete(candidates, keep, cancellation), cancellation);

            // Iterations whose result files remain; the final results always do.
            var kept = keep.Where(onDisk.Contains).Select(i => rule.ToLogical(mutable, i))
                .Append(mutable.VisAvailableIteration).Where(i => i >= 0).Distinct().Order().ToArray();
            lock (_cleaning)
                _cleaning.Remove(key);
            if (removed > 0 || !mutable.HasBeenCleaned)
                await UpdateJob(user, job, j =>
                {
                    j.HasBeenCleaned = true;
                    j.KeptIterations = kept;
                    // Accurate for the iteration files until the storage service measures the folder again.
                    if (j.Storage is { } before) j.Storage = before.AfterCleaning(keep);
                });
            Log.ForContext<DataManager>().Information("Cleaned intermediate files of job {Job}: removed {Removed} entries, kept iterations {Kept}",
                mutable.QualifiedName, removed, string.Join(", ", kept));
            return new(removed, estimated, kept);
        }
        finally
        {
            if (slot)
                _cleanupSlots.Release();
            lock (_cleaning)
                _cleaning.Remove(key);
            await JobFilesChanged.InvokeHierarchy(job, GroupName.JobHierarchy(key.Item1, key.Item2, key.Item3));
        }
    }

    /// <summary>
    /// Deletes a leftover folder of a deleted job from a space's root (admins only). The folder is first renamed out of
    /// the way under the command lock, so a new job can't take its name while it is being deleted.
    /// </summary>
    public async Task DeleteLeftover(ReadOnlyUser user, ReadOnlySpace space, string folderName)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(space);
        string doomed = null;
        await ExecuteWithLock(async () =>
        {
            var actor = ResolveUser(user.Id);
            if (actor.Role != UserRole.Admin)
                throw new UnauthorizedAccessException("Only administrators can delete leftover folders.");
            var original = ResolveSpace(space.Project.Id, space.Id);
            if (!StorageLeftovers.IsDeletable(folderName) || folderName.Contains('/') || folderName.Contains('\\'))
                throw new InvalidOperationException("Only folders of deleted jobs can be deleted here.");
            if (StorageLeftovers.JobIdOf(folderName) is int id && original.Jobs.Any(j => j.Id == id))
                throw new InvalidOperationException($"Folder {folderName} belongs to job {id}.");
            string root = Path.GetFullPath(original.RootDirectory);
            string path = Path.Combine(root, folderName);
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.LinkTarget != null || Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(info.FullName)) != Path.TrimEndingDirectorySeparator(root))
                throw new InvalidOperationException($"Folder {folderName} is not a folder in the space's root.");
            doomed = folderName.StartsWith(StorageLeftovers.DeletingPrefix, StringComparison.Ordinal) ? path
                : Path.Combine(root, $"{StorageLeftovers.DeletingPrefix}{folderName}-{Guid.NewGuid():N}");
            if (doomed != path)
                Directory.Move(path, doomed);
            await Task.CompletedTask;
        });

        Log.ForContext<DataManager>().Information("Deleting leftover folder {Folder} of space {Space} for {User}", folderName, space.QualifiedName, user.Username);
        await Task.Run(() => FileUtils.DeleteDirectoryWithRetry(doomed));

        var current = _dataRepository.FindSpace(space.Project.Id, space.Id);
        if (current?.Storage is { } storage)
            _dataRepository.SetSpaceStorage(current, new SpaceStorage
            {
                ListedAt = storage.ListedAt, LooseFiles = storage.LooseFiles,
                Leftovers = storage.Leftovers.Where(l => l.Name != folderName).ToArray(),
            });
        await RaiseStorageUpdated(space);
    }

    /// <summary>
    /// Tells the storage service that files under <paramref name="path"/> changed (deleted through the file browser).
    /// The job whose folder contains the path is re-measured.
    /// </summary>
    public async Task NotifyFilesChanged(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return; }
        foreach (var project in Projects)
            foreach (var space in project.Spaces)
            {
                if (string.IsNullOrWhiteSpace(space.RootDirectory)) continue;
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(space.RootDirectory)) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
                string first = full[root.Length..].Split(Path.DirectorySeparatorChar)[0];
                if (StorageLeftovers.JobIdOf(first) is int id && space.Jobs.FirstOrDefault(j => j.Id == id) is { } job)
                    await JobFilesChanged.InvokeHierarchy(job, GroupName.JobHierarchy(project.Id, space.Id, job.Id));
                return;
            }
    }
}
