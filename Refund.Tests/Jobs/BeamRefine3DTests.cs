using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class BeamRefine3DTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay beam " + Guid.NewGuid().ToString("N"));

    private BeamRefine3D NewJob()
    {
        JobRegistry.EnsurePopulated();
        var job = new BeamRefine3D { Id = 1, Space = new Space { RootDirectory = _root } };
        Connect(job.PortsIn["Particles"], new ParticleSet
        {
            HasData = true, HasAngles = true, HasShifts = true, HasCtf = true,
            ParticlesSingleStarPath = Path.Combine(_root, "input.star")
        });
        Connect(job.PortsIn["Reference"], new MapList([new Map(averageVolumePath: Path.Combine(_root, "map.mrc"))]));
        return job;
    }

    private static void Connect<T>(PortIn input, T resource) where T : Resource
    {
        var source = new PortOut(input.Job, typeof(T), "source", "Source", _ => resource);
        input.Edges.Add(new Edge { Source = source, Target = input });
    }

    [Fact]
    public void GlobalDefaultsUseOneGpuAndAutomaticMasks()
    {
        var job = NewJob();
        var args = job.ComposeCommandArguments();
        Assert.Equal("beam refine", job.CommandName);
        Assert.Equal("cuda:0", args["device"]);
        Assert.Equal("fsc", args["regularization"]);
        Assert.Equal("200", args["particle-diameter"]);
        Assert.Equal("40", args["translation-range"]);
        Assert.Equal(job.BeamDirectory, args["output"]);
        Assert.False(args.ContainsKey("ewald"));
        Assert.False(args.ContainsKey("local-angular-range"));
        Assert.False(args.ContainsKey("resume"));
        Assert.True(args.ContainsKey("show-progress"));
        Assert.False(args.ContainsKey("dont-show-progress"));
        Assert.False(args.ContainsKey("mask"));
        Assert.Equal(1, job.GpuCount);
        Assert.Equal(1, job.ProcessCount);
        Assert.Contains("beam", job.RequiredModules);
        Assert.DoesNotContain("relion", job.RequiredModules);
        Assert.False(job.IsResumable);
        Assert.True(job.CanBeFinalized);
    }

    [Fact]
    public void LocalModeRequiresPosesAndMaskAndOmitsGlobalSettings()
    {
        var job = NewJob();
        job.Alignment = BeamAlignment.Local;
        Assert.Contains("Mask", job.ValidatePortInputs().Keys);
        Connect(job.PortsIn["Mask"], new Mask(Path.Combine(_root, "mask.mrc")));
        job.Regularization = BeamRegularization.Denoise;
        job.LocalPivot = BeamLocalPivot.BoxCenter;
        var args = job.ComposeCommandArguments();
        Assert.Equal("5", args["local-angular-range"]);
        Assert.Equal("10", args["translation-range"]);
        Assert.Equal("box-center", args["local-pivot"]);
        Assert.Equal("0.08", args["denoise-alignment-fsc"]);
        Assert.True(args.ContainsKey("dont-align-symmetry"));
        Assert.False(args.ContainsKey("particle-diameter"));
        Assert.Empty(job.ValidatePortInputs());
    }

    [Fact]
    public void EnumSelectionsAndDependenciesControlCommandAndVisibility()
    {
        var job = NewJob();
        job.Device = BeamDevice.CPU;
        job.Ewald = BeamEwald.Negative;
        job.CtfFit = BeamCtfFit.DefocusAndAberrations;
        var args = job.ComposeCommandArguments();
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal("p-negative", args["ewald"]);
        Assert.True(args.ContainsKey("fit-defocus"));
        Assert.True(args.ContainsKey("fit-aberrations"));
        Assert.True(args.ContainsKey("defocus-range"));
        job.CtfFit = BeamCtfFit.None;
        Assert.False(job.ComposeCommandArguments().ContainsKey("defocus-range"));
        foreach (string field in new[] { nameof(job.LocalAngularRange), nameof(job.DenoiseAlignmentFsc), nameof(job.DefocusRange) })
            Assert.False(Job.IsPropertyVisible(typeof(BeamRefine3D), typeof(BeamRefine3D).GetProperty(field), job.AsReadOnly()));
    }

    [Fact]
    public void CustomOptionsOverrideScientificDefaultsAndPreserveQuotedValues()
    {
        var job = NewJob();
        job.AdditionalArguments = "--beams 12 --blush-model 'my model.pt' --sharpen-half-maps=0.5 --no-convergence-integrated-fsc";
        var args = job.ComposeCommandArguments();
        Assert.Equal("12", args["beams"]);
        Assert.Equal("my model.pt", args["blush-model"]);
        Assert.Equal("0.5", args["sharpen-half-maps"]);
        Assert.Equal("", args["no-convergence-integrated-fsc"]);
        job.AdditionalArguments = "--output /different";
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
        Assert.Throws<ArgumentException>(() => job.ComposeCommandArguments());
    }

    [Fact]
    public void FinalizationUsesCommittedIterationsAndPersistsLogs()
    {
        var job = NewJob();
        foreach (int i in new[] { 1, 2 })
        {
            foreach (string suffix in new[] { "data.star", "model.star", "class001.mrc", "half1_class001_unfiltered.mrc", "half2_class001_unfiltered.mrc" })
                Write(job.IterationFile(i, suffix), "committed");
            foreach (string path in new[] { job.VisCard(i), job.VisFilteredSlices(i), job.VisFsc(i), job.VisAngularDistribution(i), job.VisFourierSampling(i) })
                Write(path, "visualized");
        }
        Directory.CreateDirectory(Path.Combine(job.BeamDirectory, ".it003.staging-1"));
        Directory.CreateDirectory(Path.Combine(job.BeamDirectory, "it003"));
        Write(job.PathStdOut, "setup\n=== Iteration 1 ===\nfirst\n=== Iteration 2 ===\nsecond\n");
        Write(job.PathStdErr, "warning\n");
        job.FinalizeRun((j, action) => action(j));
        Assert.Equal(2, job.VisAvailableIteration);
        Assert.Equal(2, job.LogsAvailableIteration);
        Assert.True(job.HasRunToCompletion);
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));
        Assert.Contains("first", File.ReadAllText(job.LogFilePath(1)));
        Assert.DoesNotContain("second", File.ReadAllText(job.LogFilePath(1)));
        Assert.Equal(new[] { 1, 2 }, job.CommittedIterations());
        Assert.Null(job.TrackProgressResults());
        var maps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource());
        Assert.Equal(job.IterationFile(2, "half1_class001_unfiltered.mrc"), maps.Maps[0].Half1VolumePath);
        // Finished-job access relies on saved Relay state, not scanning Beam checkpoints.
        Directory.Delete(job.BeamDirectory, true);
        Assert.Equal(job.IterationFile(2, "class001.mrc"), job.VisMap3d(2));
        Assert.Contains("second", File.ReadAllText(job.LogFilePath(2)));
        var reloaded = Assert.IsType<BeamRefine3D>(Job.CreateFromPolymorphicJson(
            Job.ToPolymorphicJson(job), job.Space, new List<User>().AsReadOnly()));
        Assert.Equal(2, reloaded.VisAvailableIteration);
        Assert.True(reloaded.HasRunToCompletion);
        Assert.Equal(job.LogFilePath(2), reloaded.LogFilePath(2));
    }

    [Fact]
    public void LiveProgressIsCollapsedWithinItsIterationAndWarningsStaySeparate()
    {
        var job = NewJob();
        Write(job.PathStdOut, "setup\r\n=== Iteration 1 ===\r\n" +
            "\rParticles: 10% 10/100 [00:01<00:09, 10particle/s]" +
            "\rParticles: 50% 50/100 [00:05<00:05, 10particle/s]");
        Write(job.PathStdErr, "warning\n");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal(1, job.LogsAvailableIteration);
        Assert.Equal("setup", File.ReadAllText(job.LogFilePath(0)));
        string first = File.ReadAllText(job.LogFilePath(1));
        Assert.Contains("50/100 [00:05<00:05, 10particle/s]", first);
        Assert.DoesNotContain("10/100", first);
        Assert.DoesNotContain('\r', first);
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));

        File.AppendAllText(job.PathStdOut,
            "\rParticles: 100% 100/100 [00:10<00:00, 10particle/s]\n" +
            "=== Iteration 2 ===\n\rParticles: 25% 25/100 [00:01<00:03, 25particle/s]");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal(2, job.LogsAvailableIteration);
        first = File.ReadAllText(job.LogFilePath(1));
        Assert.Contains("100/100", first);
        Assert.DoesNotContain("50/100", first);
        Assert.DoesNotContain("25/100", first);
        Assert.Contains("25/100", File.ReadAllText(job.LogFilePath(2)));
    }

    [Theory]
    [InlineData("--show-progress")]
    [InlineData("--dont-show-progress")]
    public void ProgressRoutingIsManagedByRelay(string argument)
    {
        var job = NewJob();
        job.AdditionalArguments = argument;
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
    }

    [Fact]
    public void FinalizationRejectsAnEmptyOrUncommittedJob()
    {
        var job = NewJob();
        Assert.Throws<InvalidOperationException>(() => job.FinalizeRun((j, action) => action(j)));
        Assert.False(File.Exists(job.PathSuccess));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
