using System.Globalization;
using System.Text.Json;
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
    public override Type ExpandedViewType => typeof(BeamClass3DExpandedView);
    public override int2 CardSquareCount { get => new(Math.Clamp(NClasses, 2, 5), 1); set { } }

    [UiFieldGroup("Initial model", -1)]
    [UiInt("classes", "Number of classes", min: 1, max: 1000)]
    [RelayProperty] public int NClasses { get; set; } = 1;

    [UiInt("updates", "Updates", min: 1, max: 10000,
        helpText: "Number of stochastic gradient updates. Beam saves intermediate models every 10 updates.")]
    [RelayProperty] public int Updates { get; set; } = 200;

    [UiInt("working-box", "Box size", min: 16, max: 4096, stepSize: 2, unit: "px",
        helpText: "Even working box size, capped at the input box. Fourier cropping preserves the field of view and coarsens the pixel size.")]
    [RelayProperty] public int WorkingBox { get; set; } = 64;

    [UiDecimal("final-resolution", "Final resolution", min: 1, max: 1000, unit: "Å",
        helpText: "Target at the end of the resolution ramp, bounded by the working Nyquist resolution.")]
    [RelayProperty] public decimal FinalResolution { get; set; } = 12;

    [UiBool("ctf-intact-first-peak", "Keep first CTF peak intact", isAdvanced: true,
        ConditionalOnField = nameof(IsTiltSeries), ConditionalOnValue = false)]
    [RelayProperty] public bool CtfIntactFirstPeak { get; set; }

    [UiIntNullable("maximum-particles", "Maximum particles", min: 2, max: int.MaxValue, isAdvanced: true,
        helpText: "Optional deterministic subset for quick trials. The final particle output contains this subset.")]
    [RelayProperty] public int? MaximumParticles { get; set; }

    [UiInt("blob-count", "Starting blobs", min: 1, max: 1000, isAdvanced: true,
        helpText: "Positive and negative random Gaussians per starting model.")]
    [RelayProperty] public int BlobCount { get; set; } = 40;

    [RelayProperty, Clearable] public bool FinalAssignmentAvailable { get; set; }
    public override string IterationMetadataDirectory(int iteration) => Path.Combine(BeamDirectory, "warp_metadata");
    public string ParticleStar => Path.Combine(BeamDirectory, "initial_model_data.star");
    public string SummaryFile => Path.Combine(BeamDirectory, "summary.json");
    private int FinalIteration => (Updates + 9) / 10;
    private int OutputIndex(int i) => i < 0 ? Math.Max(LastCommittedIteration, VisAvailableIteration) : i;
    private int OutputUpdate(int i) => Math.Min(OutputIndex(i) * 10, Updates);
    public bool IsFinalUpdate(int i) => FinalAssignmentAvailable && OutputIndex(i) == FinalIteration;
    public string VisMap3d(int i, int c) => MapFile(OutputUpdate(i), c, IsFinalUpdate(i));
    private string MapFile(int i, int c, bool final) => Path.Combine(BeamDirectory,
        $"{(final ? "" : $"it{i:D3}_")}initial_model_class{c:D3}.mrc");
    public string VisFilteredSlices(int i, int c) => Figure(i, c, "slices");
    public string VisAngularDistribution(int i, int c) => Figure(i, c, "angles");
    public string VisFourierSampling(int i, int c) => Figure(i, c, "sampling");
    public string VisClassStats(int i) => Path.Combine(RelayResultsDirectoryPath, $"classes_it{OutputUpdate(i):D4}.json");
    private string Figure(int i, int c, string name) => Path.Combine(RelayResultsDirectoryPath, $"{name}_it{OutputUpdate(i):D4}_class{c:D3}.png");

    public BeamInit3D()
    {
        InitialResolution = 35;
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
        if (WorkingBox < 16 || WorkingBox % 2 != 0) errors[nameof(WorkingBox)] = "Box size must be even and at least 16 pixels.";
        if (FinalResolution <= 0 || FinalResolution > InitialResolution)
            errors[nameof(FinalResolution)] = "Final resolution must be positive and no coarser than the initial resolution.";
        if (IsTiltSeries && CtfIntactFirstPeak) errors[nameof(CtfIntactFirstPeak)] = "First-peak override is only supported for single-tilt images.";
        if (MaximumParticles is < 2) errors[nameof(MaximumParticles)] = "Maximum particles must be at least two or empty.";
        if (!errors.ContainsKey(nameof(AdditionalArguments)))
        {
            var custom = BeamWrapper.ParseArguments(AdditionalArguments);
            if (custom.ContainsKey("classes") || custom.ContainsKey("updates"))
                errors[nameof(AdditionalArguments)] = "Set classes and updates using the job fields.";
        }
        return errors;
    }

    protected override void AddReferenceArguments(Dictionary<string, string> args) { }
    protected override void AddRunArguments(Dictionary<string, string> args) { }
    protected override IEnumerable<string> RequiredIterationFiles(int i) =>
        Enumerable.Range(1, NClasses).Select(c => MapFile(OutputUpdate(i), c, false));

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
            .Select(update => (update + 9) / 10);
        return snapshots.Concat(HasFinalOutputs() ? [FinalIteration] : Array.Empty<int>()).Distinct().Order().ToArray();
    }

    protected override bool HasPublishedTiltMetadata(int iteration) => iteration == FinalIteration && HasFinalOutputs();

    protected override int? LogIteration(string line)
    {
        var match = Regex.Match(line, @"^update\s+(\d+)\b");
        if (!match.Success) return null;
        int update = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return update % 10 == 0 || update == Updates ? (update + 9) / 10 : null;
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
        averageVolumePath: VisMap3d(iteration, c), visualizationPaths: new()
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
            bool finalUpdate = final && i == FinalIteration;
            for (int c = 1; c <= NClasses; c++)
            {
                if (!BeamRefine3D.FigureFilesReady(VisFilteredSlices(i, c)))
                    BakeryWrapper.MapOrthosliceAtlas(MapFile(OutputUpdate(i), c, finalUpdate), 1, VisFilteredSlices(i, c), svg: true);
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
                Enumerable.Range(1, NClasses).Take(20).Select(c => MapFile(OutputUpdate(i), c, finalUpdate)).ToArray(),
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
