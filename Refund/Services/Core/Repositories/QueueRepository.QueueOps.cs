using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues;
using Refund.Jobs;
using Refund.Utils;
using Timer = System.Threading.Timer;

namespace Refund.Services.Core.Repositories;

public partial class QueueRepository
{
    #region Queue operations

    /// <summary>
    /// Creates a new cluster queue based on an optional template.
    /// </summary>
    public JobQueue CreateClusterQueue(ClusterQueue template = null)
    {
        lock (_saveLock)
        {
            var queue = new ClusterQueue(_jobUpdateCallback);

            if (template != null)
                queue.AdoptState(template);

            queue.Id = _clusterQueues.Select(q => q.Id).DefaultIfEmpty(0).Max() + 1;

            // The host-wide executor. Attached to every cluster queue, not only the managed ones:
            // the scheduler type is editable afterwards, and a managed queue without an executor
            // rejects every job it is handed.
            queue.Executor = ManagedExecutor;

            _clusterQueues.Add(queue);

            _needsSaving = true;

            _logger.Information("Cluster queue {QueueId} successfully created (alias: {QueueAlias})",
                queue.Id, queue.Alias);

            return queue;
        }
    }

    /// <summary>
    /// Updates an existing queue with the specified action.
    /// </summary>
    public void UpdateQueue(JobQueue queue, Action<JobQueue> updateAction)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            updateAction(queue);

            // Recomputed after every edit, not only at load. Switching the *other* managed queue to
            // a different scheduler is exactly how a user resolves the duplication, and the queue
            // that was disabled must come back without a restart.
            ManagedQueueRules.DisableDuplicateManagedQueues(_clusterQueues.OfType<ClusterQueue>());

            _needsSaving = true;
        }

        _logger.Information("Queue {QueueId} successfully updated (alias: {QueueAlias})",
            queue.Id, queue.Alias);
    }

    /// <summary>
    /// Deletes a cluster queue.
    /// </summary>
    public void DeleteClusterQueue(ClusterQueue queue)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            _clusterQueues.Remove(queue);

            _needsSaving = true;
        }

        _logger.Information("Cluster queue {QueueId} successfully deleted (alias: {QueueAlias})",
            queue.Id, queue.Alias);
    }

    /// <summary>
    /// Moves a queue to a specific position in the cluster queues list.
    /// </summary>
    /// <param name="queue">The queue to move</param>
    /// <param name="newPosition">The new position for the queue</param>
    internal void ReorderClusterQueue(JobQueue queue, int newPosition)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            if (!_clusterQueues.Contains(queue))
                throw new Exception($"Queue {queue.Id} not found");

            if (newPosition < 0 || newPosition >= _clusterQueues.Count)
                throw new ArgumentOutOfRangeException(nameof(newPosition), "New position is out of range");

            int currentPosition = _clusterQueues.IndexOf(queue);

            // Remove from current position
            _clusterQueues.RemoveAt(currentPosition);

            // Adjust the target position if it was after the removal point
            // When we remove an item at position X, all subsequent positions shift down by 1
            // So if we want to insert at position Y where Y > X, we need to decrease Y by 1
            int adjustedPosition = newPosition > currentPosition ? newPosition : newPosition;

            // Insert at the adjusted position
            _clusterQueues.Insert(adjustedPosition, queue);

            _needsSaving = true;

            _logger.Information("Cluster queue {QueueId} successfully reordered from position {OldPosition} to {NewPosition} (alias: {QueueAlias})",
                queue.Id, currentPosition, adjustedPosition, queue.Alias);
        }
    }

    /// <summary>
    /// Queues a job in the local queue.
    /// </summary>
    public void QueueLocalJob(Job job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            _localQueue.Enqueue(job);
            _needsSaving = true;
        }

        _logger.Information("Job {JobId} successfully queued in local queue (alias: {JobAlias})",
            job.Id, job.Alias);
    }

    /// <summary>
    /// Queues a job in a cluster queue.
    /// </summary>
    public void QueueClusterJob(Job job, JobQueue queue)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            queue.Enqueue(job);
            _needsSaving = true;
        }

        _logger.Information("Job {JobId} successfully queued in cluster queue {QueueId} (job alias: {JobAlias}, queue alias: {QueueAlias})",
            job.Id, queue.Id, job.Alias, queue.Alias);
    }

    /// <summary>
    /// Removes a job from the local queue.
    /// </summary>
    public void DequeueLocalJob(Job job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        lock (_saveLock)
        {
            _localQueue.Dequeue(job);
            _needsSaving = true;
        }

        _logger.Information("Job {JobId} successfully dequeued from local queue (alias: {JobAlias})",
            job.Id, job.Alias);
    }

    /// <summary>
    /// Removes a job from a cluster queue.
    /// </summary>
    public void DequeueClusterJob(Job job, JobQueue queue)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (queue == null) throw new ArgumentNullException(nameof(queue));

        lock (_saveLock)
        {
            queue.Dequeue(job);
            _needsSaving = true;
        }

        _logger.Information("Job {JobId} successfully dequeued from cluster queue {QueueId} (job alias: {JobAlias}, queue alias: {QueueAlias})",
            job.Id, queue.Id, job.Alias, queue.Alias);
    }

    /// <summary>
    /// Finds a queue by its ID.
    /// </summary>
    public JobQueue FindQueue(int id)
    {
        return id == -1 ? _localQueue : _clusterQueues.FirstOrDefault(q => q.Id == id);
    }

    #endregion
}
