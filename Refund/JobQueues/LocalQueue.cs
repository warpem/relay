using System.Runtime.CompilerServices;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues.ReadOnly;

namespace Refund.JobQueues;

public sealed class LocalQueue : JobQueue
{
    private static readonly ConditionalWeakTable<LocalQueue, ReadOnlyLocalQueue> ReadOnlyCache = new();

    public LocalQueue()
    {
        Id = -1;
        Alias = "Local Queue";
        QueueType = JobQueueType.Local;
    }

    public override ReadOnlyJobQueue AsReadOnly() =>
        ReadOnlyCache.GetValue(this, queue => new ReadOnlyLocalQueue(queue));
}
