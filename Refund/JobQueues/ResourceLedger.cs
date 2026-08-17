namespace Refund.JobQueues;

/// <summary>Everything a managed queue may hand out.</summary>
public readonly record struct ResourceTotals(int Cores, int MemoryGb, int Gpus);

/// <summary>What one job is asking for.</summary>
public readonly record struct ResourceRequest(int Cores, int MemoryGb, int Gpus);

/// <summary>What one job was given. GpuIndices are host device ids, not job-relative.</summary>
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

        var freeGpus = Enumerable.Range(0, totals.Gpus).Where(g => !takenGpus.Contains(g)).ToList();

        return new LedgerSnapshot(totals.Cores - usedCores, totals.MemoryGb - usedMemory, freeGpus);
    }

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
            request.Cores, request.MemoryGb, snap.FreeGpuIndices.Take(request.Gpus).ToList());

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
