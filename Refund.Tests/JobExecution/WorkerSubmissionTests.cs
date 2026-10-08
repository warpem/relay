using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobQueues;
using Refund.Jobs.Common.Notes.Note;

namespace Refund.Tests.JobExecution;

public class WorkerSubmissionTests
{
    [Fact]
    public async Task FailureBeforeCommandStartsIsDefiniteAndReplacementIsPlanned()
    {
        var queue = new ClusterQueue
        {
            SubmitJobTemplate = "unused",
            CustomShell = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-shell")
        };
        var (coordinator, attempt, start, operations) = Setup(queue);
        try
        {
            var error = await Assert.ThrowsAsync<ClusterCommandNotStartedException>(() =>
                operations.StartWorkerAsync(attempt.CreateSnapshot(), start.OperationId, CancellationToken.None));
            coordinator.WorkerStartFailed(attempt.Id, start.OperationId, error.Message);

            var replacement = Assert.IsType<StartWorker>(Assert.Single(coordinator.PlanEffects()));
            Assert.NotEqual(start.OperationId, replacement.OperationId);
            Assert.Equal(2, attempt.WorkerGroup.TotalSubmissions);
            Assert.Equal(1, attempt.WorkerGroup.AliveCount);
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("exit 1")]
    [InlineData("printf 'unexpected scheduler response'")]
    public async Task FailureAfterCommandStartsRemainsUncertain(string command)
    {
        var (_, attempt, start, operations) = Setup(new ClusterQueue { SubmitJobTemplate = command });
        try
        {
            await Assert.ThrowsAsync<IndeterminateBackendStartException>(() =>
                operations.StartWorkerAsync(attempt.CreateSnapshot(), start.OperationId, CancellationToken.None));
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); }
    }

    private static (ExecutionCoordinator Coordinator, ExecutionAttempt Attempt, StartWorker Start,
        RelayExecutionOperations Operations) Setup(ClusterQueue queue)
    {
        var configuration = new JsonObject();
        queue.WriteToJson(configuration);
        var coordinator = new ExecutionCoordinator([
            new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler,
                BackendConfiguration: configuration.ToJsonString())
        ]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 1), 1, ResourceVector.None, true,
            new WorkerGroupRequest(1, 1, 3));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), true);
        var start = Assert.IsType<StartWorker>(Assert.Single(coordinator.PlanEffects()));
        var job = new Note { Id = 1, Space = new Space { RootDirectory = Path.GetTempPath() } };
        var operations = new RelayExecutionOperations(_ => job, (updated, action) =>
        {
            action(updated);
            return Task.CompletedTask;
        });
        return (coordinator, attempt, start, operations);
    }
}
