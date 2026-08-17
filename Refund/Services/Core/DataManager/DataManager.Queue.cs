using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new cluster queue for executing jobs on a remote computing resource.
    /// </summary>
    /// <param name="template">Optional template queue to copy properties from</param>
    /// <returns>A read-only wrapper of the created cluster queue</returns>
    /// <exception cref="Exception">Thrown if queue creation fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// It creates a new cluster queue with a unique ID, optionally copying properties from a template,
    /// and raises events to notify subscribers about the new queue.
    ///
    /// A cluster queue represents a connection to a remote computing resource, such as an HPC cluster
    /// or cloud compute environment. The queue configuration includes connection details (hostname,
    /// credentials), scheduler parameters (queue name, partition, resource limits), and submission
    /// templates for translating Relay job parameters into scheduler-specific job scripts.
    ///
    /// Unlike other creation methods, this one doesn't require a user parameter, as it's typically
    /// called during system initialization or by administrators.
    /// </remarks>
    public async Task<ReadOnlyJobQueue> CreateClusterQueue(ClusterQueue template = null)
    {
        ReadOnlyJobQueue createdQueue = null;

        await ExecuteWithLock(async () =>
        {
            try
            {
                // The new queue adopts the template's state, so the template is what has to pass
                // the single-managed-queue rule. Nothing has been added to the repository yet.
                ManagedQueueRules.ValidateOnly(MutableClusterQueues(), template);

                JobQueue newQueue = _queueRepository.CreateClusterQueue(template);
                createdQueue = newQueue.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create cluster queue from template");

                throw;
            }
        });

        // Raise events outside of lock
        await QueueCreated.InvokeHierarchy(createdQueue, GroupName.QueueHierarchy(null));

        return createdQueue;
    }

    /// <summary>
    /// Updates an existing job queue by applying the specified update action.
    /// </summary>
    /// <param name="queue">The queue to update</param>
    /// <param name="updateAction">The action to apply to the queue</param>
    /// <returns>A read-only wrapper of the updated queue</returns>
    /// <exception cref="Exception">Thrown if the queue cannot be found or if update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable queue object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    ///
    /// Queue updates can involve modifying connection details, scheduler parameters, or
    /// submission templates. These updates affect how jobs are submitted to and monitored on
    /// remote computing resources. For local queues, updates typically involve changing
    /// resource limits or execution priorities.
    ///
    /// Unlike other update methods, this one doesn't require a user parameter, as it's typically
    /// called during system maintenance or by administrators.
    /// </remarks>
    public async Task<ReadOnlyJobQueue> UpdateQueue(ReadOnlyJobQueue queue, Action<JobQueue> updateAction)
    {
        ReadOnlyJobQueue updatedQueue = null;

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalQueue = ResolveQueue(queue.Id);

                if (originalQueue is ClusterQueue cluster)
                    ValidateManagedQueueChange(cluster, updateAction);

                _queueRepository.UpdateQueue(originalQueue, updateAction);
                updatedQueue = originalQueue.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update queue {QueueId}", queue.Id);

                throw;
            }
        });

        // Raise events outside of lock
        await QueueUpdated.InvokeHierarchy(updatedQueue, GroupName.QueueHierarchy(queue.Id));

        return updatedQueue;
    }

    /// <summary>
    /// Deletes an existing cluster queue from the system.
    /// </summary>
    /// <param name="queue">The queue to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if the queue cannot be found, is the local queue, or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the logical deletion in the data model and dispatching the appropriate events.
    /// The deletion occurs within a lock to ensure consistency. After deletion, events are raised to notify
    /// all interested subscribers.
    ///
    /// Only cluster queues can be deleted; the local queue is a permanent part of the system and cannot be removed.
    /// This is enforced by checking the queue ID (local queue always has ID -1) and rejecting attempts to delete it.
    ///
    /// When a cluster queue is deleted, any jobs that were previously submitted to that queue but haven't completed
    /// will be left in an indeterminate state. These jobs should be marked as failed or re-submitted to another queue.
    ///
    /// Unlike other deletion methods, this one doesn't require a user parameter, as it's typically
    /// called during system maintenance or by administrators.
    /// </remarks>
    public async Task DeleteQueue(ReadOnlyJobQueue queue)
    {
        ReadOnlyJobQueue deletedQueue = null;

        await ExecuteWithLock(async () =>
        {
            try
            {
                if (queue.Id == -1)
                    throw new Exception("Cannot delete local queue");

                var originalQueue = (ClusterQueue)ResolveQueue(queue.Id);

                // Before anything is removed: a managed queue that still owns compute must not go
                // away, or its jobs stop being polled while their processes keep the host's cores
                // and GPUs booked with nothing left to release them.
                ManagedQueueRules.ValidateDelete(originalQueue, HasLiveEntries(originalQueue));

                deletedQueue = originalQueue.AsReadOnly();
                _queueRepository.DeleteClusterQueue(originalQueue);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete queue {QueueId}", queue.Id);

                throw;
            }
        });

        // Raise events outside of lock
        await QueueDeleted.InvokeHierarchy(deletedQueue, GroupName.QueueHierarchy(queue.Id));
    }

    /// <summary>
    /// Moves a cluster queue to a specific position in the order of available queues.
    /// </summary>
    /// <param name="queue">The queue to move</param>
    /// <param name="newPosition">The zero-based position to move the queue to</param>
    /// <returns>A task that completes when the move operation is finished</returns>
    /// <exception cref="ArgumentNullException">Thrown if queue is null</exception>
    /// <exception cref="Exception">Thrown if the queue cannot be found or if move fails</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if newPosition is negative or exceeds the number of cluster queues</exception>
    /// <remarks>
    /// This method changes the order in which cluster queues are displayed in the user interface.
    /// Queue order can be important for usability, allowing frequently used queues to appear
    /// at the top of the list, making them easier to select.
    ///
    /// The queue ordering is maintained as a separate property of the queue repository,
    /// not as part of the individual queue objects. This allows the same ordering to be
    /// applied consistently across all users of the system.
    ///
    /// After reordering, events are raised to notify subscribers about the queue update.
    /// These events cause UI components to refresh their queue lists with the new ordering.
    /// </remarks>
    public async Task MoveQueueToPosition(ReadOnlyJobQueue queue, int newPosition)
    {
        if (queue == null)
            throw new ArgumentNullException(nameof(queue));

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalQueue = ResolveQueue(queue.Id);

                if (newPosition < 0 || newPosition >= _queueRepository.ClusterQueues.Count)
                    throw new ArgumentOutOfRangeException(nameof(newPosition), "New position is out of range");

                _queueRepository.ReorderClusterQueue(originalQueue, newPosition);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to move queue {QueueId} to position {Position}", queue.Id, newPosition);

                throw;
            }
        });

        // Raise events outside of lock
        await QueueUpdated.InvokeHierarchy(queue, GroupName.QueueHierarchy(queue.Id));
    }

    #endregion

    #region Managed queue rules

    /// <summary>
    /// The repository's mutable cluster queues. <see cref="Repositories.QueueRepository.ClusterQueues"/>
    /// hands back a read-only <i>collection</i> of the live objects, not read-only wrappers, so no extra
    /// accessor is needed — only the cast, since the list is typed as <see cref="JobQueue"/>.
    /// </summary>
    private IEnumerable<ClusterQueue> MutableClusterQueues() =>
        _queueRepository.ClusterQueues.OfType<ClusterQueue>();

    /// <summary>
    /// Whether the host's executor still holds anything belonging to <paramref name="queue"/> — a
    /// reservation or a live process, whether or not the job is still in the queue's own list.
    /// </summary>
    private bool HasLiveEntries(ClusterQueue queue) =>
        _queueRepository.ManagedExecutor.HasEntries(j => j.QueueId == queue.Id);

    /// <summary>
    /// Judges a proposed edit before the real queue is touched. The rule itself lives in
    /// <see cref="ManagedQueueRules.ValidateChange"/> so it can be tested without a repository;
    /// what belongs here is only where the two facts it needs come from.
    /// </summary>
    private void ValidateManagedQueueChange(ClusterQueue cluster, Action<JobQueue> updateAction) =>
        ManagedQueueRules.ValidateChange(cluster, updateAction,
                                         MutableClusterQueues(), () => HasLiveEntries(cluster));

    #endregion
}
