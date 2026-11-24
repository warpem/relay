using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.BeamClass3D;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.InitialModel.BeamInit3D;

[GenerateReadOnly]
public class BeamInit3D : BeamParticleJob
{
    public override string TypeGuid => "ac4616e4-0c29-4597-a78d-08b2fc776f4b";
    public override string TypeCategory => "💥 Beam.💥 Initial model";
    public override string TypeName => "💥 3D initial model";
    public override string TypeNameShort => "💥 Init3D";
    public override string TypeDescription => "Generate one or more initial 3D models from particle images or raw particle tilt series";
    public override string CommandName => "beam init3d";
    public override bool SupportsAlignment => false;
    public override bool UsesInitialResolution => false;
    public override bool ShowGlobalTranslationRange => true;
    public override Type ExpandedViewType => typeof(BeamClass3DExpandedView);
    public override int2 CardSquareCount { get => Class3DCardLayout.SquareCount(NClasses); set { } }

    [UiFieldGroup("Initial model", -1)]
    [UiInt("classes", "Number of classes", min: 1, max: 1000)]
    [RelayProperty] public int NClasses { get; set; } = 1;

    [UiInt("updates", "Updates", min: 1, max: 10000,
        helpText: "Number of stochastic gradient updates. Preview frequency is controlled by the snapshot interval.")]
    [RelayProperty] public int Updates { get; set; } = 200;

    [UiInt("initial-minibatch", "Initial particles per update", min: 2, max: int.MaxValue,
        helpText: "Starting scientific minibatch, total across halves and classes. Separate from the hardware batch size.")]
    [RelayProperty] public int InitialMinibatch { get; set; } = 250;

    [UiInt("final-minibatch", "Final particles per update", min: 2, max: int.MaxValue,
        helpText: "Final scientific minibatch, total across halves and classes. Must be at least the initial minibatch.")]
    [RelayProperty] public int FinalMinibatch { get; set; } = 1000;

    [UiDecimal("initial-learning-rate", "Initial learning rate", min: 0.000001, max: 1, stepSize: 0.05, isAdvanced: true,
        helpText: "Starting VDAM step size; decays exponentially over all updates to the final learning rate.")]
    [RelayProperty] public decimal InitialLearningRate { get; set; } = 0.9m;

    [UiDecimal("final-learning-rate", "Final learning rate", min: 0.000001, max: 1, stepSize: 0.05, isAdvanced: true,
        helpText: "VDAM step size reached on the final update.")]
    [RelayProperty] public decimal FinalLearningRate { get; set; } = 0.1m;

    [UiInt("snapshot-interval", "Snapshot interval", min: 1, max: 10000, unit: "updates", isAdvanced: true,
        helpText: "Publish intermediate models every this many updates. Relay groups iteration logs and previews using this interval.")]
    [RelayProperty] public int SnapshotInterval { get; set; } = 50;

    [UiDecimal("resolution", "Resolution", min: 1, max: 1000, unit: "Å", stepSize: 0.1,
        helpText: "The one resolution of the whole run. Particles are Fourier-cropped to a working pixel size of at most a third of it.")]
    [RelayProperty] public decimal Resolution { get; set; } = 12;

    [UiBool("ctf-intact-first-peak", "Keep first CTF peak intact", isAdvanced: true,
        ConditionalOnField = nameof(IsTiltSeries), ConditionalOnValue = false)]
    [RelayProperty] public bool CtfIntactFirstPeak { get; set; }

    [UiIntNullable("maximum-particles", "Maximum particles", min: 2, max: int.MaxValue, isAdvanced: true,
        helpText: "Optional random subset for quick trials. The final particle output contains this subset.")]
    [RelayProperty] public int? MaximumParticles { get; set; }

    [UiDecimal("class-similarity", "Class similarity", min: 0, max: 1, stepSize: 0.05, isAdvanced: true,
        helpText: "Early class-assignment sharing between initial models, faded out over the first 30% of updates. 0 disables sharing; 1 maximizes sharing.")]
    [RelayProperty] public decimal ClassSimilarity { get; set; } = 0.5m;

