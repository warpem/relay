using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.BeamClass3D;
using Refund.Jobs.Refinement.InitialModel.BeamInit3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.Utils;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class BeamInit3DTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay init3d " + Guid.NewGuid().ToString("N"));
    private BeamInit3D NewJob()
    {
        JobRegistry.EnsurePopulated();
        var job = new BeamInit3D { Id = 1, Space = new Space { RootDirectory = _root } };
        Connect(job.PortsIn["Particles"], new ParticleSet
        { HasData = true, ParticlesSingleStarPath = Path.Combine(_root, "particles.star") });
        return job;
    }
    private static void Connect<T>(PortIn input, T resource) where T : Resource =>
        input.Edges.Add(new Edge { Target = input, Source = new PortOut(input.Job, typeof(T), "source", "Source", _ => resource) });

    [Fact]
    public void DefaultsNeedNoReferenceMaskOrPosesAndUseTheInitCli()
    {
        var job = NewJob();
        job.Alignment = BeamAlignment.Local; // Hidden refinement settings cannot affect this verb.
        Assert.Empty(job.ValidatePortInputs());
        Assert.DoesNotContain("Reference", job.PortsIn.Keys);
        Assert.DoesNotContain("Mask", job.PortsIn.Keys);
        Assert.Equal("beam init3d", job.CommandName);
        var args = job.ComposeCommandArguments();
        Assert.Equal("1", args["classes"]);
        Assert.Equal("200", args["updates"]);
        Assert.Equal("64", args["working-box"]);
        Assert.Equal("35", args["initial-resolution"]);
        Assert.Equal("12", args["final-resolution"]);
        Assert.Equal("200", args["particle-diameter"]);
        Assert.Equal("cuda:0", args["device"]);
        Assert.Equal("8", args["workers"]);
        Assert.Equal(8, job.CoreCount);
        Assert.Contains("beam", job.RequiredModules);
        Assert.Contains("show-progress", args.Keys);
        foreach (string name in new[] { "reference", "mask", "local-angular-range", "translation-range", "beams", "dont-align-symmetry", "regularization", "iterations" })
            Assert.DoesNotContain(name, args.Keys);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void BothJobsRejectUnsupportedBoxSizes(int box)
    {
        var init = NewJob();
        init.WorkingBox = box;
        Assert.Contains(nameof(init.WorkingBox), init.ValidateInputs().Keys);
        var classify = new BeamClass3D { WorkingBox = box };
        Assert.Contains(nameof(classify.WorkingBox), classify.ValidateInputs().Keys);
    }

    [Fact]
    public void ClassificationBoxSizeIsExposedAndSerialized()
    {
        var classify = new BeamClass3D { Id = 2, Space = new Space { RootDirectory = _root } };
        Assert.Equal(128, classify.WorkingBox);
        classify.WorkingBox = 96;
        Connect(classify.PortsIn["Particles"], new ParticleSet { HasData = true, ParticlesSingleStarPath = "/particles.star" });
        Connect(classify.PortsIn["Reference"], new MapList([new Map(averageVolumePath: "/reference.mrc")]));
        Assert.Equal("96", classify.ComposeCommandArguments()["working-box"]);
        var clone = Assert.IsType<BeamClass3D>(Job.CreateFromPolymorphicJson(Job.ToPolymorphicJson(classify), classify.Space, new List<User>().AsReadOnly()));
        Assert.Equal(96, clone.WorkingBox);
        Assert.Equal("working-box", Job.TypeUiFields[typeof(BeamClass3D)].Single(p => p.Key.Name == nameof(classify.WorkingBox)).Value.CliName);
    }

    [Fact]
    public void OnlyCompleteSnapshotsArePublishedAndInterruptedRunsCanBeFinalized()
    {
        var job = NewJob(); job.NClasses = 2;
        Write(Path.Combine(job.BeamDirectory, "it010_initial_model_class001.mrc"), "map");
        Assert.Empty(job.CommittedIterations());
        Write(Path.Combine(job.BeamDirectory, "it010_initial_model_class002.mrc"), "map");
        SavedFigures(job, 1, sampling: false);
        job.TrackProgressResults()!();
        Assert.Equal(1, job.VisAvailableIteration);
        Assert.Null(job.PortsOut["Particles"].GetResource());
        var maps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource());
        Assert.Equal(Path.Combine(job.BeamDirectory, "it010_initial_model_class002.mrc"), maps.Maps[1].AverageVolumePath);
        Assert.False(maps.Maps[0].HasHalf1Volume);
        Write(job.PathStdOut, "setup\nupdate    1  35 A\nupdate   10  34 A\n\rAb initio updates: 1/2\rAb initio updates: 2/2\n");
        Write(job.PathStdErr, "warning\n");
        job.FinalizeRun((j, action) => action(j));
        Assert.True(job.HasRunToCompletion);
        Assert.False(job.FinalAssignmentAvailable);
        Assert.Contains("update    1", File.ReadAllText(job.LogFilePath(0)));
        Assert.Contains("2/2", File.ReadAllText(job.LogFilePath(1)));
        Assert.DoesNotContain("1/2", File.ReadAllText(job.LogFilePath(1)));
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalAssignmentAndTiltMetadataArePersistedAndCanFeedRefinement(bool tilt)
    {
        var job = NewJob(); job.NClasses = 2; job.Updates = 12;
        if (tilt)
        {
            ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource.DataDimensionality = ParticleType.Tiltseries;
            Connect(job.PortsIn["TiltSeries"], new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = "/source" });
            Write(Path.Combine(job.IterationMetadataDirectory(12), "TS.xml"), "xml");
            Assert.Equal("/source", job.ComposeCommandArguments()["metadata-dir"]);
        }
        Assert.Null(job.PortsOut["Particles"].GetResource());
        Write(Path.Combine(job.BeamDirectory, "initial_model_class001.mrc"), "map");
        Write(Path.Combine(job.BeamDirectory, "initial_model_class002.mrc"), "map");
        Write(job.ParticleStar, "particles");
        Assert.Empty(job.CommittedIterations());
        Write(job.SummaryFile, "{\"final_assignment\":{\"class_posterior_mass\":[1,3]}}");
        SavedFigures(job, 2, sampling: true);
        Write(job.PathSuccess, "");
        job.TrackProgressResults()!();
        Assert.True(job.FinalAssignmentAvailable);
        Assert.Equal(2, job.LastCommittedIteration);
        Assert.True(job.HasRunToCompletion);
        var particles = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResource());
        Assert.True(particles.HasClasses && particles.HasAngles && particles.HasShifts);
        Assert.Equal(job.ParticleStar, particles.ParticlesSingleStarPath);
        var maps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource());
        Assert.Equal(Path.Combine(job.BeamDirectory, "initial_model_class001.mrc"), maps.Maps[0].AverageVolumePath);
        Assert.Null(maps.Model);
        Assert.Null(job.PortsOut["Particles"].GetResource(1));
        Assert.Contains("0.75", File.ReadAllText(job.VisClassStats(2)));
        if (tilt)
        {
            var metadata = Assert.IsType<TiltSeriesSet>(job.PortsOut["TiltSeries"].GetResource());
            Assert.Equal(job.IterationMetadataDirectory(12), metadata.LatestMetadataDirectory);
            Assert.True(File.Exists(metadata.ProcessedItemsJson));
            Assert.False(metadata.HasReferenceBasedRefinements);
        }
        Directory.Delete(job.BeamDirectory, true);
        // Finished views/resources use saved Relay state instead of reparsing Beam output.
        Assert.Equal(maps.Maps[0].AverageVolumePath, Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource()).Maps[0].AverageVolumePath);
        var clone = Assert.IsType<BeamInit3D>(Job.CreateFromPolymorphicJson(Job.ToPolymorphicJson(job), job.Space, new List<User>().AsReadOnly()));
        Assert.True(clone.FinalAssignmentAvailable);
        Connect(clone.PortsIn["Particles"], ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource);
        Assert.Equal(job.ParticleStar, Assert.IsType<ParticleSet>(clone.PortsOut["Particles"].GetResource()).ParticlesSingleStarPath);
    }

    [Fact]
    public void TiltCtfOverrideAndInvalidAnnealingAreRejected()
    {
        var job = NewJob();
        job.FinalResolution = 40;
        Assert.Contains(nameof(job.FinalResolution), job.ValidateInputs().Keys);
        job.FinalResolution = 12;
        job.CtfIntactFirstPeak = true;
        ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource.DataDimensionality = ParticleType.Tiltseries;
        Assert.Contains(nameof(job.CtfIntactFirstPeak), job.ValidateInputs().Keys);
    }

    private static void SavedFigures(BeamInit3D job, int update, bool sampling)
    {
        for (int c = 1; c <= job.NClasses; c++)
            foreach (string png in new[] { job.VisFilteredSlices(update, c) }.Concat(sampling ? [job.VisAngularDistribution(update, c), job.VisFourierSampling(update, c)] : Array.Empty<string>()))
                foreach (string file in new[] { png, Path.ChangeExtension(png, ".svg"), Path.ChangeExtension(png, ".light.svg"), Path.ChangeExtension(png, ".dark.svg") }) Write(file, "saved");
        Write(job.VisCard(update), "saved");
    }
    private static void Write(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
