using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Jobs;

[GenerateReadOnly]
public abstract class WarpJobGpu : WarpJob, IPooledJob
{
    /// <summary>
    /// True when this job runs as a pool Manager — a CPU-only orchestrator that populates the
    /// task queue and maintains the worker fleet. The pooled workers carry the GPUs, so the
    /// Manager's own cluster submission must NOT request GPUs or per-worker-scaled resources.
    /// </summary>
    public bool IsPooled => PoolQueueId > 0;

    public override int GpuCount => IsPooled ? 0 : NGpus;

    public override int GpuMemoryGb => IsPooled ? 0 : base.GpuMemoryGb;

    public override JobQueueType QueueType => JobQueueType.GPU;

    public override int CoreCount => IsPooled ? ManagerCoreCount : (NGpus * PerDevice) * 2;

    public override int MemoryGb => IsPooled ? ManagerMemoryGb : (NGpus * PerDevice) * MemoryPerWorker;

    /// <summary>CPU cores requested for the pool Manager orchestrator process (does no GPU work).</summary>
    protected virtual int ManagerCoreCount => 4;

    /// <summary>System memory (GB) requested for the pool Manager orchestrator process.</summary>
    protected virtual int ManagerMemoryGb => 16;

    [UiFieldGroup("Resources", 999)]
    [UiInt("", "Number of GPUs",
           helpText: "Number of GPUs to request for this job. When a pool queue is set, this is " +
                     "the number of parallel GPU workers maintained in the pool (one worker per GPU).",
           min: 1)]
    [RelayProperty]
    public virtual int NGpus { get; set; } = 1;

    [UiInt("perdevice", "Workers per GPU",
           helpText: "Number of workers to use per GPU. Higher values may improve GPU utilization, " +
                     "but will also increase GPU memory consumption.",
           min: 1)]
    [RelayProperty]
    public virtual int PerDevice { get; set; } = 2;

    [UiFieldGroup("Resources", 999)]
    [UiQueue("Pool Queue",
             helpText: "Cluster queue for the GPU worker pool. Leave as Local (no pool) to run workers on this machine.")]
    [RelayProperty]
    public int PoolQueueId { get; set; } = -1;

    /// <summary>
    /// Number of alive pool workers (present and non-terminal: running + pending) at last daemon
    /// tick. Updated by QueueRepository.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public int PoolWorkersAlive { get; set; }

    /// <summary>
    /// Number of pool workers the scheduler reports as actually running (a subset of
    /// <see cref="PoolWorkersAlive"/>; the rest are pending) at last daemon tick. Updated by
    /// QueueRepository.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public int PoolWorkersRunning { get; set; }

    /// <summary>Total worker submissions since this job started. Updated by QueueRepository.</summary>
    [RelayProperty]
    [Clearable]
    public int PoolWorkersSubmitted { get; set; }

    /// <summary>
    /// Gets the modules that this job can utilize if available.
    /// Includes the base supported modules plus "warp".
    /// </summary>
    public override string[] SupportedModules => base.SupportedModules.Concat(["gpu"]).ToArray();

    /// <summary>
    /// Gets the modules that must be available for this job to run.
    /// A pool Manager is a CPU-only orchestrator, so it requires the "cpu" module (CPU
    /// partition/queue directives); a non-pooled/local job does its own GPU work and requires
    /// "gpu". Mirrors the GPU/CPU module toggle used by the RELION refinement jobs.
    /// </summary>
    public override string[] RequiredModules =>
        base.RequiredModules.Concat(IsPooled ? ["cpu"] : ["gpu"]).ToArray();

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();
        if (PoolQueueId > 0)
            result["external_provisioner"] = "";
        return result;
    }

    /// <summary>
    /// Target number of pool workers. Derived from <see cref="NGpus"/> — each pool worker is
    /// one GPU, so the worker count equals the requested GPU count (no separate pool-size field).
    /// Public (not just an explicit interface member) so the generated read-only wrapper exposes
    /// it, letting the UI reference the pool abstraction rather than a GPU-specific field.
    /// </summary>
    public int PoolSize => NGpus;

    // IPooledJob
    // PoolQueueId and PoolSize satisfy the interface implicitly via the public members above;
    // the remaining members are derived/computed.
    int IPooledJob.PoolSubmissionCap          => PoolSize * 2;

    // Build the worker's template variables from the Manager's own GetResourceValues so the two
    // can never drift: the worker inherits every variable the template expects (and any future
    // additions), then overrides only the worker-specific profile.
    Dictionary<string, string> IPooledJob.GetWorkerResourceValues(string workerLogDir)
    {
        var values = GetResourceValues();
        values["job_id"]        = $"{Id}-worker";
        values["n_processes"]   = "1";
        values["n_cores"]       = "2";
        values["memory_gb"]     = MemoryPerWorker.ToString();
        values["n_gpus"]        = "1";                              // one worker == one GPU
        values["gpu_memory_gb"] = base.GpuMemoryGb.ToString();     // un-pooled per-GPU default
        values["std_out"]       = Path.Combine(workerLogDir, "%j.out");
        values["std_err"]       = Path.Combine(workerLogDir, "%j.err");
        return values;
    }

    // Workers are always single-GPU, regardless of the Manager's (CPU-only when pooled)
    // module profile — so they require the "gpu" module, NOT this job's RequiredModules.
    string[] IPooledJob.WorkerRequiredModules => base.RequiredModules.Concat(["gpu"]).ToArray();

    // Mirror the Manager's regular script: cd to RunDirectory first so the worker executes from
    // the same working directory (where the job's relative paths, e.g. processing.settings, resolve).
    // WarpWorker2 CLI: --queue-dir is the shared task queue the Manager populates (<output>/tasks),
    // --device is the GPU id, --log-dir is for per-item <task_id>.log files. The Manager creates
    // and expects those at <output>/logs (DistributedOptions), so point --log-dir there — NOT at
    // WarpWorker2's <queue-dir>/logs default — so worker and Manager agree. (<output> == DirectoryPath.)
    string IPooledJob.GetWorkerCommand(int deviceIndex) =>
        $"cd {RunDirectory}\nWarpWorker2 " +
        $"--queue-dir {Path.Combine(DirectoryPath, "tasks")} " +
        $"--device {deviceIndex} " +
        $"--log-dir {Path.Combine(DirectoryPath, "logs")}";
}
