using Refund.DataModel;
using Refund.JobExecution;
using Refund.Jobs.Common.Notes.Note;

namespace Refund.Tests.JobExecution;

public class ResourceCountFinalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-counts-" + Guid.NewGuid().ToString("N"));

    private CountingJob CreateJob() => new()
    {
        Id = 1,
        Space = new Space { RootDirectory = _root },
        OutputItemCounts = new() { ["Old"] = 99 }
    };

    private static ExecutionAttemptSnapshot Attempt(ExecutionPurpose purpose = ExecutionPurpose.Run)
    {
        var coordinator = new ExecutionCoordinator([new ExecutionQueuePolicy(1, ExecutionBackendKind.Local)]);
        return coordinator.RequestRun(new JobAddress(1, 1, 1), 1, ResourceVector.None, true)
            .CreateSnapshot() with { Purpose = purpose };
    }

    private static RelayExecutionOperations Operations(CountingJob job) => new(
        _ => job,
        (updated, action) =>
        {
            job.InUpdate = true;
            try { action(updated); }
            finally { job.InUpdate = false; }
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(ExecutionPurpose.Run)]
    [InlineData(ExecutionPurpose.FinalizeOnly)]
    public async Task SuccessfulFinalization_CountsAfterResultsAndPersistsOutsideScan(ExecutionPurpose purpose)
    {
        var job = CreateJob();
        var operations = Operations(job);

        await operations.FinalizeAsync(Attempt(purpose), ExecutionOutcome.Succeeded, CancellationToken.None);

        Assert.Equal(1, job.CountCalls);
        Assert.True(job.ResultsReady);
        Assert.Equal(3_000_000_000L, job.OutputItemCounts["Particles"]);
        Assert.False(job.OutputItemCounts.ContainsKey("Old"));
        await operations.ShutdownAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(ExecutionOutcome.Failed)]
    [InlineData(ExecutionOutcome.Canceled)]
    [InlineData(ExecutionOutcome.Interrupted)]
    public async Task UnsuccessfulExecution_DoesNotScanPartialOutput(ExecutionOutcome outcome)
    {
        var job = CreateJob();
        var operations = Operations(job);

        await operations.FinalizeAsync(Attempt(), outcome, CancellationToken.None);

        Assert.Equal(0, job.CountCalls);
        await operations.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Preparation_DiscardsCountsBeforeStagingANewRun()
    {
        var job = CreateJob();
        var operations = Operations(job);

        await operations.PrepareAsync(Attempt(), CancellationToken.None);

        Assert.True(job.Staged);
        Assert.Empty(job.OutputItemCounts);
        await operations.ShutdownAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class CountingJob : Note
    {
        public bool InUpdate { get; set; }
        public bool ResultsReady { get; set; }
        public bool Staged { get; set; }
        public int CountCalls { get; set; }

        public override void Stage()
        {
            Assert.Empty(OutputItemCounts);
            Staged = true;
        }

        public override Action? TrackProgressLogs() => null;

        public override Action? TrackProgressResults() => ResultsReady ? null : () => ResultsReady = true;

        public override void FinalizeRun(Action<Job, Action<Job>> updateCallback) =>
            updateCallback(this, _ => ResultsReady = true);

        public override Dictionary<string, long> CountOutputItems(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(InUpdate);
            Assert.True(ResultsReady);
            CountCalls++;
            return new() { ["Particles"] = 3_000_000_000L };
        }
    }
}
