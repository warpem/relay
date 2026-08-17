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

        /// <summary>
        /// Set once this entry has been marked for death, and never un-set. It survives the job
        /// becoming active again, which is what stops a re-queued job resurrecting the leftover
        /// process of its previous run; see <see cref="Condemn"/>.
        /// </summary>
        public bool Condemned { get; set; }

        /// <summary>
        /// Whether this entry is a reservation the job could still be launched into: not condemned,
        /// and not already spent by a process that has run and exited.
        /// </summary>
        public bool IsUsableReservation => !Condemned && ExitCode == null;
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
    /// Reserve resources for a job, or explain why not. Idempotent for a reservation belonging to
    /// the job's current run: re-offering it returns Admit without booking the host a second time.
    /// </summary>
    /// <remarks>
    /// A stale entry — one that is condemned, or whose process has already run and exited — is
    /// never reused. A job can be re-queued while the process of its previous run is still alive
    /// (Aborted -> Building -> Waiting is a legal transition, Job.cs:1346), and handing that run
    /// back the old entry would launch it against an allocation sized for the old parameters and
    /// feed it the old GPU indices. It is condemned instead, and the job waits for it to die.
    /// </remarks>
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

            if (_entries.TryGetValue(job, out var existing))
            {
                if (existing.IsUsableReservation)
                    return AdmissionResult.Admitted;   // this run's own reservation; idempotent

                // Leftover from a previous run of the same job. Condemning is what keeps
                // Reconcile killing it now that the job is active again, and what retires it the
                // moment it dies; without that the entry would be protected by the job's new
                // Waiting status and nothing would ever clean it up.
                Condemn(existing);
                Reconcile();

                if (_entries.ContainsKey(job))
                    return AdmissionResult.IsBusy;     // still winding down; the daemon re-asks
            }

            if (!ResourceLedger.TryFit(totals, LiveAllocationsLocked(), request, out var allocation))
                return AdmissionResult.IsBusy;

            _entries[job] = new Entry { Allocation = allocation };
            return AdmissionResult.Admitted;
        }
    }

    /// <summary>
    /// The GPU indices this job was given, for CUDA_VISIBLE_DEVICES. Empty unless the job holds a
    /// reservation it could still be launched into — the same rule <see cref="Attach"/> applies,
    /// so a caller can never read devices belonging to a run that is over.
    /// </summary>
    public IReadOnlyList<int> GpuIndicesFor(Job job)
    {
        lock (_sync)
        {
            Reconcile();

            return _entries.TryGetValue(job, out var e) && e.IsUsableReservation
                       ? e.Allocation.GpuIndices
                       : Array.Empty<int>();
        }
    }

    /// <summary>
    /// Bind a freshly spawned process to its reservation. Only ever binds to a reservation that
    /// has no process yet.
    /// </summary>
    /// <returns>
    /// False if there is nothing to bind to. Either the reservation was retired while the process
    /// was starting (an abort during staging, say), or it already holds a process. In both cases
    /// the caller owns an unaccounted process and must kill it rather than leave it running
    /// outside the ledger. Refusing is essential in the second case: overwriting a live
    /// <see cref="Entry.Process"/> would make the old one unreachable, so nothing would ever kill
    /// it, reap it, or account for the GPU it is holding.
    /// </returns>
    public bool Attach(Job job, IManagedProcess process)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(job, out var entry))
                return false;

            if (entry.Process != null || !entry.IsUsableReservation)
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

    /// <summary>
    /// Signal a job's process to stop. Condemns the entry, so it is retired as soon as the process
    /// dies even if the job's status has moved on in the meantime.
    /// </summary>
    /// <remarks>
    /// Signals at most once per entry; escalating an unresponsive process belongs inside
    /// <see cref="IManagedProcess.KillTree"/>, not in a caller that re-signals on a timer.
    /// </remarks>
    public void Kill(Job job)
    {
        lock (_sync)
            if (_entries.TryGetValue(job, out var entry))
                Condemn(entry);
    }

    /// <summary>
    /// Mark an entry for death and signal its process, at most once. Idempotent, and sticky: an
    /// entry never becomes un-condemned, so the job going active again cannot rescue it.
    /// </summary>
    private static void Condemn(Entry entry)
    {
        if (entry.Condemned)
            return;

        entry.Condemned = true;
        entry.Process?.KillTree();
    }

    /// <summary>
    /// The single reconciliation pass. Everything that frees a resource happens here, which is why
    /// there is no Release() for any exit path to forget.
    /// </summary>
    /// <remarks>
    /// The order of the cases is load-bearing. A running process always keeps its allocation,
    /// whatever the job's status says; status (or condemnation) can retire only an entry with no
    /// live process.
    /// </remarks>
    private void Reconcile()
    {
        foreach (var (job, entry) in _entries.ToList())
        {
            if (entry.Process is { HasExited: true } exited)
            {
                entry.ExitCode ??= exited.ExitCode;         // resources free from here (see LiveAllocationsLocked)
                if (entry.Condemned || !IsJobActive(job))
                    _entries.Remove(job);                   // settled or condemned; forget it entirely
                continue;
            }

            if (!entry.Condemned && IsJobActive(job))
                continue;

            if (entry.Process != null)
            {
                // Terminal or condemned, live process. Never free here: HandleAbortingState
                // force-marks a job Aborted after 30s whether or not the kill landed, and releasing
                // would hand a still-computing job's GPU to someone else. Condemn — which signals
                // exactly once, however many passes this takes — and wait for the exit.
                Condemn(entry);
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
