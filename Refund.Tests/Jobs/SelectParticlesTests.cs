using System.Text.Json;
using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobResources;
using Refund.Jobs.Ts.Selection.SelectParticles;
using Warp;

namespace Refund.Tests.Jobs;

public class SelectParticlesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-select-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FinishInteractiveJob_FiltersParticlesAndGeneratesCard(int tomogramCount)
    {
        var job = new RenderingJob { Id = 29, Space = new Space { RootDirectory = _root }, IsInteractiveFinished = true };
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        File.WriteAllText(job.ResProcessedItemsJson, JsonSerializer.Serialize(
            Enumerable.Range(1, tomogramCount).Select(i => new { Path = $"TS_{i:00}.tomostar" })));
        Connect(job, SelectParticles.PortInTomogramSet, () => new TomogramSet
        {
            TiltSeriesSet = new TiltSeriesSet { HasMetadata = true },
            ProcessedItemsJson = job.ResProcessedItemsJson,
            ToTomogramPath = name => Path.Combine(_root, Path.GetFileNameWithoutExtension(name) + ".mrc")
        });
        Connect(job, SelectParticles.PortInParticleSet, () => new ParticleSet
        {
            Diameter = 100,
            ParticlesMultiStarDirectory = _root,
            ToMultiStarPath = name => Path.Combine(_root, Path.GetFileNameWithoutExtension(name) + ".star")
        });
        for (int i = 1; i <= tomogramCount; i++)
            File.WriteAllText(Path.Combine(_root, $"TS_{i:00}.star"),
                "data_\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnCoordinateZ #3\n_rlnAutopickFigureOfMerit #4\n10 20 30 5\n20 30 40 14\n");
        foreach (string key in SelectParticles.FilterKeys)
            job.GlobalFilterSettings.Filters[key] = new FilterSetting { Min = -180, Max = 500 };
        job.GlobalFilterSettings.Filters[SelectParticles.FilterKeyScore] = new FilterSetting { Min = 6.8M, Max = 25 };

        job.RunLocal(CancellationToken.None);

        var coordinator = new ExecutionCoordinator([new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler)]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 29), 1, ResourceVector.None, true).CreateSnapshot();
        var operations = new RelayExecutionOperations(_ => job, (updated, action) =>
        {
            action(updated);
            return Task.CompletedTask;
        });
        try
        {
            await operations.FinalizeAsync(attempt, ExecutionOutcome.Succeeded, CancellationToken.None);
            Assert.Equal(1, job.RenderCalls);
            Assert.Equal(0, job.VisAvailableIteration);
            Assert.True(File.Exists(job.VisCard(0)));
            Assert.Equal(Path.Combine(_root, "TS_01.mrc"), job.FirstTomogram);
            Assert.Equal(Path.Combine(_root, $"TS_{Math.Min(2, tomogramCount):00}.mrc"), job.SecondTomogram);
            Assert.Null(job.TrackProgressResults());
            for (int i = 1; i <= tomogramCount; i++)
            {
                var selected = new Star(Path.Combine(job.DirectoryPath, "matching", $"TS_{i:00}_selected.star"));
                Assert.Equal(1, selected.RowCount);
                Assert.Equal(14f, selected.GetFloatRobust("rlnAutopickFigureOfMerit")[0]);
            }
        }
        finally { await operations.ShutdownAsync(CancellationToken.None); }
    }

    private static void Connect<T>(SelectParticles job, string portName, Func<T> resource) where T : Resource
    {
        var input = job.PortsIn[portName];
        input.Edges.Add(new Edge
        {
            Source = new PortOut(job, typeof(T), "source", "source", _ => resource()), Target = input
        });
    }

    private sealed class RenderingJob : SelectParticles
    {
        public int RenderCalls { get; private set; }
        public string? FirstTomogram { get; private set; }
        public string? SecondTomogram { get; private set; }
        public override Action TrackProgressResults() => base.TrackProgressResults((first, firstStar, second, secondStar, diameter, output) =>
        {
            Assert.True(File.Exists(firstStar));
            Assert.True(File.Exists(secondStar));
            Assert.Equal(100f, diameter);
            FirstTomogram = first;
            SecondTomogram = second;
            RenderCalls++;
            File.WriteAllText(output, "card");
        });
    }

    public void Dispose() => Directory.Delete(_root, true);
}
