using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Refund.Utils.Star;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;

public enum BeamAlignment { Global, Local }
public enum BeamRegularization { FSC, Denoise, Blush }
public enum BeamDevice { GPU, CPU }
public enum BeamEwald { Off, Positive, Negative }

/// <summary>Single-device Beam refinement. Only committed iterations are exposed to Relay.</summary>
[GenerateReadOnly]
public class BeamRefine3D : BeamParticleJob
{
    public override string TypeGuid => "b7e15c3b-5342-4e60-8b10-770f62a14ac8";
    public override string TypeCategory => "💥 Beam.💥 Refine 3D";
    public override string TypeName => "💥 3D refinement";
    public override string TypeNameShort => "💥 Refine3D";
    public override string TypeDescription => "Gold-standard single-particle refinement with Beam";
    public override Type ExpandedViewType => typeof(BeamRefine3DExpandedView);
    public override int2 CardSquareCount { get; set; } = new(2, 1);
    public override string CommandName => "beam refine";

    [UiFieldGroup("Refinement", 0)]
    [UiEnum("", "Regularization", typeof(BeamRegularization),
        helpText: "FSC uses independent half-map filtering. Denoise trains a Noise2Noise model. Blush requires Beam's external Blush installation.")]
    [RelayProperty] public BeamRegularization Regularization { get; set; } = BeamRegularization.FSC;

    [UiInt("", "Extra iterations", min: -1, max: 100, isAdvanced: true,
        helpText: "Iterations after convergence. -1 uses Beam's default: 0 for global refinement, 2 for local refinement.")]
    [RelayProperty] public int ExtraIterations { get; set; } = -1;

    [UiBool("dont-autotune-particle-count", "Always use all particles", isAdvanced: true)]
    [RelayProperty] public bool AlwaysUseAllParticles { get; set; }

    [UiFieldGroup("CTF", 2)]
    [UiBool("fit-defocus", "Refine defocus",
        helpText: "Jointly refine defocus and astigmatism. Astigmatism is shared per micrograph for single-tilt data, or per physical tilt for tilt-series data.")]
    [RelayProperty] public bool RefineDefocus { get; set; }

    [UiDecimal("defocus-range", "Defocus search range", min: 1, max: 10000, unit: "Å", isAdvanced: true,
        ConditionalOnField = nameof(RefineDefocus), ConditionalOnValue = true)]
    [RelayProperty] public decimal DefocusRange { get; set; } = 1000;

    [UiEnum("", "Ewald curvature", typeof(BeamEwald),
        helpText: "Positive and negative select opposite Ewald-sphere hands. Requires uncorrected particles with parametric CTF metadata.")]
    [RelayProperty] public BeamEwald Ewald { get; set; } = BeamEwald.Off;

    [UiFieldGroup("Aberrations", 3)]
    [UiBool("fit-aberrations", "Refine aberrations",
        helpText: "Fit only the selected higher-order aberrations; defocus, astigmatism and image shifts use their dedicated settings.")]
    [RelayProperty] public bool RefineAberrations { get; set; }

