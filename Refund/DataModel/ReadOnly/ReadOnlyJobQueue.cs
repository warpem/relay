using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the JobQueue class, providing immutable access to job queue data.
/// Job queues manage the execution of jobs, providing ordering and resource allocation.
/// </summary>
public class ReadOnlyJobQueue : IIdentifiable
{
    /// <summary>
    /// The wrapped mutable job queue instance.
    /// </summary>
    private readonly JobQueue _queue;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyJobQueue"/> class.
    /// </summary>
    /// <param name="queue">The mutable job queue to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the queue parameter is null.</exception>
    internal ReadOnlyJobQueue(JobQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    /// <summary>
    /// Gets the unique identifier for this job queue.
    /// </summary>
    public int Id => _queue.Id;
    
    /// <summary>
    /// Gets the user-defined display name of this job queue.
    /// </summary>
    public string Alias => _queue.Alias;
    
    /// <summary>
    /// Gets a fully qualified name that combines the ID and alias.
    /// This provides a unique, human-readable identifier for UI display.
    /// </summary>
    public string QualifiedName => _queue.QualifiedName;
    
    /// <summary>
    /// Gets the type of this queue (Local, Cluster, etc.).
    /// Different queue types have different execution environments and capabilities.
    /// </summary>
    public JobQueueType QueueType => _queue.QueueType;
    
    /// <summary>
    /// Gets a read-only collection of jobs currently in this queue.
    /// The jobs are ordered according to the queue's scheduling policy.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyJob> QueuedJobs => new(_queue.QueuedJobs.Select(j => j.AsReadOnly()).ToList());
    
    /// <summary>
    /// Gets a value indicating whether this queue is empty.
    /// A queue is empty when it contains no jobs.
    /// </summary>
    public bool IsEmpty => _queue.IsEmpty;
    
    /// <summary>
    /// Converts this job queue to a JSON representation.
    /// </summary>
    /// <returns>A JSON node containing the serialized job queue data.</returns>
    public JsonNode ToJson() => _queue.ToJson();
}