    [RelayProperty, Clearable] public bool FinalAssignmentAvailable { get; set; }
    public override Refund.Services.Storage.IntermediateRule IntermediateRule => Refund.Services.Storage.IntermediateRules.BeamInit3D;
    public override string IterationMetadataDirectory(int iteration) => Path.Combine(BeamDirectory, "warp_metadata");
    public string ParticleStar => Path.Combine(BeamDirectory, "initial_model_data.star");
    public string SummaryFile => Path.Combine(BeamDirectory, "summary.json");
    private int PreviewInterval => Math.Max(1, SnapshotInterval);
    private int FinalIteration => IterationForUpdate(Updates);
    private int OutputIndex(int i) => i < 0 ? Math.Max(LastCommittedIteration, VisAvailableIteration) : i;
    public int UpdateForIteration(int i) => Math.Min(OutputIndex(i) * PreviewInterval, Updates);
    public int IterationForUpdate(int update) => (update + PreviewInterval - 1) / PreviewInterval;
    public bool IsFinalUpdate(int i) => FinalAssignmentAvailable && OutputIndex(i) == FinalIteration;
    public string VisMap3d(int i, int c) => MapFile(UpdateForIteration(i), c, IsFinalUpdate(i));
    private string MapFile(int i, int c, bool final) => Path.Combine(BeamDirectory,
        $"{(final ? "" : $"it{i:D3}_")}initial_model_class{c:D3}.mrc");
    public string VisFilteredSlices(int i, int c) => Figure(i, c, "slices");
    public string VisAngularDistribution(int i, int c) => Figure(i, c, "angles");
    public string VisFourierSampling(int i, int c) => Figure(i, c, "sampling");
    public string VisClassStats(int i) => Path.Combine(RelayResultsDirectoryPath, $"classes_it{UpdateForIteration(i):D4}.json");
    private string Figure(int i, int c, string name) => Path.Combine(RelayResultsDirectoryPath, $"{name}_it{UpdateForIteration(i):D4}_class{c:D3}.png");

    public BeamInit3D()
    {
        PortsIn = new(PortsIn.Where(p => p.Key is not ("Reference" or "Mask")).ToDictionary(p => p.Key, p => p.Value));
        var outputs = PortsOut.ToDictionary(p => p.Key, p => p.Value);
        outputs["Reference"] = new(this, typeof(MapList), "Reference", "Initial models", GetMaps);
        PortsOut = new(outputs);
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        if (NClasses < 1) errors[nameof(NClasses)] = "At least one class is required.";
        if (Updates < 1) errors[nameof(Updates)] = "Updates must be positive.";
        if (SnapshotInterval < 1) errors[nameof(SnapshotInterval)] = "Snapshot interval must be positive.";
        if (InitialMinibatch < 2) errors[nameof(InitialMinibatch)] = "Initial particles per update must be at least two.";
        if (FinalMinibatch < 2 || FinalMinibatch < InitialMinibatch)
            errors[nameof(FinalMinibatch)] = "Final particles per update must be at least two and at least the initial count.";
        if (InitialLearningRate is <= 0 or > 1) errors[nameof(InitialLearningRate)] = "Initial learning rate must be in (0, 1].";
        if (FinalLearningRate is <= 0 or > 1) errors[nameof(FinalLearningRate)] = "Final learning rate must be in (0, 1].";
        if (ClassSimilarity is < 0 or > 1) errors[nameof(ClassSimilarity)] = "Class similarity must be between 0 and 1.";
        if (Resolution <= 0) errors[nameof(Resolution)] = "Resolution must be positive.";
        if (TranslationRange <= 0) errors[nameof(TranslationRange)] = "Translation range must be positive.";
        if (IsTiltSeries && CtfIntactFirstPeak) errors[nameof(CtfIntactFirstPeak)] = "First-peak override is only supported for single-tilt images.";
        if (MaximumParticles is < 2) errors[nameof(MaximumParticles)] = "Maximum particles must be at least two or empty.";
        if (!errors.ContainsKey(nameof(AdditionalArguments)))
        {
            var custom = BeamWrapper.ParseArguments(AdditionalArguments);
            if (custom.ContainsKey("classes") || custom.ContainsKey("updates") || custom.ContainsKey("snapshot-interval"))
                errors[nameof(AdditionalArguments)] = "Set classes, updates and snapshot interval using the job fields.";
        }
        return errors;
    }

    public override void ReadFromJson(JsonNode reader)
    {
        base.ReadFromJson(reader);
        // Existing saved jobs use ten-update preview numbering and filenames.
        if (reader[nameof(SnapshotInterval)] == null) SnapshotInterval = 10;
        // Beam replaced the annealing target --final-resolution with one run resolution.
        if (reader[nameof(Resolution)] == null && reader["FinalResolution"] is { } finalResolution)
            Resolution = (decimal)finalResolution.GetValue<double>();
    }

    protected override void AddReferenceArguments(Dictionary<string, string> args) { }
    protected override void AddRunArguments(Dictionary<string, string> args) { }
    protected override IEnumerable<string> RequiredIterationFiles(int i) =>
        Enumerable.Range(1, NClasses).Select(c => MapFile(UpdateForIteration(i), c, false));

    private bool HasFinalOutputs() => File.Exists(SummaryFile) && File.Exists(ParticleStar) &&
        Enumerable.Range(1, NClasses).All(c => File.Exists(MapFile(Updates, c, true))) &&
        (!IsTiltSeries || Directory.Exists(IterationMetadataDirectory(Updates)));

