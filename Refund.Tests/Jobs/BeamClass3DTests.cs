using System.Reflection;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.BeamClass3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;
using Refund.Utils;
using Refund.Utils.Star;
using Warp;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class BeamClass3DTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay beam classes " + Guid.NewGuid().ToString("N"));
    private BeamClass3D NewJob()
    {
        JobRegistry.EnsurePopulated();
        var job = new BeamClass3D { Id = 1, Space = new Space { RootDirectory = _root } };
        Connect(job.PortsIn["Particles"], new ParticleSet { HasData = true, HasAngles = true, HasShifts = true,
            ParticlesSingleStarPath = Path.Combine(_root, "input.star") });
        Connect(job.PortsIn["Reference"], new MapList([new Map(averageVolumePath: Path.Combine(_root, "map.mrc"))]));
        Directory.CreateDirectory(job.DirectoryPath);
        return job;
    }
    private static void Connect<T>(PortIn port, T resource) where T : Resource =>
        port.Edges.Add(new Edge { Target = port, Source = new PortOut(port.Job, typeof(T), "source", "Source", _ => resource) });

    [Fact]
    public void UnsupervisedUsesBootstrapReferenceListsAndSharedExecution()
    {
        var job = NewJob();
        var args = job.ComposeCommandArguments();
        Assert.Equal("beam class3d", job.CommandName);
        Assert.Equal("4", args["classes"]);
        Assert.Equal("25", args["iterations"]);
        Assert.Equal("fsc", args["regularization"]);
        Assert.Equal("8", args["workers"]);
        Assert.Equal(8, job.CoreCount);
        Assert.Equal(1, job.GpuCount);
        Assert.Contains("beam", job.RequiredModules);
        Assert.Contains("show-progress", args.Keys);
        Assert.DoesNotContain("extra-iterations", args.Keys);
        Assert.DoesNotContain("ewald", args.Keys);
        Assert.DoesNotContain("fit-defocus", args.Keys);
        job.Stage();
        Assert.Single(ReadReferences(job.ReferenceList(1)));
        Assert.Equal(job.ReferenceList(2), args["reference-half-2"]);
        Assert.Equal(BeamClassificationType.Unsupervised, job.ClassificationType);
        Assert.Equal(1, job.PortsIn["Reference"].MaxItems);
    }

    [Fact]
    public void SupervisedFlattensMapCollectionsAndPreservesPairedHalves()
    {
        var job = NewJob();
        job.ClassificationType = BeamClassificationType.Supervised;
        Connect(job.PortsIn["Reference"], new MapList([
            new Map(half1VolumePath: "/half 1.mrc", half2VolumePath: "/half 2.mrc", averageVolumePath: "/ignore.mrc"),
            new Map(averageVolumePath: "/third.mrc")]));
        Assert.Equal(3, job.NClasses);
        Assert.Equal(int.MaxValue, job.PortsIn["Reference"].MaxItems);
        Assert.Empty(job.ValidatePortInputs());
        Assert.Equal("3", job.ComposeCommandArguments()["classes"]);
        job.Stage();
        var half1 = ReadReferences(job.ReferenceList(1));
        var half2 = ReadReferences(job.ReferenceList(2));
        Assert.Equal(new[] { Path.Combine(_root, "map.mrc"), "/half 1.mrc", "/third.mrc" }, half1);
        Assert.Equal(new[] { Path.Combine(_root, "map.mrc"), "/half 2.mrc", "/third.mrc" }, half2);
        job.ClassificationType = BeamClassificationType.Unsupervised;
        Assert.Equal(1, job.PortsIn["Reference"].MaxItems);
        Assert.Contains("Reference", job.ValidatePortInputs().Keys);
    }

    [Fact]
    public void ModeAndClassCountRoundTripAndFieldDependenciesAreRegistered()
    {
        var job = NewJob();
        job.ClassificationType = BeamClassificationType.Supervised;
        var restored = Assert.IsType<BeamClass3D>(Job.CreateFromPolymorphicJson(Job.ToPolymorphicJson(job), job.Space, new List<User>().AsReadOnly()));
        Assert.Equal(BeamClassificationType.Supervised, restored.ClassificationType);
        Assert.Equal(int.MaxValue, restored.PortsIn["Reference"].MaxItems);
        var field = typeof(BeamClass3D).GetProperty(nameof(job.ClassCount))!.GetCustomAttribute<UiInt>()!;
        Assert.Equal(nameof(job.ClassificationType), field.ConditionalOnField);
        Assert.Equal(BeamClassificationType.Unsupervised, field.ConditionalOnValue);
        Assert.DoesNotContain(Job.TypeUiFields[typeof(BeamClass3D)].Keys, p => p.Name == "RefineDefocus");
    }

    [Fact]
    public void LocalTiltModeRequiresMaskPosesAndMetadataAndOutputsCanBeChained()
    {
        var job = NewJob();
        var source = ParticleInputs.Sources(job.PortsIn["Particles"])[0].Resource;
        source.DataDimensionality = ParticleType.Tiltseries;
        source.HasShifts = false;
        job.Alignment = BeamAlignment.Local;
        Assert.Contains("Mask", job.ValidatePortInputs().Keys);
        Assert.Contains("TiltSeries", job.ValidatePortInputs().Keys);
        Assert.Contains("Particles", job.ValidatePortInputs().Keys);
        source.HasShifts = true;
        Connect(job.PortsIn["Mask"], new Mask("/mask.mrc"));
        Connect(job.PortsIn["TiltSeries"], new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = "/xml" });
        Assert.Empty(job.ValidatePortInputs());
        Assert.Equal("/xml", job.ComposeCommandArguments()["metadata-dir"]);
        job.LastCommittedIteration = 3;
        var output = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResource());
        Assert.True(output.HasClasses);
        Assert.Equal(ParticleType.Tiltseries, output.DataDimensionality);
        Assert.Null(output.OptimisationSetStarPath);
        var maps = Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource());
        Assert.Equal(4, maps.Maps.Count);
        Assert.Equal(job.IterationFile(3, "half2_class004_unfiltered.mrc"), maps.Maps[3].Half2VolumePath);
        Assert.Equal(job.IterationMetadataDirectory(3), Assert.IsType<TiltSeriesSet>(job.PortsOut["TiltSeries"].GetResource()).LatestMetadataDirectory);
    }

    [Theory]
    [InlineData("--classes 8")]
    [InlineData("--reference other.mrc")]
    [InlineData("--metadata-dir /other")]
    [InlineData("--resume /other")]
    [InlineData("--unclosed 'value")]
    public void ManagedOrMalformedArgumentsAreRejected(string custom)
    {
        var job = NewJob();
        job.AdditionalArguments = custom;
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
    }

    [Fact]
    public void CommittedIterationsRequireEveryClassAndCanFinalizeUsingSavedFigures()
    {
        var job = NewJob();
        job.ClassCount = 2;
        foreach (var suffix in new[] { "data.star", "model.star", "class001.mrc", "half1_class001_unfiltered.mrc", "half2_class001_unfiltered.mrc" })
            Write(job.IterationFile(1, suffix), "fixture");
        Assert.Empty(job.CommittedIterations());
        foreach (var suffix in new[] { "class002.mrc", "half1_class002_unfiltered.mrc", "half2_class002_unfiltered.mrc" })
            Write(job.IterationFile(1, suffix), "fixture");
        Assert.Equal(new[] { 1 }, job.CommittedIterations());
        foreach (int c in new[] { 1, 2 })
            foreach (var png in new[] { job.VisFilteredSlices(1, c), job.VisFsc(1, c), job.VisAngularDistribution(1, c), job.VisFourierSampling(1, c) })
                foreach (string path in new[] { png, Path.ChangeExtension(png, ".svg"), Path.ChangeExtension(png, ".light.svg"), Path.ChangeExtension(png, ".dark.svg") }) Write(path, "saved");
        Write(job.VisCard(1), "saved");
        Write(job.VisClassStats(1), "[]");
        Write(job.PathStdOut, "setup\n=== Iteration 1 ===\n\rParticles: 1/2\rParticles: 2/2\n");
        Write(job.PathStdErr, "warning\n");
        job.FinalizeRun((j, action) => action(j));
        Assert.True(job.HasRunToCompletion);
        Assert.Equal(1, job.VisAvailableIteration);
        Assert.Contains("2/2", File.ReadAllText(job.LogFilePath(1)));
        Assert.DoesNotContain("1/2", File.ReadAllText(job.LogFilePath(1)));
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));
        Directory.Delete(job.BeamDirectory, true);
        Assert.Equal(2, Assert.IsType<MapList>(job.PortsOut["Reference"].GetResource()).Maps.Count);
    }

    [Fact]
    public void ClassStatisticsHandleQuotedPathsAndUndefinedResolutions()
    {
        string model = Path.Combine(_root, "model.star");
        Write(model, "data_model_classes\nloop_\n_rlnReferenceImage #1\n_rlnClassDistribution #2\n_rlnEstimatedResolution #3\n'path with spaces.mrc' .75 8.5\nsecond.mrc .25 ?\n");
        var stats = BeamClass3D.ReadClassStatistics(model);
        Assert.Equal(.75f, stats[0].Distribution);
        Assert.Equal(8.5f, stats[0].Resolution);
        Assert.Null(stats[1].Resolution);
        Assert.Equal(2, stats[1].Id);
    }

    [Fact]
    public void SelectionPreservesHalfMapsAndTiltMetadataWithoutRelionSidecars()
    {
        var parent = NewJob();
        parent.LastCommittedIteration = 2;
        var particles = ParticleInputs.Sources(parent.PortsIn["Particles"])[0].Resource;
        particles.DataDimensionality = ParticleType.Tiltseries;
        Connect(parent.PortsIn["TiltSeries"], new TiltSeriesSet { HasMetadata = true, HasCtf = true, LatestMetadataDirectory = "/xml" });
        var select = new Refund.Jobs.Refinement.Classes3D.Class3DSelect.Class3DSelect
        {
            Id = 2, Space = parent.Space, SelectedClasses = [2], SelectedIteration = 2
        };
        foreach (var pair in new[] { ("Particles", "Particles"), ("Maps", "Reference"), ("TiltSeries", "TiltSeries") })
            select.PortsIn[pair.Item1].Edges.Add(new Edge { Source = parent.PortsOut[pair.Item2], Target = select.PortsIn[pair.Item1] });
        var selected = Assert.IsType<ParticleSet>(select.PortsOut["Particles"].GetResource());
        Assert.Null(selected.OptimisationSetStarPath);
        Assert.Equal(select.ResSelectedDataStarFile, selected.ParticlesSingleStarPath);
        Assert.Equal(parent.IterationFile(2, "data.star"), Assert.IsType<ParticleSet>(parent.PortsOut["Particles"].GetResource()).ParticlesSingleStarPath);
        var maps = Assert.IsType<MapList>(select.PortsOut["Maps"].GetResource());
        Assert.Equal(parent.IterationFile(2, "half1_class002_unfiltered.mrc"), maps.Maps.Single().Half1VolumePath);
        Assert.Equal(parent.IterationMetadataDirectory(2), Assert.IsType<TiltSeriesSet>(select.PortsOut["TiltSeries"].GetResource()).LatestMetadataDirectory);
    }

    private static string[] ReadReferences(string path)
    {
        var star = new StreamingStar(path, CancellationToken.None);
        var values = new List<string>();
        star.ReadRows(star.FindBlock("model_classes"), row => values.Add(StreamingStar.Value(row[0])), CancellationToken.None);
        return values.ToArray();
    }

    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
