using Refund.DataModel.ReadOnly;

namespace Refund.JobQueues.ReadOnly;

/// <summary>
/// Provides a read-only view of a LocalQueue.
/// Decorates a LocalQueue to prevent any modifications and expose only getters.
/// </summary>
/// <remarks>
/// This class is part of the read-only decorator implementation for the job queue system.
/// It is instantiated by the LocalQueue.AsReadOnly() method using a ConditionalWeakTable
/// to cache the read-only instances and prevent duplicate wrapper objects:
/// 
/// ```csharp
/// // In LocalQueue.cs:
/// private static readonly ConditionalWeakTable<LocalQueue, ReadOnlyLocalQueue> ReadOnlyCache = new();
/// 
/// public override ReadOnlyJobQueue AsReadOnly()
/// {
///     return ReadOnlyCache.GetValue(this, queue => new ReadOnlyLocalQueue(queue));
/// }
/// ```
/// 
/// The ReadOnlyLocalQueue is returned whenever a read-only view of a LocalQueue is needed,
/// particularly when providing queue references to UI components and external systems where
/// modifications should be prevented.
/// </remarks>
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
    
    // LocalQueue doesn't have additional properties beyond those in JobQueue,
    // so no additional property wrappers are needed
}