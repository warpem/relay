using System.Reflection;
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

    [Theory]
    [InlineData("{\"version\":1}", false)]
    [InlineData("{\"version\":2}", true)]
    [InlineData("{\"version\":\"2\"}", false)]
    [InlineData("{}", false)]
    [InlineData("invalid", false)]
    public void FigureVersionRejectsStaleOrIncompleteOutputs(string json, bool expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal(expected, BeamRefine3D.DiagnosticsAreCurrent(path));
        }
        finally { File.Delete(path); }
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
        var args = job.ComposeCommandArguments();
        Assert.Equal("5", args["local-angular-range"]);
        Assert.Equal("10", args["translation-range"]);
        Assert.False(args.ContainsKey("local-pivot"));
        Assert.False(args.ContainsKey("denoise-alignment-fsc"));
        Assert.False(args.ContainsKey("dont-align-symmetry"));
        Assert.False(args.ContainsKey("particle-diameter"));
        Assert.Empty(job.ValidatePortInputs());
    }

    [Fact]
    public void EnumSelectionsAndDependenciesControlCommandAndVisibility()
    {
        var job = NewJob();
        job.Device = BeamDevice.CPU;
        job.Ewald = BeamEwald.Negative;
        job.RefineDefocus = job.RefineAberrations = true;
        var args = job.ComposeCommandArguments();
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal("p-negative", args["ewald"]);
        Assert.True(args.ContainsKey("fit-defocus"));
        Assert.True(args.ContainsKey("fit-astigmatism"));
        Assert.True(args.ContainsKey("fit-aberrations"));
        Assert.True(args.ContainsKey("defocus-range"));
        job.RefineDefocus = job.RefineAberrations = false;
        Assert.False(job.ComposeCommandArguments().ContainsKey("defocus-range"));
        foreach (string field in new[] { nameof(job.LocalAngularRange), nameof(job.DefocusRange) })
            Assert.False(Job.IsPropertyVisible(typeof(BeamRefine3D), typeof(BeamRefine3D).GetProperty(field), job.AsReadOnly()));
    }

    [Fact]
    public void CustomOptionsOverrideScientificDefaultsAndPreserveQuotedValues()
    {
        var job = NewJob();
        job.AdditionalArguments = "--beams 12 --blush-model 'my model.pt' --sharpen-half-maps=0.5 --fit-phase";
        var args = job.ComposeCommandArguments();
        Assert.Equal("12", args["beams"]);
        Assert.Equal("my model.pt", args["blush-model"]);
        Assert.Equal("0.5", args["sharpen-half-maps"]);
        Assert.Equal("", args["fit-phase"]);
        job.AdditionalArguments = "--output /different";
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
        Assert.Throws<ArgumentException>(() => job.ComposeCommandArguments());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalizationUsesCommittedIterationsAndPersistsLogs(bool tilt)
    {
        var job = NewJob();
        if (tilt)
        {
            ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource.DataDimensionality = ParticleType.Tiltseries;
            Connect(job.PortsIn["TiltSeries"], new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = "/xml" });
        }
        foreach (int i in new[] { 1, 2 })
        {
            foreach (string suffix in new[] { "data.star", "model.star", "class001.mrc", "half1_class001_unfiltered.mrc", "half2_class001_unfiltered.mrc" })
                Write(job.IterationFile(i, suffix), "committed");
            foreach (string path in new[] { job.VisCard(i), job.VisFilteredSlices(i), job.VisFsc(i), job.VisAngularDistribution(i), job.VisFourierSampling(i) })
                Write(path, "visualized");
            foreach (string path in new[] { job.VisFilteredSlices(i), job.VisFsc(i), job.VisAngularDistribution(i), job.VisFourierSampling(i) })
                foreach (string extension in new[] { ".svg", ".light.svg", ".dark.svg" })
                    Write(Path.ChangeExtension(path, extension), "visualized");
            Write(job.VisDiagnostics(i), "{\"version\":2,\"history\":false,\"ctf\":null}");
        }
        if (tilt)
            foreach (int i in new[] { 1, 2 })
                Write(Path.Combine(job.IterationMetadataDirectory(i), "TS.xml"), "<TiltSeries />");
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
    public void FigureCollectionRequiresBothFixedThemeDownloads()
    {
        string png = Path.Combine(_root, "fsc.png");
        Write(png, "png");
        Write(Path.ChangeExtension(png, ".svg"), "adaptive");
        Write(Path.ChangeExtension(png, ".light.svg"), "light");
        Assert.False(BeamRefine3D.FigureFilesReady(png));
        Write(Path.ChangeExtension(png, ".dark.svg"), "dark");
        Assert.True(BeamRefine3D.FigureFilesReady(png));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PairedHalfMapsTakePrecedenceOverAnAverageReference(bool withAverage)
    {
        var job = NewJob();
        job.PortsIn["Reference"].Edges.Clear();
        Connect(job.PortsIn["Reference"], new MapList([new Map(
            half1VolumePath: "/data/half1.mrc", half2VolumePath: "/data/half2.mrc",
            averageVolumePath: withAverage ? "/data/average.mrc" : null)]));
        Assert.Empty(job.ValidatePortInputs());
        var args = job.ComposeCommandArguments();
        Assert.Equal("/data/half1.mrc", args["reference-half-1"]);
        Assert.Equal("/data/half2.mrc", args["reference-half-2"]);
        Assert.False(args.ContainsKey("reference"));
    }

    [Fact]
    public void NewControlsComposeIndependentAberrationOrdersAndComputeDefaults()
    {
        var job = NewJob();
        Assert.Equal(8, job.Workers);
        Assert.Equal(8, job.CoreCount);
        Assert.Equal("8", job.ComposeCommandArguments()["workers"]);
        Assert.False(job.ComposeCommandArguments().ContainsKey("extra-iterations"));
        job.RefineDefocus = true;
        job.RefineAberrations = true;
        job.AberrationOrder3 = job.AberrationOrder4 = false;
        job.AberrationOrder5 = true;
        job.FitMagnification = true;
        job.ExtraIterations = 3;
        job.AlwaysUseAllParticles = true;
        job.PoseMarginalizationTemperature = 0;
        job.DontAlignSymmetry = true;
        var args = job.ComposeCommandArguments();
        Assert.Equal("5", args["aberration-orders"]);
        Assert.True(args.ContainsKey("fit-defocus"));
        Assert.True(args.ContainsKey("fit-astigmatism"));
        Assert.True(args.ContainsKey("fit-aberrations"));
        Assert.True(args.ContainsKey("fit-magnification"));
        Assert.True(args.ContainsKey("dont-align-symmetry"));
        Assert.True(args.ContainsKey("dont-autotune-particle-count"));
        Assert.Equal("3", args["extra-iterations"]);
        Assert.False(args.ContainsKey("search-cache-fsc"));
        Assert.Equal("0", args["pose-marginalization-temperature"]);
        job.AberrationOrder5 = false;
        Assert.Contains(nameof(job.RefineAberrations), job.ValidateInputs().Keys);
        job.AberrationOrder4 = true;
        Assert.Equal("4", job.ComposeCommandArguments()["aberration-orders"]);
    }

    [Fact]
    public void FieldVisibilityAndEditorsMatchTheRefinementControls()
    {
        var job = NewJob();
        foreach (string name in new[] { nameof(job.CacheDirectory), nameof(job.FitMagnification), nameof(job.Ewald), nameof(job.DontAlignSymmetry) })
            Assert.False(typeof(BeamRefine3D).GetProperty(name)!.GetCustomAttribute<UiFieldBase>()!.IsAdvanced);
        foreach (string name in new[] { nameof(job.ExtraIterations), nameof(job.AlwaysUseAllParticles), nameof(job.PoseMarginalizationTemperature) })
            Assert.True(typeof(BeamRefine3D).GetProperty(name)!.GetCustomAttribute<UiFieldBase>()!.IsAdvanced);
        Assert.NotNull(typeof(BeamRefine3D).GetProperty(nameof(job.AdditionalArguments))!.GetCustomAttribute<UiString>());
        var order = typeof(BeamRefine3D).GetProperty(nameof(job.AberrationOrder5))!;
        Assert.False(Job.IsPropertyVisible(typeof(BeamRefine3D), order, job.AsReadOnly()));
        job.RefineAberrations = true;
        Assert.True(Job.IsPropertyVisible(typeof(BeamRefine3D), order, job.AsReadOnly()));
        Connect(job.PortsIn["Mask"], new Mask("/data/mask.mrc"));
        Assert.False(job.ComposeCommandArguments().ContainsKey("dont-align-symmetry"));
        job.DontAlignSymmetry = true;
        Assert.True(job.ComposeCommandArguments().ContainsKey("dont-align-symmetry"));
        job.VisAvailableIteration = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(job.IterationFile(1, "mask_refinement.mrc"))!);
        File.WriteAllText(job.IterationFile(1, "mask_refinement.mrc"), "mask");
        var mask = Assert.IsType<Mask>(job.PortsOut["Mask"].GetResource());
        Assert.Equal(job.IterationFile(1, "mask_refinement.mrc"), mask.MaskVolumePath);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void MaskOutputsUseTheExistingIterationMaskOrPreparedMask(bool automatic, bool prepared)
    {
        var job = NewJob();
        job.VisAvailableIteration = 9;
        string autoPath = job.IterationFile(9, "mask_refinement.mrc");
        string preparedPath = Path.Combine(job.BeamDirectory, "run_mask.mrc");
        Directory.CreateDirectory(Path.GetDirectoryName(autoPath)!);
        if (automatic) File.WriteAllText(autoPath, "automatic mask");
        if (prepared) File.WriteAllText(preparedPath, "prepared mask");
        string? expected = automatic ? autoPath : prepared ? preparedPath : null;

        var mask = job.PortsOut["Mask"].GetResource() as Mask;
        Assert.Equal(expected, mask?.MaskVolumePath);
        var maps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource());
        Assert.Equal(expected, maps.Maps.Single().MaskVolumePath);
    }

    [Fact]
    public void FinalizationRejectsAnEmptyOrUncommittedJob()
    {
        var job = NewJob();
        Assert.Throws<InvalidOperationException>(() => job.FinalizeRun((j, action) => action(j)));
        Assert.False(File.Exists(job.PathSuccess));
    }

    [Fact]
    public void TiltMetadataIsRequiredOnlyForTiltStacksAndLimitsAreConditional()
    {
        var job = NewJob();
        job.MaxTilts = 12;
        Assert.False(job.ComposeCommandArguments().ContainsKey("max-tilts"));
        var field = typeof(BeamRefine3D).GetProperty(nameof(job.MaxTilts))!;
        Assert.False(Job.IsPropertyVisible(typeof(BeamRefine3D), field, job.AsReadOnly()));
        Connect(job.PortsIn["TiltSeries"], new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = "/xml" });
        Assert.Contains("TiltSeries", job.ValidatePortInputs().Keys);
        var particles = ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource;
        particles.DataDimensionality = ParticleType.Tiltseries;
        particles.HasCtf = false; // CTF parameters come from XML for this modality.
        job.RefineDefocus = true;
        job.Ewald = BeamEwald.Positive;
        Assert.Empty(job.ValidatePortInputs());
        var args = job.ComposeCommandArguments();
        Assert.Equal("/xml", args["metadata-dir"]);
        Assert.Equal("12", args["max-tilts"]);
        Assert.True(args.ContainsKey("fit-defocus"));
        Assert.True(args.ContainsKey("fit-astigmatism"));
        Assert.False(args.ContainsKey("tilt-damage-threshold"));
        Assert.True(Job.IsPropertyVisible(typeof(BeamRefine3D), field, job.AsReadOnly()));
        job.AdditionalArguments = "--metadata-dir /elsewhere";
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
        job.PortsIn["TiltSeries"].Edges.Clear();
        Assert.Contains("TiltSeries", job.ValidatePortInputs().Keys);
    }

    [Fact]
    public void TiltOutputsUseLatestCommitEvenWhenPreviewsAreUnavailableAndCanBeChained()
    {
        var job = NewJob();
        var particles = ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource;
        particles.DataDimensionality = ParticleType.Tiltseries;
        particles.TomogramsStarPath = "/stale/tomograms.star";
        particles.OptimisationSetStarPath = "/stale/optimisation.star";
        string inputList = Path.Combine(_root, "processed.json");
        Write(inputList, "[{\"Path\":\"/data/TS.tomostar\",\"Extra\":123},{\"Path\":\"/data/unused.tomostar\"}]");
        var source = new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = "/original", ProcessedItemsJson = inputList };
        Connect(job.PortsIn["TiltSeries"], source);
        foreach (int i in new[] { 1, 2 })
        {
            foreach (string suffix in new[] { "data.star", "model.star", "class001.mrc", "half1_class001_unfiltered.mrc", "half2_class001_unfiltered.mrc" })
                Write(job.IterationFile(i, suffix), "committed");
            Write(Path.Combine(job.IterationMetadataDirectory(i), "TS.xml"), "<TiltSeries />");
        }
        Write(job.PathStdOut, "=== Iteration 1 ===\nfirst\n=== Iteration 2 ===\nsecond");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal(2, job.LastCommittedIteration);
        Assert.Equal(-1, job.VisAvailableIteration);
        var refined = Assert.IsType<TiltSeriesSet>(job.PortsOut["TiltSeries"].GetResource());
        Assert.Equal(job.IterationMetadataDirectory(2), refined.LatestMetadataDirectory);
        Assert.Equal("/original", source.LatestMetadataDirectory);
        Assert.True(refined.HasReferenceBasedRefinements);
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(refined.ProcessedItemsJson)))
        {
            Assert.Equal(1, json.RootElement.GetArrayLength());
            Assert.Equal(123, json.RootElement[0].GetProperty("Extra").GetInt32());
        }
        Assert.Equal("[]", File.ReadAllText(refined.FailedItemsJson));
        var output = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResource());
        Assert.Equal(job.IterationFile(2, "data.star"), output.ParticlesSingleStarPath);
        Assert.Equal(ParticleType.Tiltseries, output.DataDimensionality);
        Assert.Null(output.TomogramsStarPath);
        Assert.Null(output.OptimisationSetStarPath);
        Assert.Equal(job.IterationMetadataDirectory(1), Assert.IsType<TiltSeriesSet>(job.PortsOut["TiltSeries"].GetResource(1)).LatestMetadataDirectory);
        var next = NewJob();
        next.PortsIn["Particles"].Edges.Clear();
        Connect(next.PortsIn["Particles"], output);
        Connect(next.PortsIn["TiltSeries"], refined);
        Assert.Empty(next.ValidatePortInputs());
        Assert.Equal(output.ParticlesSingleStarPath, next.ComposeCommandArguments()["input"]);
        Assert.Equal(refined.LatestMetadataDirectory, next.ComposeCommandArguments()["metadata-dir"]);
    }

    [Fact]
    public void TiltStagingMergesWithoutRelionSidecarsAndChecksRawStacksAndXmlNames()
    {
        var job = NewJob();
        var particles = ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource;
        particles.DataDimensionality = ParticleType.Tiltseries;
        string metadata = Path.Combine(_root, "xml");
        Connect(job.PortsIn["TiltSeries"], new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = metadata });
        string star = "data_optics\nloop_\n_rlnOpticsGroup\n_rlnOpticsGroupName\n_rlnCtfDataAreCtfPremultiplied\n1 optics 0\n\ndata_particles\nloop_\n_rlnOpticsGroup\n_rlnTomoName\n_rlnImageName\n1 TS stack.mrcs\n";
        Write(particles.ParticlesSingleStarPath, star);
        string second = Path.Combine(_root, "second.star");
        Write(second, star);
        Connect(job.PortsIn["Particles"], new ParticleSet { HasData = true, HasCtf = true, DataDimensionality = ParticleType.Tiltseries, ParticlesSingleStarPath = second });
        Assert.Empty(job.ValidatePortInputs());
        Write(Path.Combine(metadata, "TS.xml"), "<TiltSeries />");
        job.Stage(CancellationToken.None);
        var merged = ParticleInputs.Describe(job.PortsIn["Particles"]);
        Assert.True(File.Exists(merged.ParticlesSingleStarPath));
        Assert.Null(merged.TomogramsStarPath);
        Assert.Contains("2 TS stack.mrcs", File.ReadAllText(merged.ParticlesSingleStarPath));
        File.Delete(Path.Combine(metadata, "TS.xml"));
        Assert.Throws<FileNotFoundException>(() => job.Stage());
        Write(Path.Combine(metadata, "TS.xml"), "<TiltSeries />");
        Write(second, star.Replace("1 optics 0", "1 optics 1"));
        Assert.Contains("Re-extract", Assert.Throws<InvalidDataException>(() => job.Stage()).Message);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
