using System.Text.Json;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Ts.Picking.TemplateMatch;
using Warp;

namespace Refund.Tests.Jobs;

public class TemplateMatchHandednessTests
{
    [Fact]
    public void AutomaticFlip_ReadsAllTenFinalResultsInsteadOfFourOriginalTrials()
    {
        using var data = new TemplateMatchTestData();
        data.WriteResults(4, false, 5);
        data.WriteResults(10, true, 14);
        data.WriteDecision(true);

        var particles = Assert.IsType<ParticleSet>(data.Job.PortsOut[TemplateMatch.PortOutParticleSet].GetResource());
        for (int i = 1; i <= 10; i++)
        {
            string path = particles.ToMultiStarPath($"TS_{i:00}.tomostar");
            Assert.Equal(data.StarPath(i, true), path);
            Assert.Equal(14f, Assert.Single(Star.LoadFloat(path, "rlnAutopickFigureOfMerit")));
        }
        Assert.Equal(data.FlippedTemplatePath, Assert.Single(particles.CorrespondingMaps.Maps).AverageVolumePath);
        Assert.False(data.Job.TemplateFlip); // The submitted parameters remain unchanged.
        Assert.Equal(10, data.Job.CountOutputItems()[TemplateMatch.PortOutParticleSet]);
    }

    [Fact]
    public void AutomaticOriginal_DoesNotSelectFlippedTrialFiles()
    {
        using var data = new TemplateMatchTestData();
        data.WriteResults(4, true, 5);
        data.WriteResults(10, false, 14);
        data.WriteDecision(false);

        var particles = Assert.IsType<ParticleSet>(data.Job.PortsOut[TemplateMatch.PortOutParticleSet].GetResource());
        Assert.Equal(data.StarPath(10, false), particles.ToMultiStarPath("TS_10.tomostar"));
        Assert.Equal(data.TemplatePath, Assert.Single(particles.CorrespondingMaps.Maps).AverageVolumePath);
    }

    [Fact]
    public void HandednessTrials_AreNotExposedAsSelectedResults()
    {
        using var data = new TemplateMatchTestData();
        data.WriteResults(4, false, 5);
        data.WriteResults(4, true, 14);
        File.WriteAllText(data.Job.PathStdOut,
            "Testing matching with original template:\nAverage top peak value with original template: 5.000\n" +
            "Testing matching with flipped template:\nAverage top peak value with flipped template: 14.000\n");

        Assert.Null(data.Job.PortsOut[TemplateMatch.PortOutParticleSet].GetResource());
        Assert.False(data.Job.CountOutputItems().ContainsKey(TemplateMatch.PortOutParticleSet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualHandedness_DoesNotRequireADecisionLog(bool flip)
    {
        using var data = new TemplateMatchTestData();
        data.Job.CheckHandN = null;
        data.Job.TemplateFlip = flip;
        data.WriteResults(10, flip, 14);

        var particles = Assert.IsType<ParticleSet>(data.Job.PortsOut[TemplateMatch.PortOutParticleSet].GetResource());
        Assert.Equal(data.StarPath(10, flip), particles.ToMultiStarPath("TS_10.tomostar"));
        Assert.Equal(flip ? data.FlippedTemplatePath : data.TemplatePath,
            Assert.Single(particles.CorrespondingMaps.Maps).AverageVolumePath);
    }

    [Fact]
    public void AutomaticEmdbFlip_UsesPaddedEmdbNameAndFlippedMap()
    {
        using var data = new TemplateMatchTestData();
        data.Job.PortsIn[TemplateMatch.PortInMapList].Edges.Clear();
        data.Job.TemplateEmdb = 123;
        data.WriteDecision(true);

        var particles = Assert.IsType<ParticleSet>(data.Job.PortsOut[TemplateMatch.PortOutParticleSet].GetResource());
        Assert.EndsWith("TS_10_10.00Apx_emd_0123_flipx.star", particles.ToMultiStarPath("TS_10.tomostar"));
        Assert.Equal(Path.Combine(data.Job.DirectoryPath, "template", "emd_0123_flipx.mrc"),
            Assert.Single(particles.CorrespondingMaps.Maps).AverageVolumePath);
    }

    [Fact]
    public void Card_WaitsForDecisionThenReplacesAnOldTrialCard()
    {
        using var data = new TemplateMatchTestData();
        data.WriteResults(4, false, 5);
        data.WriteResults(10, true, 14);
        File.WriteAllText(data.Job.VisCard(0), "old unflipped trial card");
        data.Job.VisAvailableIteration = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(data.FlippedTemplatePath)!);
        File.WriteAllText(data.FlippedTemplatePath, "flipped map");
        File.WriteAllText(Path.Combine(data.Root, "TS_01_10.00Apx.mrc"), "tomogram");
        int calls = 0;
        void Render(string tomogram, string star, string template, float diameter, string output)
        {
            calls++;
            Assert.Equal(data.StarPath(1, true), star);
            Assert.Equal(data.FlippedTemplatePath, template);
            File.WriteAllText(output, "flipped card");
        }

        Assert.Null(data.Job.TrackProgressResults(Render));
        Assert.Equal(0, calls);
        data.WriteDecision(true);
        var update = data.Job.TrackProgressResults(Render);
        Assert.NotNull(update);
        update();

        Assert.Equal(1, calls);
        Assert.Equal("reference_flipx", data.Job.VisTemplateName);
        Assert.Equal("flipped card", File.ReadAllText(data.Job.VisCard(0)));
        Assert.Null(data.Job.TrackProgressResults(Render));
        Assert.Equal(1, calls);
    }
}

internal sealed class TemplateMatchTestData : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "relay-match-" + Guid.NewGuid().ToString("N"));
    public TemplateMatch Job { get; }
    public string TemplatePath => Path.Combine(Root, "reference.mrc");
    public string FlippedTemplatePath => Path.Combine(Job.DirectoryPath, "template", "reference_flipx.mrc");

