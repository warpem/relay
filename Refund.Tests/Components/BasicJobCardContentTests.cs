using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Refund.Components.Jobs;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Notes.Note;
using Refund.Services;

namespace Refund.Tests.Components;

public sealed class BasicJobCardContentTests
{
    [Fact]
    public async Task RunningTailFollowsNewLogIterationsWithoutChangingJobIdentity()
    {
        var job = CreateJob();
        await using var card = new TestCard();
        await card.ShowAsync(job);
        card.AssertLastTail(job.PathStdOut, 3000);

        job.LogsAvailableIteration = 0;
        await card.ShowAsync(job);
        card.AssertLastTail(job.LogFilePath(0), 3000);

        job.LogsAvailableIteration = 1;
        await card.ShowAsync(job);
        card.AssertLastTail(job.LogFilePath(1), 3000);
        Assert.Equal(3, card.Initializations.Count);

        // Progress within one log is handled by its existing browser poller.
        job.UpdateDate = job.UpdateDate.AddSeconds(1);
        await card.ShowAsync(job);
        Assert.Equal(3, card.Initializations.Count);
    }

    [Fact]
    public async Task FinalizationStopsPollingAndRefreshesTheLastLog()
    {
        var job = CreateJob();
        job.LogsAvailableIteration = 2;
        await using var card = new TestCard();
        await card.ShowAsync(job);

        job.Status = JobStatus.Finalizing;
        await card.ShowAsync(job);
        Assert.Single(card.Initializations);

        job.Status = JobStatus.Finished;
        job.UpdateDate = job.UpdateDate.AddSeconds(1);
        await card.ShowAsync(job);
        card.AssertLastTail(job.LogFilePath(2), 0);
        Assert.Equal(2, card.Initializations.Count);

        // A later result/log update must trigger another one-shot fetch.
        job.UpdateDate = job.UpdateDate.AddSeconds(1);
        await card.ShowAsync(job);
        card.AssertLastTail(job.LogFilePath(2), 0);
        Assert.Equal(3, card.Initializations.Count);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Interrupted)]
    public async Task UnsuccessfulCompletionSwitchesFromStdoutToTheErrorLog(JobStatus outcome)
    {
        var job = CreateJob();
        await using var card = new TestCard();
        await card.ShowAsync(job);

        job.Status = outcome;
        job.UpdateDate = job.UpdateDate.AddSeconds(1);
        await card.ShowAsync(job);

        card.AssertLastTail(job.ErrorFilePath, 0);
        Assert.Equal(2, card.Initializations.Count);
        Assert.Single(card.Cleanups);
    }

    [Fact]
    public async Task AvailableVisualizationStopsTheLogTail()
    {
        var job = CreateJob();
        await using var card = new TestCard();
        await card.ShowAsync(job);

        job.VisAvailableIteration = 0;
        await card.ShowAsync(job);

        Assert.Single(card.Initializations);
        Assert.Single(card.Cleanups);
    }

    private static Note CreateJob() => new()
    {
        Id = 1,
        Space = new Space { RootDirectory = Path.Combine(Path.GetTempPath(), "relay-card-test") },
        Status = JobStatus.Running,
        UpdateDate = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class TestCard : BasicJobCardContent
    {
        private readonly RecordingJs _js = new();
        private readonly FileService _files = new(NullLogger<FileService>.Instance);
        private bool _firstRender = true;
        private Job? _currentJob;

        public TestCard()
        {
            typeof(BasicJobCardContent).GetProperty("JSRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, _js);
            typeof(BasicJobCardContent).GetProperty("FileService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, _files);
        }

        public List<object?[]> Initializations => _js.Initializations;
        public List<object?[]> Cleanups => _js.Cleanups;

        public async Task ShowAsync(Job job)
        {
            if (!ReferenceEquals(_currentJob, job))
            {
                _currentJob = job;
                Job = new TestReadOnlyJob(job);
            }
            OnParametersSet();
            await OnAfterRenderAsync(_firstRender);
            _firstRender = false;
        }

        public void AssertLastTail(string path, int interval)
        {
            object?[] call = Initializations.Last();
            Assert.Equal(_files.GetUrl(path), call[1]);
            Assert.Equal(interval, call[2]);
        }
    }

    private sealed class TestReadOnlyJob(Job job) : ReadOnlyJob(job);

    private sealed class RecordingJs : IJSRuntime, IJSObjectReference
    {
        public List<object?[]> Initializations { get; } = [];
        public List<object?[]> Cleanups { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
                return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "initializeFileTail")
                Initializations.Add(args!);
            else if (identifier == "cleanupFileTail")
                Cleanups.Add(args!);
            else
                throw new InvalidOperationException($"Unexpected JS call: {identifier}");
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
