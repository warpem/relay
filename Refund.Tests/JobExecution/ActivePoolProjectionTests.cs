using Refund.DataModel;
using Refund.JobExecution;
using Refund.Mcp;
using Refund.Services.Core.Repositories;
using ClassificationJob = Refund.Jobs.Refinement.Classes3D.Class3D.Class3D;

namespace Refund.Tests.JobExecution;

[Collection("JobRegistry")]
public sealed class ActivePoolProjectionTests
{
    [Fact]
    public void ResizingProjectsTheActiveTargetAndStoppingWorkersWithoutChangingRunParameters()
    {
        JobRegistry.EnsurePopulated();
        var coordinator = new ExecutionCoordinator([
            new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler)
        ]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 1), 4,
            ResourceVector.None, true, new WorkerGroupRequest(4, 3, 300));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), true);
        var starts = coordinator.PlanEffects().OfType<StartWorker>().ToArray();
        for (int i = 0; i < starts.Length; i++)
            coordinator.WorkerStarted(attempt.Id, starts[i].OperationId,
                new BackendReceipt($"worker-{i}"), isRunning: i != 1);
        var job = new ClassificationJob { UseWorkerPool = true, NWorkers = 3 };
        QueueRepository.ApplyProjection(job, attempt.CreateSnapshot());

        coordinator.ResizeWorkerGroup(attempt.Id, 2);
        var cancel = Assert.Single(coordinator.PlanEffects().OfType<CancelWorkers>());
        Assert.Equal("worker-2", Assert.Single(cancel.Receipts).Id);
        Assert.True(QueueRepository.ApplyProjection(job, attempt.CreateSnapshot()));
        Assert.False(QueueRepository.ApplyProjection(job, attempt.CreateSnapshot()));

        var dto = RelayMcpProjections.ToDetailDto(job.AsReadOnly());
        Assert.Equal(new JobPoolDto(2, 1, 1, 1, 3, true), dto.Pool);
        Assert.Equal(3, job.NWorkers);
        Assert.Equal(300, attempt.WorkerGroup.SubmissionLimit);
        Assert.Single(job.Events, item => item.Type == EventType.RunningStarted);
    }

    [Theory]
    [InlineData(ExecutionPhase.Stopping, true)]
    [InlineData(ExecutionPhase.Finalizing, true)]
    [InlineData(ExecutionPhase.Succeeded, false)]
    [InlineData(ExecutionPhase.Interrupted, false)]
    public void ResizeIsUnavailableAfterRunningAndTerminalProjectionClearsTheTarget(
        ExecutionPhase phase, bool hasPool)
    {
        JobRegistry.EnsurePopulated();
        var coordinator = new ExecutionCoordinator([
            new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler)
        ]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 1), 4,
            ResourceVector.None, true, new WorkerGroupRequest(4, 2, 200));
        var job = new ClassificationJob { UseWorkerPool = true, NWorkers = 4, PoolDesiredSize = 3 };

        QueueRepository.ApplyProjection(job, attempt.CreateSnapshot() with { Phase = phase });

        var pool = RelayMcpProjections.ToDetailDto(job.AsReadOnly()).Pool;
        Assert.Equal(hasPool, pool != null);
        Assert.False(pool?.CanResize ?? false);
        Assert.Equal(hasPool ? 2 : (int?)null, job.PoolDesiredSize);
        Assert.Equal(4, job.NWorkers);

        job.PoolWorkersStopping = 1;
        job.ClearProperties();
        Assert.Null(job.PoolDesiredSize);
        Assert.Equal(0, job.PoolWorkersStopping);
        Assert.Equal(4, job.NWorkers);
    }
}
