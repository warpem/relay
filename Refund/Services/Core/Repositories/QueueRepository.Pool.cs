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
    /// <summary>
    /// Gets the existing WorkerPool for a job, or creates and initializes one.
    /// Assumes pool-queue validity was already checked in HandleWaitingState.
    /// </summary>
    private WorkerPool GetOrCreatePool(Job job)
    {
        return _workerPools.GetOrAdd(job, j =>
        {
            var pooledJob = (IPooledJob)j;
            var poolQueue = FindQueue(pooledJob.PoolQueueId) as ClusterQueue
                            ?? throw new InvalidOperationException(
                                $"Pool queue {pooledJob.PoolQueueId} not found for job {j.Id}.");
            var pool = new WorkerPool(poolQueue, pooledJob);
            pool.Initialize();
            return pool;
        });
    }

    /// <summary>
    /// Re-adopts worker pools for pooled jobs that were Running or Staging at shutdown.
    /// Called from LoadQueues after all jobs are restored. The first Tick reconciles
    /// the live worker set from the scheduler; pool_state.json supplies prior submitted IDs.
    /// </summary>
    private void ReAdoptPools(IEnumerable<Job> jobs)
    {
        foreach (var job in jobs)
        {
            if (job is not IPooledJob pooledJob || pooledJob.PoolQueueId <= 0)
                continue;
            if (job.Status is not (JobStatus.Running or JobStatus.Staging))
                continue;

            if (FindQueue(pooledJob.PoolQueueId) is not ClusterQueue poolQueue)
                continue;

            var pool = new WorkerPool(poolQueue, pooledJob);
            pool.Initialize();   // loads pool_state.json if present
            _workerPools[job] = pool;

            _logger.Information("Re-adopted worker pool for job {JobId} from disk", job.Id);
        }
    }
}
