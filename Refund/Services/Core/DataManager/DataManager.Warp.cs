using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Fs.Warp;
using Refund.Services.Warp;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    /// <summary>How long a Warp session nothing uses stays loaded; restoring one takes well under a second.</summary>
    public static readonly TimeSpan WarpSessionIdleUnload = TimeSpan.FromMinutes(10);

    // Execution and every browser circuit share this instance. It never captures a UI user.
    public WarpSessionManager WarpSessions { get; } = new(Log.ForContext<WarpSessionManager>(), WarpSessionIdleUnload);

    public IReadOnlyList<ReadOnlyJobQueue> GetWarpWorkerQueues() => _queueRepository.ClusterQueues
        .OfType<Refund.JobQueues.ClusterQueue>()
        .Where(queue => !queue.IsManaged && queue.QueueType.HasFlag(JobQueueType.GPU))
        .Select(queue => queue.AsReadOnly()).ToArray();

    private Task RunLocalJobAsync(Job job, CancellationToken token)
    {
        if (job is WarpJob warpJob)
            return RunWarpAsync(warpJob, token);
        if (job is WarpExportJob export)
            return RunWarpExportAsync(export, token);
        ((ILocalJob)job).RunLocal(token);
        return Task.CompletedTask;
    }

    private async Task RunWarpAsync(WarpJob job, CancellationToken token)
    {
        // The lease keeps the session loaded for the whole run.
        using var lease = WarpSessions.Acquire(job.Identity, job.Settings);
        var session = lease.Session;
        using var progressCancellation = new CancellationTokenSource();
        var progress = ProjectWarpProgressAsync(job, session, progressCancellation.Token);
        try { await session.RunAsync(job.Settings, token); }
        finally
        {
            progressCancellation.Cancel();
            try { await progress; } catch (OperationCanceledException) { }
            await PublishWarpProgressAsync(job, session);
        }
    }

    private async Task ProjectWarpProgressAsync(WarpJob job, WarpSession session, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(token))
            await PublishWarpProgressAsync(job, session);
    }

    private async Task PublishWarpProgressAsync(WarpJob job, WarpSession session)
    {
        int total = session.Movies.Count, failed = session.Failed, processed = session.Complete + failed;
        if (job.NItemsTotal == total && job.NItemsFailed == failed && job.NItemsProcessed == processed) return;
        _dataRepository.UpdateJob(job.UpdatedBy, job, _ =>
        {
            job.NItemsTotal = total; job.NItemsFailed = failed; job.NItemsProcessed = processed;
        });
        await JobUpdated.InvokeHierarchy(job.AsReadOnly(), GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    private (User User, WarpJob Job) ResolveWritableWarp(ReadOnlyUser user, ReadOnlyWarpJob job)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(job);
        var actor = ResolveUser(user.Id);
        var warpJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id) as WarpJob
            ?? throw new InvalidOperationException("This job is not a Warp session.");
        var project = warpJob.Space.Project;
        if (actor.Role == UserRole.Viewer || (actor.Role != UserRole.Admin &&
            project.Owner?.Id != actor.Id && !project.Members.Any(member => member.Id == actor.Id)))
            throw new UnauthorizedAccessException("You do not have permission to modify this preprocessing session.");
        if (warpJob.Status is JobStatus.Deleted or JobStatus.Clearing)
            throw new InvalidOperationException("The session is being removed.");
        return (actor, warpJob);
    }

    private void EnsureWarpStopped(WarpJob warpJob)
    {
        if (_queueRepository.HasExecutionAttempt(warpJob) || warpJob.Status is
            JobStatus.Waiting or JobStatus.Staging or JobStatus.Running or JobStatus.Aborting or JobStatus.Finalizing)
            throw new InvalidOperationException("Stop processing and wait for its workers to finish before changing setup.");
    }

    public async Task StartWarpJob(ReadOnlyUser user, ReadOnlyWarpJob job, int poolQueueId, int poolSize)
    {
        await ExecuteWithLock(async () =>
        {
            var (actor, warpJob) = ResolveWritableWarp(user, job);
            EnsureWarpStopped(warpJob);
            if (poolSize < 1) throw new ArgumentOutOfRangeException(nameof(poolSize));
            var queue = ResolveQueue(poolQueueId);
            if (queue is not Refund.JobQueues.ClusterQueue cluster || cluster.IsManaged || !cluster.QueueType.HasFlag(JobQueueType.GPU))
                throw new InvalidOperationException("Warp requires an external scheduler queue for its GPU workers.");
            cluster.ValidateSubmissionConfiguration();
            using var lease = WarpSessions.Acquire(warpJob.Identity, warpJob.Settings);
            var session = lease.Session;
            session.Apply(warpJob.Settings with { OutputPath = warpJob.DirectoryPath });
            _dataRepository.UpdateJob(actor, warpJob, _ =>
            {
                warpJob.Settings = session.Settings;
                warpJob.UseWorkerPool = true;
                warpJob.PoolQueueId = poolQueueId;
                warpJob.NGpus = poolSize;
                // Reset execution status, never the durable preprocessing directory.
                warpJob.Status = JobStatus.Building;
                warpJob.QueueId = _queueRepository.LocalQueue.Id;
                warpJob.SubmittedByUsername = actor.Username;
            });
            EnsureJobInputsValid(warpJob);
            await _queueRepository.QueueJobAsync(warpJob, _queueRepository.LocalQueue);
        });
        await JobQueued.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public async Task StopWarpJob(ReadOnlyUser user, ReadOnlyWarpJob job)
    {
        await ExecuteWithLock(async () =>
        {
            var (_, warpJob) = ResolveWritableWarp(user, job);
            if (!_queueRepository.HasExecutionAttempt(warpJob)) return;
            await _queueRepository.CancelJobAsync(warpJob);
        });
    }

    public async Task ResizeWarpJob(ReadOnlyUser user, ReadOnlyWarpJob job, int size)
    {
        await ExecuteWithLock(async () =>
        {
            var (actor, warpJob) = ResolveWritableWarp(user, job);
            if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
            if (_queueRepository.HasExecutionAttempt(warpJob))
                await _queueRepository.ResizeWorkerGroupAsync(warpJob, size);
            else
            {
                EnsureWarpStopped(warpJob);
                _dataRepository.UpdateJob(actor, warpJob, _ => warpJob.NGpus = size);
            }
        });
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public async Task ConfigureWarpWorkers(ReadOnlyUser user, ReadOnlyWarpJob job,
        int poolQueueId, int poolSize, int perDevice, int memoryPerWorker)
    {
        await ExecuteWithLock(() =>
        {
            var (actor, warpJob) = ResolveWritableWarp(user, job);
            EnsureWarpStopped(warpJob);
            if (poolSize < 1 || perDevice < 1 || memoryPerWorker < 1)
                throw new ArgumentOutOfRangeException(nameof(poolSize), "Worker resources must be positive.");
            if (ResolveQueue(poolQueueId) is not Refund.JobQueues.ClusterQueue queue || queue.IsManaged || !queue.QueueType.HasFlag(JobQueueType.GPU))
                throw new InvalidOperationException("Select an external scheduler queue.");
            _dataRepository.UpdateJob(actor, warpJob, _ =>
            {
                warpJob.PoolQueueId = poolQueueId; warpJob.NGpus = poolSize;
                warpJob.PerDevice = perDevice; warpJob.MemoryPerWorker = memoryPerWorker;
            });
            return Task.CompletedTask;
        });
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public async Task ApplyWarpSettings(ReadOnlyUser user, ReadOnlyWarpJob job, WarpSettings settings)
    {
        await ExecuteWithLock(() =>
        {
            var (actor, warpJob) = ResolveWritableWarp(user, job);
            EnsureWarpStopped(warpJob);
            using var lease = WarpSessions.Acquire(warpJob.Identity, warpJob.Settings);
            var session = lease.Session;
            session.Apply(settings with { OutputPath = warpJob.DirectoryPath });
            _dataRepository.UpdateJob(actor, warpJob, _ => warpJob.Settings = session.Settings);
            return Task.CompletedTask;
        });
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public Task UpdateWarpSession(ReadOnlyUser user, ReadOnlyWarpJob job, Action<WarpSession> update) =>
        UpdateWarpSession(user, job, session => { update(session); return true; });

    public Task<T> UpdateWarpSession<T>(ReadOnlyUser user, ReadOnlyWarpJob job, Func<WarpSession, T> update) =>
        ExecuteWithLock(() =>
        {
            var (_, warpJob) = ResolveWritableWarp(user, job);
            using var lease = WarpSessions.Acquire(warpJob.Identity, warpJob.Settings);
            return Task.FromResult(update(lease.Session));
        });

    /// <summary>
    /// Creates and queues an export job for the session's accepted selection. The job writes the selection when it runs,
    /// so running it again (after clearing it) exports the session as it is then.
    /// </summary>
    public async Task<ReadOnlyJob> ExportWarpJob(ReadOnlyUser user, ReadOnlyWarpJob job, bool particles)
    {
        ReadOnlyJob exported = null;
        ReadOnlyView exportView = null;
        await ExecuteSpaceChange(job.Space, () =>
        {
            var (actor, warpJob) = ResolveWritableWarp(user, job);
            // Nothing to export yet is said here, rather than by a job that fails.
            using (var lease = WarpSessions.Acquire(warpJob.Identity, warpJob.Settings))
                if (lease.Session.CountExportable(particles) == 0)
                throw new InvalidOperationException(WarpSession.NothingToExport(particles));
            var view = warpJob.Space.Views.FirstOrDefault(candidate => candidate.Jobs.Any(item => item.Id == warpJob.Id))
                ?? warpJob.Space.Views.FirstOrDefault()
                ?? throw new InvalidOperationException("The session's space has no view for its export.");
            var template = particles ? (WarpExportJob)new WarpParticlesExportJob() : new WarpMicrographsExportJob();
            template.SourceWarpJobId = warpJob.Id;
            var created = _dataRepository.CreateJob(actor, warpJob.Space, view, template.TypeGuid, template);
            exported = created.AsReadOnly();
            exportView = view.AsReadOnly();
            return Task.CompletedTask;
        });
        await JobCreated.InvokeHierarchy(exported, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, null));
        await ViewUpdated.InvokeHierarchy(exportView, GroupName.ViewHierarchy(job.Space.Project.Id, job.Space.Id, exportView.Id));
        await QueueLocalJob(user, exported);
        return exported;
    }

    /// <summary>
    /// Writes the source session's accepted selection into the export job's directory, as it is now. The session's job
    /// may be processing meanwhile; it cannot be cleared or deleted while one of its exports is queued or running
    /// (<see cref="HasPendingWarpExport"/>), so the files the export reads stay.
    /// </summary>
    private async Task RunWarpExportAsync(WarpExportJob export, CancellationToken token)
    {
        var source = export.Space.FindJob(export.SourceWarpJobId) as WarpJob
            ?? throw new InvalidOperationException($"Warp session J{export.SourceWarpJobId} no longer exists.");
        if (source.Status is JobStatus.Deleted or JobStatus.Clearing)
            throw new InvalidOperationException($"Warp session J{source.Id} is being removed.");
        using var lease = WarpSessions.Acquire(source.Identity, source.Settings);
        var session = lease.Session;
        // The lease spans the job update, so views redraw with the new manifest when it ends.
        using var operation = session.BeginExportOperation();
        string root = export.Space.RootDirectory;
        var manifest = await session.ExportAsync(export.Particles, export.DirectoryPath, root, operation, token);
        export.Summarize(manifest);
        // Saved relative to the space, like the export's files, so the job's outputs hold when the space moves.
        _dataRepository.UpdateJob(export.UpdatedBy, export, _ => export.Manifest = manifest.RelativeTo(root));
        await JobUpdated.InvokeHierarchy(export.AsReadOnly(), GroupName.JobHierarchy(export.Space.Project.Id, export.Space.Id, export.Id));
    }

    /// <summary>An export job reading this Warp session's files is queued or running.</summary>
    private bool HasPendingWarpExport(Job job) => job is WarpJob && job.Space != null &&
        job.Space.Jobs.Any(other => other is WarpExportJob export && export.SourceWarpJobId == job.Id && HasPendingExecution(export));
}