    internal override int[] CommittedIterations()
    {
        if (!Directory.Exists(BeamDirectory)) return [];
        var snapshots = Directory.EnumerateFiles(BeamDirectory, "it*_initial_model_class001.mrc")
            .Select(Path.GetFileName).Select(name => Regex.Match(name!, @"^it(\d+)_initial_model_class001\.mrc$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(update => Enumerable.Range(1, NClasses).All(c => File.Exists(MapFile(update, c, false))))
            .Select(IterationForUpdate);
        return snapshots.Concat(HasFinalOutputs() ? [FinalIteration] : Array.Empty<int>()).Distinct().Order().ToArray();
    }

    protected override bool HasPublishedTiltMetadata(int iteration) => iteration == FinalIteration && HasFinalOutputs();

    protected override int? LogIteration(string line)
    {
        // The progress bar appears before the first completed update. Mark the
        // first preview block active immediately, just like a refinement header.
        if (line.StartsWith("Ab initio updates:", StringComparison.Ordinal)) return 1;
        var match = Regex.Match(line, @"^update\s+(\d+)\b");
        if (!match.Success) return null;
        int update = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return update > 0 ? IterationForUpdate(update) : null;
    }

    protected override ParticleSet GetParticles(int iteration)
    {
        if (!IsFinalUpdate(iteration)) return null;
        var result = base.GetParticles(iteration);
        if (result == null) return null;
        result.ParticlesSingleStarPath = ParticleStar;
        result.HasClasses = true;
        return result;
    }

    protected override TiltSeriesSet GetTiltSeries(int iteration)
    {
        if (!IsFinalUpdate(iteration)) return null;
        var result = base.GetTiltSeries(iteration);
        if (result != null) result.HasReferenceBasedRefinements = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>().HasReferenceBasedRefinements;
        return result;
    }

    protected override MapList GetMaps(int iteration) => new(Enumerable.Range(1, NClasses).Select(c => new Map(
        averageVolumePath: VisMap3d(iteration, c), classId: c, visualizationPaths: new()
        {
            [Map.VisTypes.OrthoSlices] = VisFilteredSlices(iteration, c),
            [Map.VisTypes.Statistics] = VisClassStats(iteration),
            [Map.VisTypes.AngularDistribution] = IsFinalUpdate(iteration) ? VisAngularDistribution(iteration, c) : null,
            [Map.VisTypes.FourierSampling] = IsFinalUpdate(iteration) ? VisFourierSampling(iteration, c) : null
        })).ToList());

    public override Action TrackProgressResults()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        bool final = HasFinalOutputs();
        int latest = VisAvailableIteration;
        foreach (int i in CommittedIterations())
        {
            using var figureBatch = BakeryWrapper.BeginBatch();
            bool finalUpdate = final && i == FinalIteration;
            for (int c = 1; c <= NClasses; c++)
            {
                if (!BeamRefine3D.FigureFilesReady(VisFilteredSlices(i, c)))
                    BakeryWrapper.MapOrthosliceAtlas(MapFile(UpdateForIteration(i), c, finalUpdate), 1, VisFilteredSlices(i, c), svg: true);
                if (finalUpdate && (!BeamRefine3D.FigureFilesReady(VisAngularDistribution(i, c)) || !BeamRefine3D.FigureFilesReady(VisFourierSampling(i, c))))
                    BakeryWrapper.OrientationAndFourierSamplingHexBin(ParticleStar, VisAngularDistribution(i, c), VisFourierSampling(i, c),
                        Symmetry == "C1" ? null : Symmetry, svg: true, classNumber: c,
                        projectionDirectionsFile: IsTiltSeries ? Path.Combine(BeamDirectory, $"initial_model_class{c:D3}_projection_directions.npy") : null);
            }
            if (!File.Exists(VisClassStats(i)))
            {
                double[] mass = [];
                if (finalUpdate)
                {
                    using var summary = JsonDocument.Parse(File.ReadAllText(SummaryFile));
                    mass = summary.RootElement.GetProperty("final_assignment").GetProperty("class_posterior_mass")
                        .EnumerateArray().Select(v => v.GetDouble()).ToArray();
                }
                double total = mass.Sum();
                var stats = Enumerable.Range(1, NClasses).Select(c => new Class3DModel
                { Id = c, Distribution = total > 0 ? (float)(mass[c - 1] / total) : null }).ToArray();
                File.WriteAllText(VisClassStats(i), JsonSerializer.Serialize(stats));
            }
            if (!File.Exists(VisCard(i))) BakeryWrapper.Class3DJobCard(
                Enumerable.Range(1, NClasses).Take(20).Select(c => MapFile(UpdateForIteration(i), c, finalUpdate)).ToArray(),
                Enumerable.Range(1, NClasses).Take(20).ToArray(), VisCard(i));
            if (finalUpdate && IsTiltSeries && !File.Exists(TiltProcessedItems(i))) CollectTiltSeriesItems(i);
            latest = i;
        }
        int committed = CommittedIterations().DefaultIfEmpty(-1).Max();
        bool finished = File.Exists(PathSuccess);
        if (latest == VisAvailableIteration && committed == LastCommittedIteration && finished == HasRunToCompletion && final == FinalAssignmentAvailable) return null;
        return () => { VisAvailableIteration = latest; LastCommittedIteration = committed; HasRunToCompletion = finished; FinalAssignmentAvailable = final; };
    }
}
