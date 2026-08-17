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

    /// <summary>
    /// Refuses deletion of a managed queue that still has something on the host.
    /// </summary>
    /// <remarks>
    /// Deleting one is worse than editing its totals. The daemon stops polling its jobs, so they
    /// stay Running forever; the executor keeps their entries alive because their status says they
    /// are active, so their cores and GPUs are never released; and nothing stops the user creating
    /// a fresh managed queue declaring the host's full totals a moment later — the very edit
    /// <see cref="ValidateTotalsChange"/> refuses, reached in two clicks with the leaked GPUs now
    /// invisible.
    /// <para>
    /// The editor already disables the delete button for a non-empty queue, but an entry outlives
    /// its job's membership of the queue: <c>HandleAbortingState</c> dequeues a job after 30
    /// seconds whether or not the kill landed, and the executor deliberately holds the allocation
    /// until the process actually exits. So an empty queue can still own live compute.
    /// </para>
    /// </remarks>
    public static void ValidateDelete(ClusterQueue queue, bool hasLiveEntries)
    {
        if (queue.IsManaged && hasLiveEntries)
            throw new InvalidOperationException(
                $"Queue \"{queue.Alias}\" still has jobs on this host. Wait for them to finish, or " +
                "abort them, before deleting it — deleting it now would leave their processes " +
                "running with nothing tracking the cores and GPUs they hold.");
    }
}
