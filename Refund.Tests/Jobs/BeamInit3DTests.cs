using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.BeamClass3D;
using Refund.Jobs.Refinement.InitialModel.BeamInit3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.Utils;
using Refund.Services.Storage;

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
        Assert.Equal("1000", args["updates"]);
        Assert.Equal("50", args["snapshot-interval"]);
        Assert.Equal("100", args["initial-minibatch"]);
        Assert.Equal("500", args["final-minibatch"]);
        Assert.Equal("0.5", args["initial-learning-rate"]);
        Assert.Equal("0.1", args["final-learning-rate"]);
        Assert.True(job.EnforcePositivity);
        Assert.DoesNotContain("no-positivity", args.Keys);
        Assert.Equal("1", args["pose-marginalization-temperature"]);
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
        var job = NewJob(); job.SnapshotInterval = 10; job.NClasses = 2;
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
        Assert.Contains("update    1", File.ReadAllText(job.LogFilePath(1)));
        Assert.Contains("2/2", File.ReadAllText(job.LogFilePath(1)));
        Assert.DoesNotContain("1/2", File.ReadAllText(job.LogFilePath(1)));
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));
    }

    [Fact]
    public void ActiveUpdateBlocksArePublishedBeforeTheirVisualizationsAndKeepAllLogLines()
    {
        var job = NewJob(); job.SnapshotInterval = 10; job.Updates = 12;
        Write(job.PathStdOut, "preparing particles\nAb initio updates: 0/200\n");
        job.TrackProgressLogs()!();
        Assert.Equal(1, job.LogsAvailableIteration);
        Assert.Equal(-1, job.VisAvailableIteration); // Pagination shows a spinner.
        Assert.Equal("preparing particles", File.ReadAllText(job.LogFilePath(0)).Trim());

        string firstBlock = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"update {i,4}  35 A"));
        Write(job.PathStdOut, $"preparing particles\nAb initio updates: 0/200\n{firstBlock}\nupdate   11  34 A\nupdate   12  33 A\nAssigning all particles\n");
        job.TrackProgressLogs()!();
        Assert.Equal(2, job.LogsAvailableIteration);
        Assert.Equal(-1, job.VisAvailableIteration);
        string first = File.ReadAllText(job.LogFilePath(1));
        foreach (int update in Enumerable.Range(1, 10)) Assert.Contains($"update {update,4} ", first);
        Assert.DoesNotContain("update   11", first);
        string second = File.ReadAllText(job.LogFilePath(2));
        Assert.Contains("update   11", second);
        Assert.Contains("update   12", second);
        Assert.Contains("Assigning all particles", second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalAssignmentAndTiltMetadataArePersistedAndCanFeedRefinement(bool tilt)
    {
        var job = NewJob(); job.SnapshotInterval = 10; job.NClasses = 2; job.Updates = 12;
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

    [Theory]
    [InlineData(7)]
    [InlineData(50)]
    [InlineData(100)]
    public void SnapshotIntervalControlsLogsPreviewsFinalBlocksAndStorageMapping(int interval)
    {
        var job = NewJob(); job.SnapshotInterval = interval; job.Updates = 2 * interval + 3;
        Write(Path.Combine(job.BeamDirectory, $"it{interval:D3}_initial_model_class001.mrc"), "map");
        SavedFigures(job, 1, sampling: false);
        Assert.Equal(new[] { 1 }, job.CommittedIterations());
        job.TrackProgressResults()!();
        Assert.Equal(1, job.VisAvailableIteration);
        Write(job.PathStdOut, $"Ab initio updates: 0/1000\nupdate    1  35 A\nupdate {interval,4}  35 A\nupdate {interval + 1,4}  34 A\n");
        job.TrackProgressLogs()!();
        Assert.Equal(2, job.LogsAvailableIteration);
        Assert.Contains($"update {interval,4} ", File.ReadAllText(job.LogFilePath(1)));
        Assert.Contains($"update {interval + 1,4} ", File.ReadAllText(job.LogFilePath(2)));
        var maps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource(1));
        Assert.Equal(Path.Combine(job.BeamDirectory, $"it{interval:D3}_initial_model_class001.mrc"), maps.Maps[0].AverageVolumePath);
        Assert.Equal(interval * 2, IntermediateRules.BeamInit3D.ToDisk(job, 2));
        Assert.Equal(3, IntermediateRules.BeamInit3D.ToLogical(job, job.Updates));
        Assert.Equal(job.Updates, IntermediateRules.BeamInit3D.ToDisk(job, 3));

        Write(Path.Combine(job.BeamDirectory, "initial_model_class001.mrc"), "final map");
        Write(job.ParticleStar, "particles");
        Write(job.SummaryFile, "{\"final_assignment\":{\"class_posterior_mass\":[1]}}");
        SavedFigures(job, 3, sampling: true);
        job.TrackProgressResults()!();
        Assert.Equal(new[] { 1, 3 }, job.CommittedIterations());
        Assert.True(job.IsFinalUpdate(3));
        var finalMaps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource(3));
        Assert.Equal(Path.Combine(job.BeamDirectory, "initial_model_class001.mrc"), finalMaps.Maps[0].AverageVolumePath);
        Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResource(3));
    }

    [Fact]
    public void OptimizationControlsRoundTripAndPositivityCanBeDisabled()
    {
        var job = NewJob();
        job.EnforcePositivity = false;
        job.InitialMinibatch = 40; job.FinalMinibatch = 300;
        job.InitialLearningRate = .4m; job.FinalLearningRate = .05m;
        job.SnapshotInterval = 25;
        var clone = NewJob(); clone.ReadFromJson(job.ToJson());
        var args = clone.ComposeCommandArguments();
        Assert.Contains("no-positivity", args.Keys);
        Assert.Equal("40", args["initial-minibatch"]);
        Assert.Equal("300", args["final-minibatch"]);
        Assert.Equal("0.4", args["initial-learning-rate"]);
        Assert.Equal("0.05", args["final-learning-rate"]);
        Assert.Equal("25", args["snapshot-interval"]);
        Assert.Equal(.4m, clone.InitialLearningRate);
        Assert.False(clone.EnforcePositivity);
        clone.EnforcePositivity = true;
        Assert.DoesNotContain("no-positivity", clone.ComposeCommandArguments().Keys);
    }

    [Fact]
    public void SavedJobsWithoutSnapshotIntervalRetainTheirTenUpdatePreviewMapping()
    {
        var job = NewJob(); job.Updates = 200;
        var saved = job.ToJson(); saved.AsObject().Remove(nameof(job.SnapshotInterval));
        var restored = NewJob(); restored.ReadFromJson(saved);
        Assert.Equal(200, restored.Updates);
        Assert.Equal(10, restored.SnapshotInterval);
        Assert.Equal(10, restored.UpdateForIteration(1));
        Assert.Equal(2, restored.IterationForUpdate(11));
        Assert.Equal("10", restored.ComposeCommandArguments()["snapshot-interval"]);
    }

    [Fact]
    public void OptimizationSettingsRejectInvalidValuesAndConflictingSnapshotArguments()
    {
        var job = NewJob();
        job.InitialMinibatch = 0; job.FinalMinibatch = -1;
        job.InitialLearningRate = 0; job.FinalLearningRate = 1.1m;
        job.SnapshotInterval = 0;
        var errors = job.ValidateInputs();
        foreach (string field in new[] { nameof(job.InitialMinibatch), nameof(job.FinalMinibatch), nameof(job.InitialLearningRate), nameof(job.FinalLearningRate), nameof(job.SnapshotInterval) })
            Assert.Contains(field, errors.Keys);
        job.InitialMinibatch = 500; job.FinalMinibatch = 100;
        Assert.Contains(nameof(job.FinalMinibatch), job.ValidateInputs().Keys);
        job.AdditionalArguments = "--snapshot-interval 5";
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
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
