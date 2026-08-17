using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobResources;
using Warp;
using ExpandSymmetryJob = Refund.Jobs.Common.Tools.ExpandSymmetry.ExpandSymmetry;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class ExpandSymmetryTests
{
    private static readonly object PopulateLock = new();

    private static void EnsurePopulated()
    {
        lock (PopulateLock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    // Build a job with a Space and a connected single-STAR ParticleSet on its input port, so
    // ComposeCommandArguments can resolve --i against the Space.
    private static ExpandSymmetryJob MakeJobWithInput(ParticleSet particles)
    {
        EnsurePopulated();

        var job = new ExpandSymmetryJob
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            Id = 1,
            DirectoryName = "J1_ExpandSym",
        };

        var portIn = job.PortsIn[ExpandSymmetryJob.PortInParticles];
        var source = new PortOut(job, typeof(ParticleSet), "src", "src", _ => particles);
        portIn.Edges.Add(new Edge { Source = source, Target = portIn });

        return job;
    }

    private static ParticleSet SingleStar(string path = "/tmp/relay-test/input/particles.star", string optSet = null)
    {
        return new ParticleSet
        {
            ParticlesSingleStarPath = path,
            OptimisationSetStarPath = optSet,
            CoordPixelSize = 1.35M,
        };
    }

    [Fact]
    public void CommandName_IsRelionSymmetryExpand()
    {
        Assert.Equal("relion_particle_symmetry_expand", new ExpandSymmetryJob().CommandName);
    }

    [Fact]
    public void IsCpuClusterJob()
    {
        var job = new ExpandSymmetryJob();
        Assert.IsAssignableFrom<IClusterJob>(job);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
    }

    [Fact]
    public void ComposeCommandArguments_PointGroup_EmitsSymAndIo()
    {
        var job = MakeJobWithInput(SingleStar());
        job.DoHelical = false;
        job.Symmetry = "C3";

        var args = job.ComposeCommandArguments();

        Assert.Equal("input/particles.star", args["i"]);
        Assert.Equal("J1_ExpandSym/expanded.star", args["o"]);
        Assert.Contains("C3", args["sym"]); // stored quoted by the base composer

        // Helical-only flags must not leak into a point-group run (RELION rejects --sym with --helix).
        Assert.False(args.ContainsKey("helix"));
        Assert.False(args.ContainsKey("twist"));
        Assert.False(args.ContainsKey("rise"));
        Assert.False(args.ContainsKey("asu"));
    }

    [Fact]
    public void ComposeCommandArguments_Helical_EmitsHelixFlagsAndNotSym()
    {
        var job = MakeJobWithInput(SingleStar());
        job.DoHelical = true;
        job.HelicalUnits = 5;
        job.HelicalTwist = 30m;
        job.HelicalRise = 12.5m;

        var args = job.ComposeCommandArguments();

        Assert.True(args.ContainsKey("helix"));
        Assert.Equal("", args["helix"]);           // presence-only flag
        Assert.Equal("5", args["asu"]);
        Assert.Equal("30", args["twist"]);
        Assert.Equal("12.5", args["rise"]);
        Assert.Equal("1.35", args["angpix"]);      // taken from the input pixel size

        // sym must be absent so RELION doesn't error on "--sym OR --helix".
        Assert.False(args.ContainsKey("sym"));
    }

    [Fact]
    public void ComposeCommandArguments_Throws_WhenNoInputConnected()
    {
        EnsurePopulated();
        var job = new ExpandSymmetryJob { Space = new Space { RootDirectory = "/tmp/relay-test" } };

        Assert.Throws<Exception>(() => job.ComposeCommandArguments());
    }

    [Fact]
    public void Parameters_RoundTripJson()
    {
        var job = new ExpandSymmetryJob
        {
            DoHelical = true,
            Symmetry = "D7",
            HelicalUnits = 6,
            HelicalTwist = 15.25m,
            HelicalRise = 4.75m,
        };

        var node = new JsonObject();
        job.WriteToJson(node);

        var job2 = new ExpandSymmetryJob();
        job2.ReadFromJson(node);

        Assert.True(job2.DoHelical);
        Assert.Equal("D7", job2.Symmetry);
        Assert.Equal(6, job2.HelicalUnits);
        Assert.Equal(15.25m, job2.HelicalTwist);
        Assert.Equal(4.75m, job2.HelicalRise);
    }

    [Fact]
    public void Stage_WritesAdaptedOptimisationSet_ForTomoInput()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-expandsym-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "input"));
        try
        {
            // Upstream optimisation set on disk, pointing at the upstream particles + tomograms.
            string inOptSet = Path.Combine(root, "input", "optimisation_set.star");
            new StarParameters(
                new[] { "rlnTomoParticlesFile", "rlnTomoTomogramsFile" },
                new[] { "input/particles.star", "input/tomograms.star" }).Save(inOptSet);

            var particles = new ParticleSet
            {
                ParticlesSingleStarPath = Path.Combine(root, "input", "particles.star"),
                OptimisationSetStarPath = inOptSet,
                CoordPixelSize = 1M,
            };

            EnsurePopulated();
            var job = new ExpandSymmetryJob
            {
                Space = new Space { RootDirectory = root },
                Id = 203,
                DirectoryName = "203",
            };
            var portIn = job.PortsIn[ExpandSymmetryJob.PortInParticles];
            var source = new PortOut(job, typeof(ParticleSet), "src", "src", _ => particles);
            portIn.Edges.Add(new Edge { Source = source, Target = portIn });

            job.Stage();

            // The job must write its own optimisation set (before the RELION command runs), repointed
            // at the expanded particles while preserving the tomograms entry.
            string outOptSet = Path.Combine(root, "203", "optimisation_set.star");
            Assert.True(File.Exists(outOptSet), "Stage() did not write the optimisation set");

            var set = new StarParameters(outOptSet);
            Assert.Equal("203/expanded.star", set.GetColumn("rlnTomoParticlesFile")[0]);
            Assert.Equal("input/tomograms.star", set.GetColumn("rlnTomoTomogramsFile")[0]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Stage_NoOp_WhenInputHasNoOptimisationSet()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-expandsym-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var particles = new ParticleSet
            {
                ParticlesSingleStarPath = Path.Combine(root, "particles.star"),
                OptimisationSetStarPath = null,
            };

            EnsurePopulated();
            var job = new ExpandSymmetryJob
            {
                Space = new Space { RootDirectory = root },
                Id = 7,
                DirectoryName = "7",
            };
            var portIn = job.PortsIn[ExpandSymmetryJob.PortInParticles];
            var source = new PortOut(job, typeof(ParticleSet), "src", "src", _ => particles);
            portIn.Edges.Add(new Edge { Source = source, Target = portIn });

            job.Stage();

            Assert.False(File.Exists(Path.Combine(root, "7", "optimisation_set.star")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AdaptOptimisationSet_RepointsParticlesFileAndPreservesTomograms()
    {
        string dir = Path.Combine(Path.GetTempPath(), "relay-expandsym-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string inputPath = Path.Combine(dir, "optimisation_set.star");
            string outputPath = Path.Combine(dir, "optimisation_set_out.star");

            var setIn = new StarParameters(
                new[] { "rlnTomoParticlesFile", "rlnTomoTomogramsFile" },
                new[] { "old/particles.star", "tomograms.star" });
            setIn.Save(inputPath);

            ExpandSymmetryJob.AdaptOptimisationSet(inputPath, outputPath, "new/expanded.star");

            var setOut = new StarParameters(outputPath);
            Assert.Equal("new/expanded.star", setOut.GetColumn("rlnTomoParticlesFile")[0]);
            Assert.Equal("tomograms.star", setOut.GetColumn("rlnTomoTomogramsFile")[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