    [UiBool("", "3rd order (beam tilt/coma, trefoil)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder3 { get; set; } = true;

    [UiBool("", "4th order (spherical aberration, secondary astigmatism, tetrafoil)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder4 { get; set; } = true;

    [UiBool("", "5th order (secondary coma/trefoil, pentafoil)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder5 { get; set; }

    [UiBool("fit-magnification", "Refine magnification")]
    [RelayProperty] public bool FitMagnification { get; set; }

    public string VisFilteredSlices(int i) => Path.Combine(RelayResultsDirectoryPath, $"slices_it{i:D4}.png");
    public string VisFsc(int i) => Path.Combine(RelayResultsDirectoryPath, $"fsc_it{i:D4}.png");
    public string VisAngularDistribution(int i) => Path.Combine(RelayResultsDirectoryPath, $"angles_it{i:D4}.png");
    public string VisFourierSampling(int i) => Path.Combine(RelayResultsDirectoryPath, $"sampling_it{i:D4}.png");
    public string VisMap3d(int i) => IterationFile(i < 0 ? VisAvailableIteration : i, "class001.mrc");
    public string VisDiagnostics(int i) => Path.Combine(RelayResultsDirectoryPath, $"diagnostics_it{i:D4}.json");
    public string VisHistory(int i) => Path.Combine(RelayResultsDirectoryPath, $"history_it{i:D4}.svg");
    public string VisCtf(int i) => Path.Combine(RelayResultsDirectoryPath, $"ctf_it{i:D4}.svg");

    public BeamRefine3D()
    {
        var outputs = PortsOut.ToDictionary(p => p.Key, p => p.Value);
        outputs["Mask"] = new(this, typeof(Mask), "Mask", "Refinement mask", GetMask);
        PortsOut = new(outputs);
    }

    protected override MapList GetMaps(int iteration)
    {
        int i = OutputIteration(iteration);
        return new MapList([new Map(
            half1VolumePath: IterationFile(i, "half1_class001_unfiltered.mrc"),
            half2VolumePath: IterationFile(i, "half2_class001_unfiltered.mrc"),
            averageVolumePath: IterationFile(i, "class001.mrc"),
            maskVolumePath: GetMask(i)?.MaskVolumePath,
            fscStarPath: IterationFile(i, "model.star"), isAbsoluteScale: true)], IterationFile(i, "model.star"));
    }

    private Mask GetMask(int iteration) =>
        new(IterationFile(OutputIteration(iteration), "mask_refinement.mrc"));

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        foreach (var source in ParticleInputs.Sources(PortsIn["Particles"]))
            if (source.Resource is { HasCtf: false } && !IsTiltSeries &&
                (RefineDefocus || RefineAberrations || FitMagnification || Ewald != BeamEwald.Off))
                AddPortValidationError(errors, "Particles", "CTF refinement and Ewald correction require particle CTF metadata.");
        var maps = PortsIn["Reference"].GetSingleResource<MapList>();
        if (maps != null && (maps.Maps.Count != 1 ||
            !(maps.Maps[0].HasHalf1Volume && maps.Maps[0].HasHalf2Volume) && !maps.Maps[0].HasAverageVolume))
            AddPortValidationError(errors, "Reference", "Connect one starting reference with paired half-maps or an average map.");
        return errors;
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        if (RefineAberrations && SelectedAberrationOrders().Length == 0)
            errors[nameof(RefineAberrations)] = "Select at least one aberration order.";
        if (ExtraIterations < -1)
            errors[nameof(ExtraIterations)] = "Extra iterations must be -1 (automatic) or nonnegative.";
        return errors;
    }

    private int[] SelectedAberrationOrders()
    {
        var orders = new List<int>();
        if (AberrationOrder3) orders.Add(3);
        if (AberrationOrder4) orders.Add(4);
        if (AberrationOrder5) orders.Add(5);
        return orders.ToArray();
    }

    protected override void AddRunArguments(Dictionary<string, string> args)
    {
        args["regularization"] = Regularization.ToString().ToLowerInvariant();
        if (Ewald != BeamEwald.Off) args["ewald"] = Ewald == BeamEwald.Positive ? "p-positive" : "p-negative";
        if (ExtraIterations >= 0) args["extra-iterations"] = ExtraIterations.ToString(CultureInfo.InvariantCulture);
        if (RefineAberrations) args["aberration-orders"] = string.Join(",", SelectedAberrationOrders());
        if (RefineDefocus) args["fit-astigmatism"] = "";
    }

    protected override void AddReferenceArguments(Dictionary<string, string> args)
    {
        var map = PortsIn["Reference"].GetSingleResource<MapList>()?.Maps.Single()
            ?? throw new InvalidOperationException("Connect one starting reference.");
        if (map.HasHalf1Volume && map.HasHalf2Volume)
        {
            args["reference-half-1"] = map.Half1VolumePath;
            args["reference-half-2"] = map.Half2VolumePath;
        }
        else args["reference"] = map.AverageVolumePath;
    }

    protected override IEnumerable<string> RequiredIterationFiles(int i) =>
        new[] { "data.star", "model.star", "class001.mrc", "half1_class001_unfiltered.mrc", "half2_class001_unfiltered.mrc" }
            .Select(suffix => IterationFile(i, suffix));

    public override Action TrackProgressResults()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        int latest = VisAvailableIteration;
        bool generated = false;
        int committed = CommittedIterations().DefaultIfEmpty(-1).Max();
        foreach (int i in CommittedIterations())
        {
            if (IsTiltSeries && !File.Exists(TiltProcessedItems(i))) CollectTiltSeriesItems(i);
            bool currentFigures = DiagnosticsAreCurrent(VisDiagnostics(i));
            if (currentFigures && File.Exists(VisCard(i)) && FigureFilesReady(VisFilteredSlices(i)) && FigureFilesReady(VisFsc(i)) &&
                FigureFilesReady(VisAngularDistribution(i)) && FigureFilesReady(VisFourierSampling(i)) && File.Exists(VisDiagnostics(i)))
            { latest = Math.Max(latest, i); continue; }
            // A committed directory contains complete products. Generate Relay-owned files once.
            // Existing PNG-only jobs acquire SVG siblings on their next collection/finalization.
            if (!currentFigures || !FigureFilesReady(VisFilteredSlices(i)))
                BakeryWrapper.MapOrthosliceAtlas(IterationFile(i, "class001.mrc"), 1, VisFilteredSlices(i), svg: true);
            if (!currentFigures || !FigureFilesReady(VisFsc(i)))
                BakeryWrapper.FSCFromModelStar(IterationFile(i, "model.star"), VisFsc(i), svg: true,
                    directionalFscFile: File.Exists(IterationFile(i, "class001_directional_fsc.csv"))
                        ? IterationFile(i, "class001_directional_fsc.csv") : null);
            if (!currentFigures || !FigureFilesReady(VisAngularDistribution(i)) || !FigureFilesReady(VisFourierSampling(i)))
                BakeryWrapper.OrientationAndFourierSamplingHexBin(IterationFile(i, "data.star"),
                    VisAngularDistribution(i), VisFourierSampling(i), Symmetry == "C1" ? null : Symmetry, svg: true,
                    projectionDirectionsFile: IsTiltSeries ? IterationFile(i, "projection_directions.npy") : null);
            if (!File.Exists(VisCard(i)))
                BakeryWrapper.Refine3DJobCard(IterationFile(i, "class001.mrc"), IterationFile(i, "model.star"), VisCard(i));
            if (!currentFigures)
                BakeryWrapper.BeamRefinementDiagnostics(BeamDirectory, i, RelayResultsDirectoryPath);
            generated = true;
            latest = i;
        }
        bool finished = File.Exists(PathSuccess) || File.Exists(Path.Combine(BeamDirectory, "RELION_JOB_EXIT_SUCCESS"));
        if (!generated && latest == VisAvailableIteration && finished == HasRunToCompletion && committed == LastCommittedIteration) return null;
        return () => { VisAvailableIteration = latest; LastCommittedIteration = committed; HasRunToCompletion = finished; };
    }

    internal static bool DiagnosticsAreCurrent(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int value) && value == 2;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static bool FigureFilesReady(string pngPath) =>
        new[] { pngPath, Path.ChangeExtension(pngPath, ".svg"),
            Path.ChangeExtension(pngPath, ".light.svg"), Path.ChangeExtension(pngPath, ".dark.svg") }.All(File.Exists);

}
