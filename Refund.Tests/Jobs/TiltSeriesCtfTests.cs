using System.Globalization;
using System.Text.Json;
using Warp;
using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobResources;
using Refund.Jobs.Ts.Ctf;

namespace Refund.Tests.Jobs;

public class TiltSeriesCtfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-ts-ctf-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1234.5", 1234.5)]
    [InlineData(null, 0)]
    public void WarpMetadata_ProvidesThicknessInAngstrom(string? thickness, double expected)
    {
        Directory.CreateDirectory(_root);
        string attribute = thickness == null ? "" : $"CTFSpecimenThicknessAngstrom=\"{thickness}\"";
        File.WriteAllText(Path.Combine(_root, "TS_01.xml"),
            $"<TiltSeries {attribute}><Angles>-30\n30</Angles></TiltSeries>");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var series = new TiltSeries(Path.Combine(_root, "TS_01.tomostar"));
            Assert.Equal((decimal)expected, series.CTFSpecimenThicknessAngstrom);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private RenderingJob CreateJob()
    {
        var job = new RenderingJob { Id = 25, Space = new Space { RootDirectory = _root } };
        var resource = new TiltSeriesSet
        {
            DataSet = new DataSetTs
            {
                Micrographs = new MicrographSet
                {
                    ToThumbnailPath = name => Path.Combine(_root, name + ".png")
                }
            }
        };
        var input = job.PortsIn[Ctf.PortInTiltSeriesSet];
        input.Edges.Add(new Edge
        {
            Source = new PortOut(job, typeof(TiltSeriesSet), "source", "source", _ => resource),
            Target = input
        });
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        File.WriteAllText(job.ResProcessedItemsJson, JsonSerializer.Serialize(new[]
        {
            new { Path = "TS_01.tomostar", Tilts = new[] { "tilt_0", "tilt_1", "tilt_2" } }
        }));
        File.WriteAllText(Path.Combine(job.DirectoryPath, "TS_01.xml"), "<TiltSeries />");
        File.WriteAllText(Path.Combine(_root, "tilt_1.png"), "thumbnail");
        return job;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Finalization_GeneratesSingleSeriesCardWithOrWithoutConsoleProgress(bool hasLog)
    {
        var job = CreateJob();
        if (hasLog)
            File.WriteAllText(job.PathStdOut,
                "0/1\r1/1, previous metadata found for 1\n" +
                "Estimating the CTF for 5 tilt series to check handedness...\n" +
                "0/1\r1/1, 00:00 remaining\nFinished: 1 processed, 0 failed\n" +
                "Checking defocus handedness...\n0/1\r1/1, 0.998\nAverage correlation: 0.998\n" +
                "Now running CTF estimation for all tilt series...\n" +
                "0/1\r1/1, 00:00 remaining\nFinished: 1 processed, 0 failed\nSaving settings... Done\n");
        var coordinator = new ExecutionCoordinator([new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler)]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 25), 1, ResourceVector.None, true).CreateSnapshot();
        var operations = new RelayExecutionOperations(_ => job, (updated, action) =>
        {
            action(updated);
            return Task.CompletedTask;
        });
        try
        {
            await operations.FinalizeAsync(attempt, ExecutionOutcome.Succeeded, CancellationToken.None);
            Assert.Equal(hasLog ? 1 : (int?)null, job.NItemsProcessed);
            Assert.Equal(hasLog ? 0 : -1, job.LogsAvailableIteration);
            Assert.Equal(1, job.OutputItemCounts[Ctf.PortOutTiltSeriesSet]);
            Assert.Equal(1, job.RenderCalls);
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

    [Theory]
    [InlineData("thumbnail")]
    [InlineData("metadata")]
    public void MissingArtifact_DefersUntilAvailable(string artifact)
    {
        var job = CreateJob();
        var path = artifact == "thumbnail" ? Path.Combine(_root, "tilt_1.png") : Path.Combine(job.DirectoryPath, "TS_01.xml");
        var contents = File.ReadAllText(path);
        File.Delete(path);
        Assert.Null(job.TrackProgressResults());
        Assert.Equal(0, job.RenderCalls);
        Assert.Equal(-1, job.VisAvailableIteration);
        File.WriteAllText(path, contents);
        var update = job.TrackProgressResults();
        Assert.NotNull(update);
        update();
        Assert.Equal(0, job.VisAvailableIteration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("[{\"Path\":\"TS_01.tomostar\",\"Tilts\":[]}]")]
    public void MissingOrEmptyResults_DoNotPublishCard(string? manifest)
    {
        var job = CreateJob();
        if (manifest == null) File.Delete(job.ResProcessedItemsJson);
        else File.WriteAllText(job.ResProcessedItemsJson, manifest);
        Assert.Null(job.TrackProgressResults());
        Assert.Equal(0, job.RenderCalls);
        Assert.Equal(-1, job.VisAvailableIteration);
    }

    private sealed class RenderingJob : Ctf
    {
        public int RenderCalls { get; private set; }
        public override Action TrackProgressResults() => base.TrackProgressResults((thumbnail, metadata, output) =>
        {
            Assert.EndsWith("tilt_1.png", thumbnail);
            Assert.EndsWith("TS_01.xml", metadata);
            Assert.True(File.Exists(thumbnail));
            Assert.True(File.Exists(metadata));
            RenderCalls++;
            File.WriteAllText(output, "rendered card");
        });
    }

    public void Dispose() => Directory.Delete(_root, true);
}
