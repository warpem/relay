using System.Text.Json;
using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public class WorkerRetryTests
{
    private static readonly ExecutionQueuePolicy Queue = new(1, ExecutionBackendKind.ExternalScheduler);

    [Fact]
    public void UnknownWorkersAreReplacedAtDeadlineWithoutReplacingRunningWorkers()
    {
        var clock = new Clock();
        var (coordinator, attempt, starts) = Setup(clock, 30, 100);
        foreach (var start in starts.Take(26))
            coordinator.WorkerStarted(attempt.Id, start.OperationId, new BackendReceipt(start.OperationId.ToString()), true);
        foreach (var start in starts.Skip(26))
            coordinator.WorkerStartIndeterminate(attempt.Id, start.OperationId, "lost receipt");

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Empty(coordinator.PlanEffects());
        clock.Advance(TimeSpan.FromSeconds(1));
        var replacements = coordinator.PlanEffects().OfType<StartWorker>().ToArray();

        Assert.Equal(4, replacements.Length);
        Assert.All(replacements, replacement => Assert.DoesNotContain(starts, old => old.OperationId == replacement.OperationId));
        Assert.Equal(26, attempt.WorkerGroup.RunningCount);
        Assert.Equal(30, attempt.WorkerGroup.AliveCount);
        Assert.Equal(34, attempt.WorkerGroup.TotalSubmissions);
        Assert.DoesNotContain(attempt.WorkerGroup.Workers, worker => worker.Phase == WorkerPhase.Indeterminate);
        Assert.Equal(replacements, coordinator.PlanEffects().OfType<StartWorker>().ToArray());
    }

    [Fact]
    public void RestartPreservesRetryDeadlineAndLegacySnapshotsGetAGracePeriod()
    {
        var clock = new Clock();
        var (coordinator, attempt, starts) = Setup(clock, 1, 3);
        coordinator.WorkerStartIndeterminate(attempt.Id, starts[0].OperationId, "lost receipt");
        clock.Advance(TimeSpan.FromSeconds(90));

        var snapshot = JsonSerializer.Deserialize<ExecutionCoordinatorSnapshot>(
            JsonSerializer.Serialize(coordinator.CreateSnapshot()))!;
        var restored = new ExecutionCoordinator([Queue], clock);
        restored.Restore(snapshot);
        restored.Recover();
        Assert.Empty(restored.PlanEffects());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsType<StartWorker>(Assert.Single(restored.PlanEffects()));

        var legacy = snapshot with
        {
            Attempts = snapshot.Attempts.Select(item => item with
            {
                WorkerGroup = item.WorkerGroup with
                {
                    Workers = item.WorkerGroup.Workers.Select(worker => worker with { RetryAfter = null }).ToArray()
                }
            }).ToArray()
        };
        var legacyRestored = new ExecutionCoordinator([Queue], clock);
        legacyRestored.Restore(legacy);
        legacyRestored.Recover();
        Assert.Empty(legacyRestored.PlanEffects());
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsType<StartWorker>(Assert.Single(legacyRestored.PlanEffects()));
    }

    [Fact]
    public void RetryNeverExceedsSubmissionLimit()
    {
        var clock = new Clock();
        var (coordinator, attempt, starts) = Setup(clock, 1, 2);
        coordinator.WorkerStartIndeterminate(attempt.Id, starts[0].OperationId, "lost receipt");
        clock.Advance(TimeSpan.FromMinutes(2));
        var replacement = Assert.IsType<StartWorker>(Assert.Single(coordinator.PlanEffects()));
        coordinator.WorkerStartIndeterminate(attempt.Id, replacement.OperationId, "lost again");
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(0, attempt.WorkerGroup.AliveCount);
        Assert.Equal(2, attempt.WorkerGroup.TotalSubmissions);
    }

    [Fact]
    public void ReducedTargetDoesNotReplaceUnneededUnknownWorkers()
    {
        var clock = new Clock();
        var (coordinator, attempt, starts) = Setup(clock, 2, 3);
        coordinator.WorkerStartIndeterminate(attempt.Id, starts[0].OperationId, "lost receipt");
        coordinator.WorkerStarted(attempt.Id, starts[1].OperationId, new BackendReceipt("running"), true);
        coordinator.ResizeWorkerGroup(attempt.Id, 1);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(coordinator.PlanEffects());
        Assert.Equal(2, attempt.WorkerGroup.TotalSubmissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletingOrCancelingManagerDoesNotRetryWorkers(bool cancel)
    {
        var clock = new Clock();
        var (coordinator, attempt, starts) = Setup(clock, 1, 3);
        coordinator.WorkerStartIndeterminate(attempt.Id, starts[0].OperationId, "lost receipt");
        if (cancel)
            coordinator.RequestCancel(attempt.Id);
        else
            coordinator.Observe(attempt.Id, new BackendObservation(BackendObservationKind.Succeeded));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.DoesNotContain(coordinator.PlanEffects(), effect => effect is StartWorker);
    }

    private static (ExecutionCoordinator Coordinator, ExecutionAttempt Attempt, StartWorker[] Starts)
        Setup(Clock clock, int desired, int limit)
    {
        var coordinator = new ExecutionCoordinator([Queue], clock);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 1), 1, ResourceVector.None, true,
            new WorkerGroupRequest(1, desired, limit));
        coordinator.PreparationCompleted(attempt.Id);
        coordinator.PlanEffects();
        coordinator.StartCompleted(attempt.Id, new BackendReceipt("manager"), true);
        StartWorker[] starts;
        do { starts = coordinator.PlanEffects().OfType<StartWorker>().ToArray(); }
        while (starts.Length < desired);
        return (coordinator, attempt, starts);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
