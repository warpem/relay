using System.Globalization;
using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;
using Refund.Utils;
using Refund.Utils.Star;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Classes3D.BeamClass3D;

public enum BeamClassificationType { Unsupervised, Supervised }
public enum BeamClassificationRegularization { FSC, Denoise, Blush }

[GenerateReadOnly]
public class BeamClass3D : BeamParticleJob
{
    public override string TypeGuid => "86aec998-374c-437f-a765-b50db0d74047";
    public override string TypeCategory => "💥 Beam.💥 Classify 3D";
    public override string TypeName => "💥 3D classification";
    public override string TypeNameShort => "💥 Class3D";
    public override string TypeDescription => "Supervised or unsupervised 3D classification of images or particle tilt series with Beam";
    public override string CommandName => "beam class3d";
    public override Type ExpandedViewType => typeof(BeamClass3DExpandedView);
    public override int2 CardSquareCount { get => Class3DCardLayout.SquareCount(NClasses); set { } }

    private BeamClassificationType _classificationType;
    [UiFieldGroup("Classification", -1)]
    [UiEnum("", "Classification type", typeof(BeamClassificationType),
        helpText: "Unsupervised replicates one reference into K classes. Supervised starts from one reference per class, including all maps in connected collections.")]
    [RelayProperty]
    public BeamClassificationType ClassificationType
    {
        get => _classificationType;
        set
        {
            _classificationType = value;
            PortsIn["Reference"].MaxItems = value == BeamClassificationType.Supervised ? int.MaxValue : 1;
        }
    }

    [UiInt("", "Number of classes", min: 2, max: 1000,
        ConditionalOnField = nameof(ClassificationType), ConditionalOnValue = BeamClassificationType.Unsupervised)]
    [RelayProperty] public int ClassCount { get; set; } = 4;

    [UiStatic("", "Number of classes", ConditionalOnField = nameof(ClassificationType), ConditionalOnValue = BeamClassificationType.Supervised)]
    public int NClasses => ClassificationType == BeamClassificationType.Unsupervised ? ClassCount : ReferenceMaps().Count;

    [UiInt("iterations", "Iterations", min: 1, max: 10000)]
    [RelayProperty] public int Iterations { get; set; } = 25;

    [UiInt("working-box", "Box size", min: 16, max: 4096, stepSize: 2, unit: "px",
        helpText: "Even working box size, capped at the input box. Fourier cropping preserves the field of view and coarsens the pixel size.")]
    [RelayProperty] public int WorkingBox { get; set; } = 128;

    [UiEnum("", "Regularization", typeof(BeamClassificationRegularization),
        helpText: "Denoise trains one Noise2Noise network across all usable classes. Blush uses RELION's pretrained denoiser and requires Beam's external Blush installation. Class FSC is a controller diagnostic; refine selected particles independently for a final resolution estimate.")]
    [RelayProperty] public BeamClassificationRegularization Regularization { get; set; } = BeamClassificationRegularization.FSC;

    public string ReferenceList(int half) => Path.Combine(DirectoryPath, $"references_half{half}.star");
    public string VisFilteredSlices(int i, int c) => Figure(i, c, "slices", "png");
    public string VisFsc(int i, int c) => Figure(i, c, "fsc_column", "png");
    public string VisAngularDistribution(int i, int c) => Figure(i, c, "angles", "png");
    public string VisFourierSampling(int i, int c) => Figure(i, c, "sampling", "png");
    public string VisMap3d(int i, int c) => IterationFile(OutputIteration(i), $"class{c:D3}.mrc");
    public string VisClassStats(int i) => Path.Combine(RelayResultsDirectoryPath, $"classes_it{OutputIteration(i):D4}.json");
    private string Figure(int i, int c, string kind, string ext) => Path.Combine(RelayResultsDirectoryPath, $"{kind}_it{OutputIteration(i):D4}_class{c:D3}.{ext}");

    public BeamClass3D()
    {
        var outputs = PortsOut.ToDictionary(p => p.Key, p => p.Value);
        outputs["Reference"] = new(this, typeof(MapList), "Reference", "3D classes", GetMaps);
        PortsOut = new(outputs);
    }

