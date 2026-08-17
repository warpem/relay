using Refund.DataModel;

namespace Refund.JobQueues;

/// <summary>
/// Configuration rules for managed queues. Separate from DataManager so they can be tested without
/// standing up a repository.
/// </summary>
public static class ManagedQueueRules
{
    /// <summary>
    /// Refuses a second managed queue. They would share the host-wide executor while each declaring
    /// its own totals, so the host would be booked twice over and both would hand out device 0.
    /// The editor's "Copy current queue" button makes this a one-click mistake.
    /// </summary>
    public static void ValidateOnly(IEnumerable<ClusterQueue> existing, ClusterQueue candidate)
    {
        if (candidate is not { IsManaged: true })
            return;

        var other = existing.FirstOrDefault(q => q.IsManaged && !ReferenceEquals(q, candidate));

        if (other != null)
            throw new InvalidOperationException(
                $"\"{other.Alias}\" is already the managed queue for this host, and there can only " +
                "be one — a host has a single set of cores and GPUs. Edit that queue instead, or " +
                "switch it to another scheduler first.");
    }

    /// <summary>
    /// Refuses total changes while jobs are running, rather than defining what should happen when
    /// new totals fall below current usage.
    /// </summary>
    public static void ValidateTotalsChange(ClusterQueue queue, bool hasLiveEntries)
    {
        if (queue.IsManaged && hasLiveEntries)
            throw new InvalidOperationException(
                $"Queue \"{queue.Alias}\" has running jobs. Wait for them to finish, or abort them, " +
                "before changing its cores, memory or GPU count.");
    }
}
