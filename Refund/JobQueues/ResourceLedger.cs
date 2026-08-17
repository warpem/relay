namespace Refund.JobQueues;

/// <summary>Everything a managed queue may hand out.</summary>
public readonly record struct ResourceTotals(int Cores, int MemoryGb, int Gpus);

/// <summary>What one job is asking for.</summary>
public readonly record struct ResourceRequest(int Cores, int MemoryGb, int Gpus);

/// <summary>What one job was given. GpuIndices are host device ids, not job-relative.</summary>
/// <remarks>
/// Allocations produced by <see cref="ResourceLedger"/> carry a genuinely immutable GpuIndices —
/// disjointness of GPU assignments is the one guarantee this type exists to make, so the list
/// handed back cannot be cast to <see cref="List{T}"/> and written through.
/// </remarks>
public sealed record ResourceAllocation(int Cores, int MemoryGb, IReadOnlyList<int> GpuIndices);

/// <summary>What is left over.</summary>
public readonly record struct LedgerSnapshot(
    int FreeCores, int FreeMemoryGb, IReadOnlyList<int> FreeGpuIndices);

/// <summary>
/// Pure resource arithmetic for a managed queue. Holds no state: callers pass both the totals and
/// the currently-live allocations on every call.
/// </summary>
/// <remarks>
/// Statelessness is the point. An incremental ledger — subtract on admit, add back on release —
/// leaks permanently if any one of the exit paths (finished, failed, aborted, killed at shutdown,
/// job deleted mid-flight) forgets to release, and the symptom is a queue that silently never
/// starts anything. Here "release" is not an operation at all: a job's resources are free the
/// moment its entry stops being in the live set the caller passes in.
///
/// It also means there is no configuration lifecycle to get wrong. ClusterQueue is constructed
/// before ReadFromJson hydrates its persisted totals, and an admin can edit them later; because
/// totals arrive per call, neither needs special handling.
/// </remarks>
public static class ResourceLedger
{
    /// <summary>
    /// What is left of <paramref name="totals"/> once every allocation in <paramref name="live"/>
    /// is subtracted.
    /// </summary>
    /// <param name="live">
    /// Must be a stable snapshot: enumerated once here, but callers making consecutive calls will
    /// get answers that disagree if the sequence is lazy over mutating state, or is mutated
    /// concurrently. The consuming queue enumerates a materialised list under its own lock.
    /// </param>
    /// <remarks>
    /// Total. Never throws, whatever the totals — a negative GPU total is clamped rather than
    /// rejected. See the note on <see cref="TryFit"/> for why this path must not raise.
    /// </remarks>
    public static LedgerSnapshot Compute(ResourceTotals totals, IEnumerable<ResourceAllocation> live)
    {
        int usedCores = 0, usedMemory = 0;
        var takenGpus = new HashSet<int>();

        foreach (var a in live)
        {
            usedCores += a.Cores;
            usedMemory += a.MemoryGb;
            foreach (var g in a.GpuIndices)
                takenGpus.Add(g);
        }

        // Math.Max, not a guard clause: Enumerable.Range throws on a negative count, and this runs
        // on the admission path. An exception there is caught by HandleWaitingState, which logs and
        // returns *without* changing job status, so the job would sit Waiting and re-log on every
        // daemon tick — exactly the failure AdmissionResult.Reject exists to prevent. A nonsensical
        // total should mean "nothing fits", not "the queue is wedged".
        var freeGpus = Enumerable.Range(0, Math.Max(0, totals.Gpus))
                                 .Where(g => !takenGpus.Contains(g))
                                 .ToList()
                                 .AsReadOnly();

        return new LedgerSnapshot(totals.Cores - usedCores, totals.MemoryGb - usedMemory, freeGpus);
    }

    /// <summary>
    /// Whether <paramref name="request"/> fits in what is currently free, and if so what it gets.
    /// </summary>
    /// <param name="live">
    /// Must be a stable snapshot; see <see cref="Compute"/>.
    /// </param>
    /// <remarks>
    /// Total: never throws for any combination of totals, live set and request. This is deliberate
    /// rather than incidental — the caller is the daemon's Waiting handler, whose catch logs without
    /// transitioning the job, so a throw here becomes a job stuck in Waiting that re-logs forever.
    /// </remarks>
    public static bool TryFit(ResourceTotals totals,
                              IEnumerable<ResourceAllocation> live,
                              ResourceRequest request,
                              out ResourceAllocation allocation)
    {
        allocation = null;

        var snap = Compute(totals, live);

        if (request.Cores > snap.FreeCores ||
            request.MemoryGb > snap.FreeMemoryGb ||
            request.Gpus > snap.FreeGpuIndices.Count)
            return false;

        allocation = new ResourceAllocation(
            request.Cores,
            request.MemoryGb,
            snap.FreeGpuIndices.Take(request.Gpus).ToList().AsReadOnly());

        return true;
    }

    /// <summary>
    /// Whether a request could ever be satisfied on an empty host. Distinguishes "busy now, retry"
    /// from "impossible, fail the job" — without this a job asking for more than the host has would
    /// sit in Waiting forever with no explanation.
    /// </summary>
    public static bool CanEverFit(ResourceTotals totals, ResourceRequest request) =>
        request.Cores <= totals.Cores &&
        request.MemoryGb <= totals.MemoryGb &&
        request.Gpus <= totals.Gpus;
}
