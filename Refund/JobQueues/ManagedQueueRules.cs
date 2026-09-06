using Refund.DataModel;

namespace Refund.JobQueues;

public static class ManagedQueueRules
{
    public static void ValidateOnly(
        IEnumerable<ClusterQueue> existing,
        ClusterQueue candidate)
    {
        if (!candidate.IsManaged)
            return;

        var other = existing.FirstOrDefault(queue =>
            queue.IsManaged && !ReferenceEquals(queue, candidate));
        if (other != null)
            throw new InvalidOperationException(
                $"Queue \"{other.Alias}\" already manages this host's resources.");
    }

    public static IReadOnlyList<ClusterQueue> DisableDuplicateManagedQueues(
        IEnumerable<ClusterQueue> queues)
    {
        var managed = queues.Where(queue => queue.IsManaged)
            .OrderBy(queue => queue.Id)
            .ToArray();

        foreach (var queue in managed)
            queue.ManagedDisabledReason = null;
        if (managed.Length < 2)
            return Array.Empty<ClusterQueue>();

        var disabled = managed.Skip(1).ToArray();
        foreach (var queue in disabled)
            queue.ManagedDisabledReason =
                $"Queue \"{managed[0].Alias}\" already manages this host's resources.";

        return disabled;
    }

    public static void ValidateChange(
        ClusterQueue queue,
        ClusterQueue proposed,
        IEnumerable<ClusterQueue> allQueues,
        bool hasActiveAttempts)
    {
        ValidateOnly(
            allQueues.Where(other => !ReferenceEquals(other, queue)),
            proposed);

        if (hasActiveAttempts && proposed.IsManaged != queue.IsManaged)
            throw new InvalidOperationException(
                $"Queue \"{queue.Alias}\" has active jobs and cannot switch between managed and external execution.");

        if (hasActiveAttempts && queue.IsManaged && proposed.IsManaged &&
            (proposed.ManagedCores != queue.ManagedCores ||
             proposed.ManagedMemoryGb != queue.ManagedMemoryGb ||
             proposed.ManagedGpus != queue.ManagedGpus))
            throw new InvalidOperationException(
                $"Queue \"{queue.Alias}\" has active jobs and its managed capacity cannot be changed.");
    }

    public static void ValidateDelete(ClusterQueue queue, bool hasActiveAttempts)
    {
        if (hasActiveAttempts)
            throw new InvalidOperationException(
                $"Queue \"{queue.Alias}\" has active jobs and cannot be deleted.");
    }
}