    private List<Map> ReferenceMaps() => PortsIn["Reference"].Edges
        .SelectMany(e => (e.Source.GetResource() as MapList)?.Maps.AsEnumerable() ?? Enumerable.Empty<Map>()).ToList();

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        var maps = ReferenceMaps();
        if (maps.Count > 0 && ClassificationType == BeamClassificationType.Unsupervised && maps.Count != 1)
            AddPortValidationError(errors, "Reference", "Unsupervised classification requires exactly one starting map.");
        if (ClassificationType == BeamClassificationType.Supervised && maps.Count < 2)
            AddPortValidationError(errors, "Reference", "Supervised classification requires at least two starting maps.");
        if (maps.Any(m => !(m.HasHalf1Volume && m.HasHalf2Volume) && !m.HasAverageVolume))
            AddPortValidationError(errors, "Reference", "Every reference needs paired half-maps or an average map.");
        return errors;
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        if (NClasses < 2) errors[nameof(ClassCount)] = "Classification requires at least two classes.";
        if (WorkingBox < 16 || WorkingBox % 2 != 0) errors[nameof(WorkingBox)] = "Box size must be even and at least 16 pixels.";
        if (Iterations < 1) errors[nameof(Iterations)] = "Iterations must be positive.";
        if (!errors.ContainsKey(nameof(AdditionalArguments)) && BeamWrapper.ParseArguments(AdditionalArguments).ContainsKey("classes"))
            errors[nameof(AdditionalArguments)] = "Use the classification type and number of classes fields to set --classes.";
        return errors;
    }

    protected override void AddRunArguments(Dictionary<string, string> args)
    {
        args["classes"] = NClasses.ToString(CultureInfo.InvariantCulture);
        args["regularization"] = Regularization.ToString().ToLowerInvariant();
    }

    protected override void AddReferenceArguments(Dictionary<string, string> args)
    {
        var maps = ReferenceMaps();
        if (maps.Count == 0) throw new InvalidOperationException("Connect starting references.");
        // Explicit paired lists also support mixtures of paired and averaged references.
        // An averaged reference seeds both halves for that class.
        args["reference-half-1"] = ReferenceList(1);
        args["reference-half-2"] = ReferenceList(2);
    }

    public override void Stage()
    {
        base.Stage();
        var maps = ReferenceMaps();
        for (int half = 1; half <= 2; half++)
        {
            var paths = maps.Select(map => map.HasHalf1Volume && map.HasHalf2Volume
                ? half == 1 ? map.Half1VolumePath : map.Half2VolumePath
                : map.AverageVolumePath);
            File.WriteAllText(ReferenceList(half), "data_model_classes\n\nloop_\n_rlnReferenceImage #1\n" +
                string.Join("\n", paths.Select(StreamingStar.Quote)) + "\n");
        }
    }

    protected override ParticleSet GetParticles(int iteration)
    {
        var result = base.GetParticles(iteration);
        if (result != null) result.HasClasses = true;
        return result;
    }

    protected override TiltSeriesSet GetTiltSeries(int iteration)
    {
        var result = base.GetTiltSeries(iteration);
        if (result != null)
            result.HasReferenceBasedRefinements = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>().HasReferenceBasedRefinements;
        return result;
    }

    protected override MapList GetMaps(int iteration)
    {
        int i = OutputIteration(iteration);
        return new MapList(Enumerable.Range(1, NClasses).Select(c => new Map(
            half1VolumePath: IterationFile(i, $"half1_class{c:D3}_unfiltered.mrc"),
            half2VolumePath: IterationFile(i, $"half2_class{c:D3}_unfiltered.mrc"),
            averageVolumePath: VisMap3d(i, c), isAbsoluteScale: true,
            visualizationPaths: new()
            {
                [Map.VisTypes.OrthoSlices] = VisFilteredSlices(i, c), [Map.VisTypes.Fsc] = VisFsc(i, c),
                [Map.VisTypes.AngularDistribution] = VisAngularDistribution(i, c),
                [Map.VisTypes.FourierSampling] = VisFourierSampling(i, c), [Map.VisTypes.Statistics] = VisClassStats(i)
            })).ToList(), IterationFile(i, "model.star"));
    }

    protected override IEnumerable<string> RequiredIterationFiles(int i) =>
        new[] { IterationFile(i, "data.star"), IterationFile(i, "model.star") }.Concat(
            Enumerable.Range(1, NClasses).SelectMany(c => new[] { $"class{c:D3}.mrc", $"half1_class{c:D3}_unfiltered.mrc", $"half2_class{c:D3}_unfiltered.mrc" }
                .Select(suffix => IterationFile(i, suffix))));

    internal static Class3DModel[] ReadClassStatistics(string path)
    {
        var star = new StreamingStar(path, CancellationToken.None);
        var table = star.FindBlock("model_classes") ?? throw new InvalidDataException("Missing class statistics.");
        var result = new List<Class3DModel>();
        float? Number(string[] row, string column)
        {
            int index = table.Columns.IndexOf(column);
            return index >= 0 && float.TryParse(StreamingStar.Value(row[index]), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value) ? value : null;
        }
        star.ReadRows(table, row => result.Add(new Class3DModel
        {
            Id = result.Count + 1, Distribution = Number(row, "rlnClassDistribution"),
            Resolution = Number(row, "rlnEstimatedResolution")
        }), CancellationToken.None);
        return result.ToArray();
    }

    public override Action TrackProgressResults()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        int latest = VisAvailableIteration;
        foreach (int i in CommittedIterations())
        {
            if (IsTiltSeries && !File.Exists(TiltProcessedItems(i))) CollectTiltSeriesItems(i);
            for (int c = 1; c <= NClasses; c++)
            {
                if (!BeamRefine3D.FigureFilesReady(VisFilteredSlices(i, c)))
                    BakeryWrapper.MapOrthosliceAtlas(VisMap3d(i, c), 1, VisFilteredSlices(i, c), svg: true);
                if (!BeamRefine3D.FigureFilesReady(VisFsc(i, c)))
                    BakeryWrapper.FSCFromModelStar(IterationFile(i, "model.star"), VisFsc(i, c), svg: true,
                        directionalFscFile: IterationFile(i, $"class{c:D3}_directional_fsc.csv"), classNumber: c, compact: true);
                if (!BeamRefine3D.FigureFilesReady(VisAngularDistribution(i, c)) || !BeamRefine3D.FigureFilesReady(VisFourierSampling(i, c)))
                    BakeryWrapper.OrientationAndFourierSamplingHexBin(IterationFile(i, "data.star"), VisAngularDistribution(i, c),
                        VisFourierSampling(i, c), Symmetry == "C1" ? null : Symmetry, svg: true,
                        projectionDirectionsFile: IsTiltSeries ? IterationFile(i, $"class{c:D3}_projection_directions.npy") : null, classNumber: c);
            }
            if (!File.Exists(VisClassStats(i)))
            {
                File.WriteAllText(VisClassStats(i), JsonSerializer.Serialize(ReadClassStatistics(IterationFile(i, "model.star"))));
            }
            if (!File.Exists(VisCard(i))) BakeryWrapper.Class3DJobCard(
                Enumerable.Range(1, NClasses).Take(20).Select(c => VisMap3d(i, c)).ToArray(),
                Enumerable.Range(1, NClasses).Take(20).ToArray(), VisCard(i), IterationFile(i, "model.star"));
            latest = i;
        }
        int committed = CommittedIterations().DefaultIfEmpty(-1).Max();
        bool finished = File.Exists(PathSuccess) || File.Exists(Path.Combine(BeamDirectory, "RELION_JOB_EXIT_SUCCESS"));
        if (latest == VisAvailableIteration && committed == LastCommittedIteration && finished == HasRunToCompletion) return null;
        return () => { VisAvailableIteration = latest; LastCommittedIteration = committed; HasRunToCompletion = finished; };
    }
}