    public TemplateMatchTestData()
    {
        Job = new TemplateMatch { Id = 20, Space = new Space { RootDirectory = Root }, CheckHandN = 4 };
        Directory.CreateDirectory(Job.RelayResultsDirectoryPath);
        Directory.CreateDirectory(Path.Combine(Job.DirectoryPath, "matching"));
        var tomograms = new TomogramSet
        {
            PixelSize = 10,
            TiltSeriesSet = new TiltSeriesSet { HasMetadata = true },
            ToTomogramPath = name => Path.Combine(Root, Path.GetFileNameWithoutExtension(name) + "_10.00Apx.mrc"),
            ToTomogramThumbnailPath = name => Path.Combine(Root, Path.GetFileNameWithoutExtension(name) + ".png")
        };
        Connect(TemplateMatch.PortInTomogramSet, tomograms);
        Connect(TemplateMatch.PortInMapList, new MapList([new Map(averageVolumePath: TemplatePath)]));
    }

    private void Connect(string portName, Resource resource)
    {
        var input = Job.PortsIn[portName];
        input.Edges.Add(new Edge
        {
            Source = new PortOut(Job, resource.GetType(), "source", "source", _ => resource),
            Target = input
        });
    }

    public string StarPath(int i, bool flip) => Path.Combine(Job.DirectoryPath, "matching",
        $"TS_{i:00}_10.00Apx_reference{(flip ? "_flipx" : "")}.star");

    public void WriteResults(int count, bool flip, int score)
    {
        File.WriteAllText(Job.ResProcessedItemsJson, JsonSerializer.Serialize(
            Enumerable.Range(1, count).Select(i => new { Path = $"TS_{i:00}.tomostar" })));
        for (int i = 1; i <= count; i++)
            File.WriteAllText(StarPath(i, flip),
                "data_\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnCoordinateZ #3\n" +
                "_rlnAutopickFigureOfMerit #4\n" + $"0.5 0.5 0.5 {score}\n");
    }

    public void WriteDecision(bool flip) => File.WriteAllText(Job.PathStdOut,
        $"{(flip ? "Flipped" : "Original")} template has higher peak values, using it for further processing\n");

    public void Dispose() => Directory.Delete(Root, true);
}
