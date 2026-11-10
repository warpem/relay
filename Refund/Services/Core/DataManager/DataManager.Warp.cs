using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Fs.Warp;
using Refund.Services.Warp;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    private readonly HashSet<Job> _warpExportOperations = [];

    // Execution and every browser circuit share this instance. It never captures a UI user.
    public WarpSessionManager WarpSessions { get; } = new(Log.ForContext<WarpSessionManager>());

    public IReadOnlyList<ReadOnlyJobQueue> GetWarpWorkerQueues() => _queueRepository.ClusterQueues
        .OfType<Refund.JobQueues.ClusterQueue>()
        .Where(queue => !queue.IsManaged && queue.QueueType.HasFlag(JobQueueType.GPU))
        .Select(queue => queue.AsReadOnly()).ToArray();

    private Task RunLocalJobAsync(Job job, CancellationToken token)
    {
        if (job is WarpJob warpJob)
            return RunWarpAsync(warpJob, token);
        ((ILocalJob)job).RunLocal(token);
        return Task.CompletedTask;
    }

    private async Task RunWarpAsync(WarpJob job, CancellationToken token)
    {
        var session = WarpSessions.GetOrCreate(job.Identity, job.Settings);
        using var progressCancellation = new CancellationTokenSource();
        var progress = ProjectWarpProgressAsync(job, session, progressCancellation.Token);
        try { await WarpSessions.RunAsync(job.Identity, job.Settings, token); }
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
            var session = WarpSessions.GetOrCreate(warpJob.Identity, warpJob.Settings);
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
            var session = WarpSessions.GetOrCreate(warpJob.Identity, warpJob.Settings);
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
            return Task.FromResult(update(WarpSessions.GetOrCreate(warpJob.Identity, warpJob.Settings)));
        });

    public async Task<ReadOnlyJob> ExportWarpJob(ReadOnlyUser user, ReadOnlyWarpJob job, bool particles)
    {
        var (session, source, operation) = await ExecuteWithLock(() =>
        {
            var (_, warpJob) = ResolveWritableWarp(user, job);
            var shared = WarpSessions.GetOrCreate(warpJob.Identity, warpJob.Settings);
            if (!_warpExportOperations.Add(warpJob))
                throw new InvalidOperationException("An export is already being compiled for this session.");
            try { return Task.FromResult((shared, warpJob, shared.BeginExportOperation())); }
            catch { _warpExportOperations.Remove(warpJob); throw; }
        });
        try
        {
            // Capturing can read many small STAR files. Keep it outside the command lock,
            // but retain a lifetime lease so destructive actions cannot remove its files.
            var manifest = await session.ExportAsync(particles, operation);
            ReadOnlyJob exported = null;
            ReadOnlyView exportView = null;
            await ExecuteSpaceChange(job.Space, () =>
            {
                var (actor, warpJob) = ResolveWritableWarp(user, job);
                var view = warpJob.Space.Views.FirstOrDefault(candidate => candidate.Jobs.Any(item => item.Id == warpJob.Id))
                    ?? warpJob.Space.Views.FirstOrDefault()
                    ?? throw new InvalidOperationException("The session's space has no view for its export.");
                var template = particles ? (WarpExportJob)new WarpParticlesExportJob() : new WarpMicrographsExportJob();
                template.SourceWarpJobId = warpJob.Id;
                template.Manifest = manifest;
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
        finally
        {
            try
            {
                await ExecuteWithLock(() =>
                {
                    _warpExportOperations.Remove(source);
                    return Task.CompletedTask;
                });
            }
            finally { operation.Dispose(); }
        }
    }
}
