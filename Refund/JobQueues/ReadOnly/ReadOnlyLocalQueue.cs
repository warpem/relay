using Refund.DataModel.ReadOnly;

namespace Refund.JobQueues.ReadOnly;

/// <summary>
/// Provides a read-only view of a LocalQueue.
/// Decorates a LocalQueue to prevent any modifications and expose only getters.
/// </summary>
[ReadOnlyFor(typeof(LocalQueue))]
public sealed class ReadOnlyLocalQueue : ReadOnlyJobQueue
{
    private readonly LocalQueue _queue;

    /// <summary>
    /// Initializes a new instance of the ReadOnlyLocalQueue class.
    /// </summary>
    /// <param name="queue">The LocalQueue to wrap</param>
    internal ReadOnlyLocalQueue(LocalQueue queue) : base(queue)
    {
        _queue = queue;
    }
}
