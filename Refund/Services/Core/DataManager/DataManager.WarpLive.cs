using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Fs.WarpLive;
using Refund.Services.WarpLive;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    private readonly HashSet<Job> _warpLiveExportOperations = [];

    // Execution and every browser circuit share this instance. It never captures a UI user.
    public WarpLiveSessionManager WarpLiveSessions { get; } = new(Log.ForContext<WarpLiveSessionManager>());

    public IReadOnlyList<ReadOnlyJobQueue> GetWarpLiveWorkerQueues() => _queueRepository.ClusterQueues
        .OfType<Refund.JobQueues.ClusterQueue>()
        .Where(queue => !queue.IsManaged && queue.QueueType.HasFlag(JobQueueType.GPU))
        .Select(queue => queue.AsReadOnly()).ToArray();

    private Task RunLocalJobAsync(Job job, CancellationToken token)
    {
        if (job is WarpLiveJob live)
            return RunWarpLiveAsync(live, token);
        ((ILocalJob)job).RunLocal(token);
        return Task.CompletedTask;
    }

    private async Task RunWarpLiveAsync(WarpLiveJob job, CancellationToken token)
    {
        var session = WarpLiveSessions.GetOrCreate(job.Identity, job.Settings);
        using var progressCancellation = new CancellationTokenSource();
        var progress = ProjectWarpLiveProgressAsync(job, session, progressCancellation.Token);
        try { await WarpLiveSessions.RunAsync(job.Identity, job.Settings, token); }
        finally
        {
            progressCancellation.Cancel();
            try { await progress; } catch (OperationCanceledException) { }
            await PublishWarpLiveProgressAsync(job, session);
        }
    }

    private async Task ProjectWarpLiveProgressAsync(WarpLiveJob job, WarpLiveSession session, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(token))
            await PublishWarpLiveProgressAsync(job, session);
    }

    private async Task PublishWarpLiveProgressAsync(WarpLiveJob job, WarpLiveSession session)
    {
        int total = session.Movies.Count, failed = session.Failed, processed = session.Complete + failed;
        if (job.NItemsTotal == total && job.NItemsFailed == failed && job.NItemsProcessed == processed) return;
        _dataRepository.UpdateJob(job.UpdatedBy, job, _ =>
        {
            job.NItemsTotal = total; job.NItemsFailed = failed; job.NItemsProcessed = processed;
        });
        await JobUpdated.InvokeHierarchy(job.AsReadOnly(), GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    private (User User, WarpLiveJob Job) ResolveWritableWarpLive(ReadOnlyUser user, ReadOnlyWarpLiveJob job)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(job);
        var actor = ResolveUser(user.Id);
        var live = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id) as WarpLiveJob
            ?? throw new InvalidOperationException("This job is not a Warp Live session.");
        var project = live.Space.Project;
        if (actor.Role == UserRole.Viewer || (actor.Role != UserRole.Admin &&
            project.Owner?.Id != actor.Id && !project.Members.Any(member => member.Id == actor.Id)))
            throw new UnauthorizedAccessException("You do not have permission to modify this preprocessing session.");
        if (live.Status is JobStatus.Deleted or JobStatus.Clearing)
            throw new InvalidOperationException("The session is being removed.");
        return (actor, live);
    }

    private void EnsureWarpLiveStopped(WarpLiveJob live)
    {
        if (_queueRepository.HasExecutionAttempt(live) || live.Status is
            JobStatus.Waiting or JobStatus.Staging or JobStatus.Running or JobStatus.Aborting or JobStatus.Finalizing)
            throw new InvalidOperationException("Stop processing and wait for its workers to finish before changing setup.");
    }

    public async Task StartWarpLiveJob(ReadOnlyUser user, ReadOnlyWarpLiveJob job, int poolQueueId, int poolSize)
    {
        await ExecuteWithLock(async () =>
        {
            var (actor, live) = ResolveWritableWarpLive(user, job);
            EnsureWarpLiveStopped(live);
            if (poolSize < 1) throw new ArgumentOutOfRangeException(nameof(poolSize));
            var queue = ResolveQueue(poolQueueId);
            if (queue is not Refund.JobQueues.ClusterQueue cluster || cluster.IsManaged || !cluster.QueueType.HasFlag(JobQueueType.GPU))
                throw new InvalidOperationException("Warp Live requires an external scheduler queue for its GPU workers.");
            cluster.ValidateSubmissionConfiguration();
            var session = WarpLiveSessions.GetOrCreate(live.Identity, live.Settings);
            session.Apply(live.Settings with { OutputPath = live.DirectoryPath });
            _dataRepository.UpdateJob(actor, live, _ =>
            {
                live.Settings = session.Settings;
                live.UseWorkerPool = true;
                live.PoolQueueId = poolQueueId;
                live.NGpus = poolSize;
                // Reset execution status, never the durable preprocessing directory.
                live.Status = JobStatus.Building;
                live.QueueId = _queueRepository.LocalQueue.Id;
                live.SubmittedByUsername = actor.Username;
            });
            EnsureJobInputsValid(live);
            await _queueRepository.QueueJobAsync(live, _queueRepository.LocalQueue);
        });
        await JobQueued.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public async Task StopWarpLiveJob(ReadOnlyUser user, ReadOnlyWarpLiveJob job)
    {
        await ExecuteWithLock(async () =>
        {
            var (_, live) = ResolveWritableWarpLive(user, job);
            if (!_queueRepository.HasExecutionAttempt(live)) return;
            await _queueRepository.CancelJobAsync(live);
        });
    }

    public async Task ResizeWarpLiveJob(ReadOnlyUser user, ReadOnlyWarpLiveJob job, int size)
    {
        await ExecuteWithLock(async () =>
        {
            var (actor, live) = ResolveWritableWarpLive(user, job);
            if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
            if (_queueRepository.HasExecutionAttempt(live))
                await _queueRepository.ResizeWorkerGroupAsync(live, size);
            else
            {
                EnsureWarpLiveStopped(live);
                _dataRepository.UpdateJob(actor, live, _ => live.NGpus = size);
            }
        });
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public async Task ConfigureWarpLiveWorkers(ReadOnlyUser user, ReadOnlyWarpLiveJob job,
        int poolQueueId, int poolSize, int perDevice, int memoryPerWorker)
    {
        await ExecuteWithLock(() =>
        {
            var (actor, live) = ResolveWritableWarpLive(user, job);
            EnsureWarpLiveStopped(live);
            if (poolSize < 1 || perDevice < 1 || memoryPerWorker < 1)
                throw new ArgumentOutOfRangeException(nameof(poolSize), "Worker resources must be positive.");
            if (ResolveQueue(poolQueueId) is not Refund.JobQueues.ClusterQueue queue || queue.IsManaged || !queue.QueueType.HasFlag(JobQueueType.GPU))
                throw new InvalidOperationException("Select an external scheduler queue.");
            _dataRepository.UpdateJob(actor, live, _ =>
            {
                live.PoolQueueId = poolQueueId; live.NGpus = poolSize;
                live.PerDevice = perDevice; live.MemoryPerWorker = memoryPerWorker;
            });
            return Task.CompletedTask;
        });
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public async Task ApplyWarpLiveSettings(ReadOnlyUser user, ReadOnlyWarpLiveJob job, WarpLiveSettings settings)
    {
        await ExecuteWithLock(() =>
        {
            var (actor, live) = ResolveWritableWarpLive(user, job);
            EnsureWarpLiveStopped(live);
            var session = WarpLiveSessions.GetOrCreate(live.Identity, live.Settings);
            session.Apply(settings with { OutputPath = live.DirectoryPath });
            _dataRepository.UpdateJob(actor, live, _ => live.Settings = session.Settings);
            return Task.CompletedTask;
        });
        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    public Task UpdateWarpLiveSession(ReadOnlyUser user, ReadOnlyWarpLiveJob job, Action<WarpLiveSession> update) =>
        UpdateWarpLiveSession(user, job, session => { update(session); return true; });

    public Task<T> UpdateWarpLiveSession<T>(ReadOnlyUser user, ReadOnlyWarpLiveJob job, Func<WarpLiveSession, T> update) =>
        ExecuteWithLock(() =>
        {
            var (_, live) = ResolveWritableWarpLive(user, job);
            return Task.FromResult(update(WarpLiveSessions.GetOrCreate(live.Identity, live.Settings)));
        });

    public async Task<ReadOnlyJob> ExportWarpLiveJob(ReadOnlyUser user, ReadOnlyWarpLiveJob job, bool particles)
    {
        var (session, source, operation) = await ExecuteWithLock(() =>
        {
            var (_, live) = ResolveWritableWarpLive(user, job);
            var shared = WarpLiveSessions.GetOrCreate(live.Identity, live.Settings);
            if (!_warpLiveExportOperations.Add(live))
                throw new InvalidOperationException("An export is already being compiled for this session.");
            try { return Task.FromResult((shared, live, shared.BeginExportOperation())); }
            catch { _warpLiveExportOperations.Remove(live); throw; }
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
                var (actor, live) = ResolveWritableWarpLive(user, job);
                var view = live.Space.Views.FirstOrDefault(candidate => candidate.Jobs.Any(item => item.Id == live.Id))
                    ?? live.Space.Views.FirstOrDefault()
                    ?? throw new InvalidOperationException("The session's space has no view for its export.");
                var template = particles ? (WarpLiveExportJob)new WarpLiveParticlesExportJob() : new WarpLiveMicrographsExportJob();
                template.SourceLiveJobId = live.Id;
                template.Manifest = manifest;
                var created = _dataRepository.CreateJob(actor, live.Space, view, template.TypeGuid, template);
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
                    _warpLiveExportOperations.Remove(source);
                    return Task.CompletedTask;
                });
            }
            finally { operation.Dispose(); }
        }
    }
}
