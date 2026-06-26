using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Refund.DataModel;

namespace Refund.JobQueues;

/// <summary>
/// Manages a fleet of short-lived GPU worker cluster jobs for one pooled Relay job.
/// Owned and driven by QueueRepository's pool wiring.
/// </summary>
public class WorkerPool
{
    /// <summary>
    /// Maximum workers submitted in a single tick. A large pool fills over several ticks rather
    /// than blocking one daemon iteration on a long synchronous burst of sbatch calls, and the
    /// submitted/alive counts advance every tick (~1s) instead of only after the whole fleet is in.
    /// </summary>
    private const int MaxSubmitsPerTick = 5;

    private readonly IPoolQueue _poolQueue;
    private readonly IPooledJob _job;
    private readonly string     _jobDir;
    private readonly ILogger    _log;

    private readonly HashSet<string> _submittedIds = new();
    private          HashSet<string> _aliveIds     = new();
    private int    _totalSubmissions;
    private bool   _initialized;
    private string _workerScriptPath = "";

    private string StatePath        => Path.Combine(_jobDir, "pool_state.json");
    private string WorkerLogsDir     => Path.Combine(_jobDir, "worker_logs");
    private string WorkerScriptPath  => Path.Combine(_jobDir, "worker_submit.sh");

    public WorkerPool(IPoolQueue poolQueue, IPooledJob job)
    {
        _poolQueue = poolQueue;
        _job       = job;
        _jobDir    = job.DirectoryPath;
        _log       = Log.ForContext<WorkerPool>().ForContext("PoolDir", _jobDir);
    }

    /// <summary>
    /// Prepares the worker submission script and loads any persisted state.
    /// Must be called before the first Tick(). Idempotent.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        Directory.CreateDirectory(WorkerLogsDir);

        _workerScriptPath = WorkerScriptPath;
        // The job derives the worker's template variables from the Manager's own GetResourceValues
        // (see GetWorkerResourceValues), so the worker script can never silently miss a variable the
        // template expects. WorkerLogsDir is where the worker's SLURM stdout/stderr land.
        var resourceValues = _job.GetWorkerResourceValues(WorkerLogsDir);
        _poolQueue.BuildWorkerScript(
            _job.GetWorkerCommand(0), resourceValues, _job.WorkerRequiredModules, _workerScriptPath);

        LoadState();
        _initialized = true;

        _log.Information(
            "Worker pool initialized: poolQueueId={PoolQueueId} target={Target} workerCmd={WorkerCmd} " +
            "scriptPath={ScriptPath} restoredSubmittedIds={Restored} totalSubmissions={Total}",
            _job.PoolQueueId, _job.PoolSize, _job.GetWorkerCommand(0),
            _workerScriptPath, _submittedIds.Count, _totalSubmissions);
    }

    /// <summary>
    /// One maintenance tick: reconcile alive workers, submit replacements up to the cap.
    /// Returns (aliveCount, runningCount, totalSubmissions) for the caller to push to the job model.
    /// "Alive" is every one of our workers still present and non-terminal in the scheduler
    /// (running + pending + unknown); "running" is the subset the scheduler reports as actually
    /// executing. The difference is pending — the distinction the pool UI needs so a fleet that is
    /// mostly queued doesn't read as fully up.
    /// </summary>
    public async Task<(int aliveCount, int runningCount, int totalSubmissions)> Tick()
    {
        if (!_initialized)
            throw new InvalidOperationException("Call Initialize() before Tick().");

        var active = await _poolQueue.ListActiveJobs();

        // Keep only our workers that are still present and not in a terminal state — a worker that
        // finished or failed must be replaced, not counted as filling its slot.
        var ours = _submittedIds
            .Where(id => active.TryGetValue(id, out var s)
                         && s != ClusterJobStatus.Finished
                         && s != ClusterJobStatus.Failed)
            .ToDictionary(id => id, id => active[id]);

        _aliveIds       = ours.Keys.ToHashSet();
        int runningCount = ours.Count(kv => kv.Value == ClusterJobStatus.Running);

        int deficit   = Math.Max(0, _job.PoolSize - _aliveIds.Count);
        int canSubmit = Math.Max(0, _job.PoolSubmissionCap - _totalSubmissions);
        int toSubmit  = Math.Min(Math.Min(deficit, canSubmit), MaxSubmitsPerTick);

        _log.Debug(
            "Worker pool tick: target={Target} schedulerActive={SchedulerActive} ours={Submitted} " +
            "alive={Alive} running={Running} pending={Pending} deficit={Deficit} " +
            "capRemaining={CapRemaining} submitting={ToSubmit}",
            _job.PoolSize, active.Count, _submittedIds.Count, _aliveIds.Count,
            runningCount, _aliveIds.Count - runningCount, deficit, canSubmit, toSubmit);

        for (int i = 0; i < toSubmit; i++)
        {
            string id = await _poolQueue.SubmitScript(_workerScriptPath);
            _submittedIds.Add(id);
            _aliveIds.Add(id);
            _totalSubmissions++;
            _log.Information("Worker pool submitted worker {Index}/{ToSubmit}: clusterJobId={ClusterJobId}",
                i + 1, toSubmit, id);
        }

        SaveState();
        return (_aliveIds.Count, runningCount, _totalSubmissions);
    }

    /// <summary>
    /// Cancels all known alive workers in one call and clears state.
    /// Called when the Manager job ends for any reason.
    /// </summary>
    public async Task Dissolve()
    {
        _log.Information("Worker pool dissolving: cancelling {Count} alive workers", _aliveIds.Count);

        if (_aliveIds.Count > 0)
            await _poolQueue.CancelJobs(_aliveIds);

        _aliveIds.Clear();
        _submittedIds.Clear();
        _totalSubmissions = 0;

        if (File.Exists(StatePath))
            File.Delete(StatePath);
    }

    private void SaveState()
    {
        var tmp = StatePath + ".tmp." + Environment.ProcessId;
        var node = new JsonObject
        {
            ["pool_queue_id"]     = _job.PoolQueueId,
            ["submitted_ids"]     = new JsonArray(_submittedIds.Select(id => JsonValue.Create(id)).ToArray<JsonNode>()),
            ["total_submissions"] = _totalSubmissions,
        };
        File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StatePath, overwrite: true);
    }

    private void LoadState()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(StatePath));
            if (node == null) return;

            _totalSubmissions = node["total_submissions"]?.GetValue<int>() ?? 0;
            var ids = node["submitted_ids"]?.AsArray();
            if (ids != null)
                foreach (var id in ids)
                    if (id?.GetValue<string>() is { } s)
                        _submittedIds.Add(s);
        }
        catch
        {
            // Corrupted state file — start fresh; worst case is brief over-provisioning.
        }
    }
}
