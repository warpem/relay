using Refund.DataModel;
using Refund.JobQueues;
using Refund.JobResources;
using Refund.Jobs.Common.Tools.DeduplicateParticles;
using Refund.Utils;
using Warp;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class DeduplicateParticlesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay dedup " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private DeduplicateParticles NewJob(params ParticleSet[] inputs)
    {
        JobRegistry.EnsurePopulated();
        var job = new DeduplicateParticles { Id = 7, Space = new Space { RootDirectory = _root } };
        var port = job.PortsIn[DeduplicateParticles.PortInParticles];
        foreach (var input in inputs)
        {
            var source = new PortOut(job, typeof(ParticleSet), "source", "Source", _ => input.Copy());
            port.Edges.Add(new Edge { Source = source, Target = port });
        }
        return job;
    }

    private static ParticleSet SingleStar(string path = "J1/particles.star") => new()
    {
        ParticlesSingleStarPath = path,
        HasData = true,
        HasPositions = true,
        HasShifts = true,
        CoordPixelSize = 1.35M,
        ItemCount = 1000
    };

    [Fact]
    public void RunsBeamOnTheCpuQueue()
    {
        var job = NewJob(SingleStar());
        Assert.IsAssignableFrom<IClusterJob>(job);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal(new[] { "beam", "cpu" }, job.RequiredModules);
        Assert.Equal(0, job.GpuCount);
        Assert.Equal("beam deduplicate", job.CommandName);
        Assert.Contains("touch", job.CommandSuffix);
        Assert.Contains(job.PathSuccess, job.CommandSuffix);
    }

    [Fact]
    public void SingleStarDefaultsRemoveTheWorseParticleWithin20Angstrom()
    {
        var job = NewJob(SingleStar());
        Assert.Empty(job.ValidateInputs());
        Assert.Empty(job.ValidatePortInputs());
        var args = job.ComposeCommandArguments();
        Assert.Equal(Path.Combine(_root, "J1", "particles.star"), args["input"]);
        Assert.Equal(job.ResParticlesStar, args["output"]);
        Assert.Equal(_root, args["project-root"]);
        Assert.Equal("20", args["distance"]);
        Assert.Equal("worse", args["drop"]);
        Assert.Equal("auto", args["score"]);
        Assert.Equal("1.35", args["coordinate-pixel-size"]);
        Assert.False(args.ContainsKey("lower-is-better"));

        job.CoordinatePixelSize = 0.83M;
        Assert.Equal("0.83", job.ComposeCommandArguments()["coordinate-pixel-size"]);
    }

    [Fact]
    public void UnknownCoordinatePixelSizeIsLeftToBeam()
    {
        var input = SingleStar();
        input.HasCoordPixelSize = false;
        var job = NewJob(input);
        Assert.False(job.ComposeCommandArguments().ContainsKey("coordinate-pixel-size"));
        job.CoordinatePixelSize = 0.9M;
        Assert.Equal("0.9", job.ComposeCommandArguments()["coordinate-pixel-size"]);
    }

    [Fact]
    public void ScoreOptionsFollowTheRemovalPolicy()
    {
        var job = NewJob(SingleStar());
        job.Score = DuplicateScore.Column;
        Assert.Contains(nameof(job.ScoreColumn), job.ValidateInputs().Keys);
        job.ScoreColumn = " rlnScore ";
        job.LowerIsBetter = true;
        var args = job.ComposeCommandArguments();
        Assert.Equal("rlnScore", args["score"]);
        Assert.True(args.ContainsKey("lower-is-better"));

        job.Score = DuplicateScore.None;
        args = job.ComposeCommandArguments();
        Assert.Equal("none", args["score"]);
        Assert.False(args.ContainsKey("lower-is-better"));

        job.Removal = DuplicateRemoval.Both;
        job.Score = DuplicateScore.Column;
        job.ScoreColumn = "";
        Assert.Empty(job.ValidateInputs());
        args = job.ComposeCommandArguments();
        Assert.Equal("both", args["drop"]);
        Assert.False(args.ContainsKey("score"));
        Assert.False(Job.IsPropertyVisible(typeof(DeduplicateParticles),
            typeof(DeduplicateParticles).GetProperty(nameof(job.Score))!, job.AsReadOnly()));

        job.Distance = 0;
        Assert.Contains(nameof(job.Distance), job.ValidateInputs().Keys);
    }

    [Fact]
    public void FolderOfStarFilesStaysAFolder()
    {
        var picks = new ParticleSet
        {
            ParticlesMultiStarDirectory = "/data/J3/matching",
            ToMultiStarPath = path => Path.Combine("/data/J3/matching", Path.GetFileNameWithoutExtension(path) + "_picks.star"),
            CoordPixelSize = 2.1M
        };
        var job = NewJob(picks);
        var args = job.ComposeCommandArguments();
        Assert.Equal("/data/J3/matching", args["input"]);
        Assert.Equal(job.ResParticlesDirectory, args["output"]);
        Assert.Equal("2.1", args["coordinate-pixel-size"]);

        var output = Assert.IsType<ParticleSet>(job.PortsOut[DeduplicateParticles.PortOutParticles].GetResource());
        Assert.Equal(job.ResParticlesDirectory, output.ParticlesMultiStarDirectory);
        Assert.False(output.HasSingleStar);
        Assert.Equal(Path.Combine(job.ResParticlesDirectory, "movie1_picks.star"), output.ToMultiStarPath("/movies/movie1.tiff"));
        Assert.Equal(2.1M, output.CoordPixelSize);
    }

    [Fact]
    public void NormalizedCoordinatesAreRejected()
    {
        var picks = new ParticleSet { ParticlesMultiStarDirectory = "/data/tm", HasNormalizedCoords = true };
        var job = NewJob(picks);
        Assert.Contains(DeduplicateParticles.PortInParticles, job.ValidatePortInputs().Keys);
        Assert.Throws<InvalidOperationException>(() => job.ComposeCommandArguments());
    }

    [Fact]
    public void SeveralSetsAreMergedFirst_IncludingWarpTiltSeriesParticles()
    {
        var first = SingleStar("/data/J1/particles.star");
        var second = SingleStar("/data/J2/particles.star");
        var job = NewJob(first, second);
        Assert.Empty(job.ValidatePortInputs());
        Assert.Equal(Path.Combine(ParticleInputs.DirectoryFor(job.PortsIn[DeduplicateParticles.PortInParticles]), "particles.star"),
            job.ComposeCommandArguments()["input"]);

        // Warp tilt-series particles have no RELION tomograms STAR; Beam needs none to deduplicate them.
        var tilt1 = SingleStar("/data/J4/particles.star");
        var tilt2 = SingleStar("/data/J5/particles.star");
        tilt1.DataDimensionality = tilt2.DataDimensionality = ParticleType.Tiltseries;
        Assert.Empty(NewJob(tilt1, tilt2).ValidatePortInputs());
    }

    [Fact]
    public void OutputCountIsUnknownUntilBeamPublishesIt()
    {
        var job = NewJob(SingleStar());
        var port = job.PortsOut[DeduplicateParticles.PortOutParticles];
        var output = Assert.IsType<ParticleSet>(port.GetResource());
        Assert.Null(output.ItemCount);
        Assert.Equal(job.ResParticlesStar, output.ParticlesSingleStarPath);
        Assert.True(output.HasShifts);
        Assert.Null(job.TrackProgressResults());

        Directory.CreateDirectory(job.DirectoryPath);
        File.WriteAllText(job.ResParticlesStar, """
            data_optics

            loop_
            _rlnOpticsGroup #1
            _rlnImagePixelSize #2
            1 1.35

            data_particles

            loop_
            _rlnCoordinateX #1
            _rlnCoordinateY #2
            _rlnOpticsGroup #3
            10 10 1
            90 10 1

            """);
        job.TrackProgressResults()!.Invoke();
        Assert.Equal(2, job.KeptParticleCount);
        Assert.Equal(2, port.GetResource().ItemCount);
        Assert.Null(job.TrackProgressResults());
    }

    [Fact]
    public void FolderOutputCountsEveryFile()
    {
        var job = NewJob(new ParticleSet { ParticlesMultiStarDirectory = "/data/picks", CoordPixelSize = 1 });
        Directory.CreateDirectory(job.ResParticlesDirectory);
        File.WriteAllText(Path.Combine(job.ResParticlesDirectory, "a.star"),
            "data_\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n1 2\n3 4\n");
        File.WriteAllText(Path.Combine(job.ResParticlesDirectory, "b.star"),
            "data_\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n");
        job.TrackProgressResults()!.Invoke();
        Assert.Equal(2, job.KeptParticleCount);
    }

    [Fact]
    public void StageRepointsTheOptimisationSet()
    {
        Directory.CreateDirectory(Path.Combine(_root, "J1"));
        string optimisation = Path.Combine(_root, "J1", "optimisation_set.star");
        File.WriteAllText(optimisation, "data_\n\n_rlnTomoParticlesFile J1/particles.star\n_rlnTomoTomogramsFile J1/tomograms.star\n");
        var input = SingleStar();
        input.OptimisationSetStarPath = optimisation;
        input.TomogramsStarPath = Path.Combine(_root, "J1", "tomograms.star");
        var job = NewJob(input);
        job.Stage();

        var output = Assert.IsType<ParticleSet>(job.PortsOut[DeduplicateParticles.PortOutParticles].GetResource());
        Assert.Equal(job.ResOptimisationSetStar, output.OptimisationSetStarPath);
        Assert.Equal(input.TomogramsStarPath, output.TomogramsStarPath);
        string written = File.ReadAllText(job.ResOptimisationSetStar);
        Assert.Contains(job.Space.GetRelativePath(job.ResParticlesStar), written);
        Assert.Contains("J1/tomograms.star", written);
    }
}
