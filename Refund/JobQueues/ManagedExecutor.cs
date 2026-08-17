using Refund.DataModel;

namespace Refund.JobQueues;

/// <summary>
/// Runs jobs as local processes and accounts for the host's resources. Exactly one instance exists
/// per Relay host, owned by QueueRepository — a host has one set of GPUs, so it has one ledger.
/// </summary>
public sealed class ManagedExecutor
{
    private sealed class Entry
    {
        public required ResourceAllocation Allocation { get; init; }
        public IManagedProcess Process { get; set; }
        public int? ExitCode { get; set; }
    }

    /// <summary>
    /// Keyed by job identity, not by allocation: <see cref="ResourceAllocation"/> is a record whose
    /// GpuIndices compares by reference, so it has no usable value equality. Job does not override
    /// Equals either, which is exactly what is wanted here — one entry per job object.
    /// </summary>
    private readonly Dictionary<Job, Entry> _entries = new();

    private readonly object _sync = new();

    /// <summary>
    /// Cores are per-process in the job model (<c>Job.CoreCount</c>, Job.cs:347) while memory is
    /// already a total in every override. Conflating the two silently over- or under-books the host.
    /// </summary>
    /// <remarks>
    /// Every dimension is clamped at zero. <see cref="ResourceLedger"/> is deliberately permissive
    /// and will fit a negative request, handing back an allocation that *adds* capacity once it is
    /// in the live set — a job declaring -4 cores would make the host report four cores more than
    /// it has. The two core factors are clamped separately rather than only their product, so a
    /// pair of negatives cannot multiply back into a plausible-looking positive.
    /// </remarks>
    public static ResourceRequest RequestFor(Job job)
    {
        // long, so a pathological ProcessCount x CoreCount cannot overflow into a negative int.
        long processes = Math.Max(0, job.ProcessCount);
        long coresPerProcess = Math.Max(0, job.CoreCount);
        long cores = Math.Min(processes * coresPerProcess, int.MaxValue);

        return new ResourceRequest((int)cores,
                                   Math.Max(0, job.MemoryGb),
                                   Math.Max(0, job.GpuCount));
    }

    /// <summary>
    /// Reserve resources for a job, or explain why not. Idempotent: re-offering a job that is
    /// already tracked returns Admit without booking the host a second time.
    /// </summary>
    public AdmissionResult TryAdmit(Job job, ResourceTotals totals)
    {
        var request = RequestFor(job);

        if (!ResourceLedger.CanEverFit(totals, request))
            return new AdmissionResult.Reject(
                $"Job needs {request.Cores} cores, {request.MemoryGb} GB and {request.Gpus} GPU(s); " +
                $"this queue has {totals.Cores} cores, {totals.MemoryGb} GB and {totals.Gpus} GPU(s), " +
                "so it can never run here.");

        lock (_sync)
        {
            Reconcile();

            if (_entries.ContainsKey(job))
                return AdmissionResult.Admitted;   // already admitted; idempotent

            if (!ResourceLedger.TryFit(totals, LiveAllocationsLocked(), request, out var allocation))
                return AdmissionResult.IsBusy;

            _entries[job] = new Entry { Allocation = allocation };
            return AdmissionResult.Admitted;
        }
    }

    /// <summary>The GPU indices this job was given, for CUDA_VISIBLE_DEVICES. Empty if untracked.</summary>
    public IReadOnlyList<int> GpuIndicesFor(Job job)
    {
        lock (_sync)
            return _entries.TryGetValue(job, out var e) ? e.Allocation.GpuIndices : Array.Empty<int>();
    }

    /// <summary>
    /// Bind a freshly spawned process to its reservation.
    /// </summary>
    /// <returns>
    /// False if the job is no longer tracked — its reservation was retired while the process was
    /// starting (an abort during staging, say). The caller owns an unaccounted process at that
    /// point and must kill it rather than leave it running outside the ledger.
    /// </returns>
    public bool Attach(Job job, IManagedProcess process)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(job, out var entry))
                return false;

            entry.Process = process;
            return true;
        }
    }

    public void Reap()
    {
        lock (_sync)
            Reconcile();
    }

    /// <summary>
    /// A stable snapshot of what is currently held, suitable for handing to
    /// <see cref="ResourceLedger"/> — materialised under the lock, not lazy over the entry table.
    /// </summary>
    public IEnumerable<ResourceAllocation> LiveAllocations()
    {
        lock (_sync)
        {
            Reconcile();
            return LiveAllocationsLocked().ToList();
        }
    }

    public bool HasEntries(Func<Job, bool> predicate)
    {
        lock (_sync)
        {
            Reconcile();
            return _entries.Keys.Any(predicate);
        }
    }

    public ClusterJobStatus GetStatus(Job job)
    {
        lock (_sync)
        {
            Reconcile();

            if (!_entries.TryGetValue(job, out var entry))
                return ClusterJobStatus.Failed;     // untracked: nothing is running this

            if (entry.ExitCode is { } code)
                return code == 0 ? ClusterJobStatus.Finished : ClusterJobStatus.Failed;

            return entry.Process == null ? ClusterJobStatus.Pending : ClusterJobStatus.Running;
        }
    }

    public void Kill(Job job)
    {
        lock (_sync)
            if (_entries.TryGetValue(job, out var entry))
                entry.Process?.KillTree();
    }

    /// <summary>
    /// The single reconciliation pass. Everything that frees a resource happens here, which is why
    /// there is no Release() for any exit path to forget.
    /// </summary>
    /// <remarks>
    /// The order of the three cases is load-bearing. A running process always keeps its allocation,
    /// whatever the job's status says; job status can retire only an entry with no process.
    /// </remarks>
    private void Reconcile()
    {
        foreach (var (job, entry) in _entries.ToList())
        {
            if (entry.Process is { HasExited: true } exited)
            {
                entry.ExitCode ??= exited.ExitCode;         // resources free from here (see LiveAllocationsLocked)
                if (!IsJobActive(job))
                    _entries.Remove(job);                   // job settled too; forget it entirely
                continue;
            }

            if (IsJobActive(job))
                continue;

            if (entry.Process != null)
            {
                // Terminal job, live process. Never free here: HandleAbortingState force-marks a job
                // Aborted after 30s whether or not the kill landed, and releasing would hand a
                // still-computing job's GPU to someone else. Kill, and wait for a later pass.
                entry.Process.KillTree();
                continue;
            }

            _entries.Remove(job);                           // abandoned reservation, no process
        }
    }

    private static bool IsJobActive(Job job) =>
        job.Status.IsUnsettled() || job.Status == JobStatus.Waiting;

    /// <summary>An entry holds resources until its process has exited; see Reconcile.</summary>
    private IEnumerable<ResourceAllocation> LiveAllocationsLocked() =>
        _entries.Values.Where(e => e.ExitCode == null).Select(e => e.Allocation);
}
