using Refund.DataModel;
using Refund.JobExecution;
using Refund.Jobs.Common.Notes.Note;
using Refund.Services.Core.Repositories;

namespace Refund.Tests.JobExecution;

public sealed class JobProjectionTests
{
    [Fact]
    public void RepeatedPendingProjectionAddsOnlyOneStagingEvent()
    {
        var coordinator = new ExecutionCoordinator([
            new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler)
        ]);
        var attempt = coordinator.RequestRun(
            new JobAddress(1, 1, 1),
            4,
            ResourceVector.None,
            dependenciesReady: true);
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(
            attempt.Id,
            new BackendReceipt("scheduler-42"),
            isRunning: false);
        var snapshot = attempt.CreateSnapshot();
        var job = new Note { Status = JobStatus.Waiting };

        Assert.True(QueueRepository.ApplyProjection(job, snapshot));
        Assert.False(QueueRepository.ApplyProjection(job, snapshot));

        Assert.Equal(JobStatus.Staging, job.Status);
        Assert.Equal("scheduler-42", job.ClusterJobId);
        Assert.Single(job.Events, item => item.Type == EventType.StagingStarted);
    }

    [Fact]
    public void HistoricalTerminalAttemptDoesNotResurrectAClearedJob()
    {
        var coordinator = new ExecutionCoordinator([
            new ExecutionQueuePolicy(4, ExecutionBackendKind.ExternalScheduler)
        ]);
        var attempt = coordinator.RequestRun(
            new JobAddress(1, 1, 1),
            4,
            ResourceVector.None,
            dependenciesReady: true);
        coordinator.PreparationFailed(attempt.Id, "invalid input");
        var job = new Note { Status = JobStatus.Building };

        Assert.False(QueueRepository.ApplyProjection(job, attempt.CreateSnapshot()));
        Assert.Equal(JobStatus.Building, job.Status);
        Assert.DoesNotContain(job.Events, item => item.Type == EventType.Failed);
    }
}
