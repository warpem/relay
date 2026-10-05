using System.Text.Json;
using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobResources;
using Refund.Jobs.Ts.Reconstruction.ReconstructTomograms;

namespace Refund.Tests.Jobs;

public class ReconstructTomogramsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-reconstruct-" + Guid.NewGuid().ToString("N"));

    private RenderingJob CreateJob(int count = 2)
    {
        var job = new RenderingJob { Id = 18, Space = new Space { RootDirectory = _root } };
        var input = job.PortsIn[ReconstructTomograms.PortInTiltSeriesSet];
        input.Edges.Add(new Edge
        {
            Source = new PortOut(job, typeof(TiltSeriesSet), "source", "source", _ => new TiltSeriesSet()),
            Target = input
        });
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        Directory.CreateDirectory(Path.Combine(job.DirectoryPath, "reconstruction"));
        File.WriteAllText(job.ResProcessedItemsJson, JsonSerializer.Serialize(
            Enumerable.Range(1, count).Select(i => new { Path = $"TS_{i:00}.tomostar" })));
        for (int i = 1; i <= count; i++)
            File.WriteAllText(Thumbnail(job, i), "thumbnail");
        return job;
    }

    private static string Thumbnail(ReconstructTomograms job, int i) =>
        Path.Combine(job.DirectoryPath, "reconstruction", $"TS_{i:00}_10.00Apx.png");

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Finalization_GeneratesCardFromResultsWithoutConsoleProgress(int? reportedCount)
    {
        var job = CreateJob(10);
        job.NItemsProcessed = reportedCount;
        var coordinator = new ExecutionCoordinator([new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler)]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 18), 1, ResourceVector.None, true).CreateSnapshot();
        var operations = new RelayExecutionOperations(_ => job, (updated, action) =>
        {
            action(updated);
            return Task.CompletedTask;
        });
        try
        {
            await operations.FinalizeAsync(attempt, ExecutionOutcome.Succeeded, CancellationToken.None);

            Assert.Equal(10, job.OutputItemCounts[ReconstructTomograms.PortOutTomogramSet]);
            Assert.Equal(1, job.RenderCalls);
            Assert.Equal((Thumbnail(job, 1), Thumbnail(job, 2)), job.RenderedThumbnails);
            Assert.Equal(0, job.VisAvailableIteration);
            Assert.True(File.Exists(job.VisCard(0)));
            Assert.Null(job.TrackProgressResults());
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); }
    }

    [Fact]
    public void ExistingCard_RestoresAvailabilityWithoutRendering()
    {
        var job = CreateJob();
        File.WriteAllText(job.VisCard(0), "existing card");

        var update = job.TrackProgressResults();

        Assert.NotNull(update);
        update();
        Assert.Equal(0, job.VisAvailableIteration);
        Assert.Equal(0, job.RenderCalls);
    }

    [Fact]
    public void OneSuccessfulTomogram_UsesItsThumbnailForBothSlots()
    {
        var job = CreateJob(1);
        job.NItemsProcessed = 2;
        job.NItemsFailed = 1;

        var update = job.TrackProgressResults();

        Assert.NotNull(update);
        update();
        Assert.Equal((Thumbnail(job, 1), Thumbnail(job, 1)), job.RenderedThumbnails);
        Assert.Equal(0, job.VisAvailableIteration);
    }

    [Fact]
    public void MissingThumbnail_DefersRenderingUntilItExists()
    {
        var job = CreateJob();
        job.NItemsProcessed = 2;
        File.Delete(Thumbnail(job, 2));

        Assert.Null(job.TrackProgressResults());
        Assert.Equal(0, job.RenderCalls);
        Assert.Equal(-1, job.VisAvailableIteration);

        File.WriteAllText(Thumbnail(job, 2), "thumbnail");
        var update = job.TrackProgressResults();
        Assert.NotNull(update);
        update();
        Assert.Equal(0, job.VisAvailableIteration);
    }

    [Fact]
    public void ConsoleProgress_ParsesCompletedReconstructionOutput()
    {
        var job = CreateJob(10);
        File.WriteAllText(job.PathStdOut,
            "0/10\r10/10, previous metadata found for 10\n" +
            "Distributing 10 item(s) across up to 2 local worker(s)...\n" +
            "0/10\r1/10, 00:49 remaining\r10/10, 00:00 remaining\n" +
            "Finished processing in 00:00:17\nFinished: 10 processed, 0 failed\n");

        var update = job.TrackProgressLogs();

        Assert.NotNull(update);
        update();
        Assert.Equal(10, job.NItemsProcessed);
        Assert.Equal(10, job.NItemsTotal);
        Assert.Equal(0, job.NItemsFailed);
        Assert.Equal(0, job.LogsAvailableIteration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public void MissingOrEmptyResults_DoNotPublishACard(string? manifest)
    {
        var job = CreateJob(0);
        if (manifest == null)
            File.Delete(job.ResProcessedItemsJson);
        else
            File.WriteAllText(job.ResProcessedItemsJson, manifest);

        Assert.Null(job.TrackProgressResults());
        Assert.Equal(0, job.RenderCalls);
        Assert.Equal(-1, job.VisAvailableIteration);
    }

    private sealed class RenderingJob : ReconstructTomograms
    {
        public int RenderCalls { get; private set; }
        public (string, string) RenderedThumbnails { get; private set; }

        public override Action TrackProgressResults() => base.TrackProgressResults((first, second, output) =>
        {
            RenderCalls++;
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            RenderedThumbnails = (first, second);
            File.WriteAllText(output, "rendered card");
        });
    }

    public void Dispose() => Directory.Delete(_root, true);
}
