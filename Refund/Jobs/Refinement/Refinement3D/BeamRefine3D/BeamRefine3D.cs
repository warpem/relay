using System.Globalization;
using System.Text.RegularExpressions;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;

public enum BeamAlignment { Global, Local }
public enum BeamRegularization { FSC, Denoise, Blush }
public enum BeamDevice { GPU, CPU }
public enum BeamEwald { Off, Positive, Negative }
public enum BeamLocalPivot { MaskCenter, BoundingBox, BoxCenter }

/// <summary>Single-device Beam refinement. Only committed iterations are exposed to Relay.</summary>
[GenerateReadOnly]
public class BeamRefine3D : Job, IClusterJob
{
    public override string TypeGuid => "b7e15c3b-5342-4e60-8b10-770f62a14ac8";
    public override string TypeCategory => "💥 Beam.💥 Refine 3D";
    public override string TypeName => "💥 3D refinement";
    public override string TypeNameShort => "💥 Refine3D";
    public override string TypeDescription => "Gold-standard single-particle refinement with Beam";
    public override bool IsIterative => true;
    public override bool CanBeFinalized => true;
    public override Type ExpandedViewType => typeof(BeamRefine3DExpandedView);
    public override int2 CardSquareCount { get; set; } = new(2, 1);
    public override JobQueueType QueueType => Device == BeamDevice.GPU ? JobQueueType.GPU : JobQueueType.CPU;
    public override string[] SupportedModules => ["beam", "gpu", "cpu"];
    public override string[] RequiredModules => ["beam", Device == BeamDevice.GPU ? "gpu" : "cpu"];
    public override int GpuCount => Device == BeamDevice.GPU ? 1 : 0;
    public override int ProcessCount => 1;
    public override int CoreCount => Workers;
    public override int MemoryGb => Memory;
    public override string CommandName => "beam refine";
    public override string CommandSuffix => $" && touch {JobTools.QuoteArgumentValue(PathSuccess)}";

    [UiFieldGroup("Refinement", 0)]
    [UiEnum("", "Alignment", typeof(BeamAlignment), helpText: "Global searches all orientations. Local searches around existing particle poses and requires a mask.")]
    [RelayProperty] public BeamAlignment Alignment { get; set; } = BeamAlignment.Global;

    [UiSymmetry("symmetry", "Symmetry")]
    [RelayProperty] public string Symmetry { get; set; } = "C1";

    [UiDecimal("initial-resolution", "Initial low-pass", min: 1, max: 1000, unit: "Å",
        helpText: "Low-pass applied to the starting reference.")]
    [RelayProperty] public decimal InitialResolution { get; set; } = 40;

    [UiInt("particle-diameter", "Particle diameter", min: 1, max: 10000, unit: "Å",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Global)]
    [RelayProperty] public int ParticleDiameter { get; set; } = 200;

    [UiEnum("", "Regularization", typeof(BeamRegularization),
        helpText: "FSC uses independent half-map filtering. Denoise trains a Noise2Noise model. Blush requires Beam's external Blush installation.")]
    [RelayProperty] public BeamRegularization Regularization { get; set; } = BeamRegularization.FSC;

    [UiDecimal("denoise-alignment-fsc", "Denoising alignment FSC", min: 0.01, max: 0.99, stepSize: 0.01,
        isAdvanced: true, ConditionalOnField = nameof(Regularization), ConditionalOnValue = BeamRegularization.Denoise)]
    [RelayProperty] public decimal DenoiseAlignmentFsc { get; set; } = 0.08m;

    [UiInt("", "Extra iterations", min: -1, max: 100, isAdvanced: true,
        helpText: "Iterations after convergence. -1 uses Beam's default: 0 for global refinement, 2 for local refinement.")]
    [RelayProperty] public int ExtraIterations { get; set; } = -1;

    [UiBool("dont-align-symmetry", "Don't align symmetry",
        helpText: "Leave the starting reference frame unchanged. Otherwise Beam transforms both the reference and connected mask.",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Global)]
    [RelayProperty] public bool DontAlignSymmetry { get; set; }

    [UiBool("dont-autotune-particle-count", "Always use all particles", isAdvanced: true)]
    [RelayProperty] public bool AlwaysUseAllParticles { get; set; }

    [UiDecimal("pose-marginalization-temperature", "Pose marginalization temperature", min: 0, max: 100,
        stepSize: 0.1, isAdvanced: true, helpText: "Scales the pose posterior covariance; 0 uses only the best pose.")]
    [RelayProperty] public decimal PoseMarginalizationTemperature { get; set; } = 1;

    [UiFieldGroup("Alignment", 1)]
    [UiDecimal("local-angular-range", "Local angular range", min: 0.1, max: 60, stepSize: 0.1, unit: "°",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Local)]
    [RelayProperty] public decimal LocalAngularRange { get; set; } = 5;

    [UiDecimal("translation-range", "Translation range", min: 0.1, max: 1000, unit: "Å",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Global)]
    [RelayProperty] public decimal TranslationRange { get; set; } = 40;

    [UiDecimal("translation-range", "Local translation range", min: 0.1, max: 1000, unit: "Å",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Local)]
    [RelayProperty] public decimal LocalTranslationRange { get; set; } = 10;

    [UiEnum("", "Local rotation pivot", typeof(BeamLocalPivot), isAdvanced: true,
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Local)]
    [RelayProperty] public BeamLocalPivot LocalPivot { get; set; } = BeamLocalPivot.MaskCenter;

    [UiBool("recenter-local-search", "Recenter local searches", isAdvanced: true,
        helpText: "Center each new search on the preceding iteration's best pose.",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Local)]
    [RelayProperty] public bool RecenterLocalSearch { get; set; }

    [UiBool("local-gaussian-priors", "Gaussian local priors", isAdvanced: true,
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Local)]
    [RelayProperty] public bool LocalGaussianPriors { get; set; }

    [UiDecimal("search-cache-fsc", "Search cache FSC", min: 0, max: 2, stepSize: 0.01,
        isAdvanced: true, helpText: "Minimum reference agreement for reusing global-search candidates; values above 1 disable reuse.",
        ConditionalOnField = nameof(Alignment), ConditionalOnValue = BeamAlignment.Global)]
    [RelayProperty] public decimal SearchCacheFsc { get; set; } = 0.95m;

    [UiInt("beams", "Alignment candidates", min: 1, max: 64, isAdvanced: true)]
    [RelayProperty] public int Beams { get; set; } = 20;

    [UiFieldGroup("CTF", 2)]
    [UiBool("fit-defocus", "Refine defocus")]
    [RelayProperty] public bool RefineDefocus { get; set; }

    [UiDecimal("defocus-range", "Defocus search range", min: 1, max: 10000, unit: "Å", isAdvanced: true,
        ConditionalOnField = nameof(RefineDefocus), ConditionalOnValue = true)]
    [RelayProperty] public decimal DefocusRange { get; set; } = 1000;

    [UiEnum("", "Ewald curvature", typeof(BeamEwald),
        helpText: "Positive and negative select opposite Ewald-sphere hands. Requires uncorrected particles with parametric CTF metadata.")]
    [RelayProperty] public BeamEwald Ewald { get; set; } = BeamEwald.Off;

    [UiFieldGroup("Aberrations", 3)]
    [UiBool("fit-aberrations", "Refine aberrations",
        helpText: "Fit only the selected radial orders. Even-order fits also include a constant phase offset.")]
    [RelayProperty] public bool RefineAberrations { get; set; }

    [UiBool("", "1st order (image shifts)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder1 { get; set; } = true;

    [UiBool("", "2nd order (defocus, astigmatism)",
        helpText: "Even-order fits also include the constant phase-offset term.",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder2 { get; set; } = true;

    [UiBool("", "3rd order (beam tilt/coma, trefoil)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder3 { get; set; } = true;

    [UiBool("", "4th order (spherical aberration, secondary astigmatism, tetrafoil)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder4 { get; set; } = true;

    [UiBool("", "5th order (secondary coma/trefoil, pentafoil)",
        ConditionalOnField = nameof(RefineAberrations), ConditionalOnValue = true)]
    [RelayProperty] public bool AberrationOrder5 { get; set; }

    [UiBool("fit-magnification", "Refine anisotropic magnification")]
    [RelayProperty] public bool FitMagnification { get; set; }

    [UiFieldGroup("Compute", 4)]
    [UiEnum("", "Compute device", typeof(BeamDevice))]
    [RelayProperty] public BeamDevice Device { get; set; } = BeamDevice.GPU;

    [UiInt("workers", "CPU workers", min: 1, max: 256,
        helpText: "Concurrent processing batches and CPU thread budget. More workers also use more host and GPU memory.")]
    [RelayProperty] public int Workers { get; set; } = 8;

    [UiInt("batch-size", "Particles per batch", min: 1, max: 4096, isAdvanced: true,
        helpText: "Reduce for large particle boxes or limited GPU memory.")]
    [RelayProperty] public int BatchSize { get; set; } = 64;

    [UiInt("", "Host memory", min: 1, max: 4096, unit: "GB")]
    [RelayProperty] public int Memory { get; set; } = 32;

    [UiPath("cache-dir", "Particle scratch directory", SelectionMode.SingleFolder,
        helpText: "Optional node-local scratch for particle-stack caching. Queue variables such as $TMPDIR are supported.")]
    [RelayProperty] public string CacheDirectory { get; set; } = "";

    [UiString("", "Additional arguments", isAdvanced: true,
        helpText: "Additional Beam options, e.g. --convergence-relative-improvement 0.005. Values may be quoted. Input/output paths, device, workers and resume are managed by Relay.")]
    [RelayProperty] public string AdditionalArguments { get; set; } = "";

    [RelayProperty, Clearable] public bool HasRunToCompletion { get; set; }

    public string BeamDirectory => Path.Combine(DirectoryPath, "beam");
    public string IterationFile(int iteration, string suffix) => Path.Combine(BeamDirectory,
        $"it{iteration:D3}", $"run_it{iteration:D3}_{suffix}");
    public string VisFilteredSlices(int i) => Path.Combine(RelayResultsDirectoryPath, $"slices_it{i:D4}.png");
    public string VisFsc(int i) => Path.Combine(RelayResultsDirectoryPath, $"fsc_it{i:D4}.png");
    public string VisAngularDistribution(int i) => Path.Combine(RelayResultsDirectoryPath, $"angles_it{i:D4}.png");
    public string VisFourierSampling(int i) => Path.Combine(RelayResultsDirectoryPath, $"sampling_it{i:D4}.png");
    public string VisMap3d(int i) => IterationFile(i < 0 ? VisAvailableIteration : i, "class001.mrc");
    public string VisDiagnostics(int i) => Path.Combine(RelayResultsDirectoryPath, $"diagnostics_it{i:D4}.json");
    public string VisHistory(int i) => Path.Combine(RelayResultsDirectoryPath, $"history_it{i:D4}.svg");
    public string VisCtf(int i) => Path.Combine(RelayResultsDirectoryPath, $"ctf_it{i:D4}.svg");
    public string VisCtfGroup(int i, int group) => Path.Combine(RelayResultsDirectoryPath, $"ctf_it{i:D4}_group{group}.svg");
    public string VisCtfOptics(int i) => Path.Combine(RelayResultsDirectoryPath, $"ctf_it{i:D4}_optics.tsv");

    public BeamRefine3D()
    {
        PortsIn = new(new Dictionary<string, PortIn>
        {
            ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", 1, int.MaxValue),
            ["Reference"] = new(this, typeof(MapList), "Reference", "Reference", 1, 1),
            ["Mask"] = new(this, typeof(Mask), "Mask", "Mask", 0, 1)
        });
        PortsOut = new(new Dictionary<string, PortOut>
        {
            ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", GetParticles),
            ["Reference"] = new(this, typeof(MapList), "Reference", "Refined map", GetMaps),
            ["Mask"] = new(this, typeof(Mask), "Mask", "Refinement mask", GetMask)
        });
    }

    private ParticleSet GetParticles(int iteration)
    {
        var result = ParticleInputs.Describe(PortsIn["Particles"])?.Copy();
        if (result == null) return null;
        result.ParticlesSingleStarPath = IterationFile(iteration < 0 ? VisAvailableIteration : iteration, "data.star");
        result.ParticlesMultiStarDirectory = "";
        result.ToMultiStarPath = null;
        result.ItemCount = null;
        result.HasAngles = result.HasShifts = result.HasScale = true;
        return result;
    }

    private MapList GetMaps(int iteration)
    {
        int i = iteration < 0 ? VisAvailableIteration : iteration;
        return new MapList([new Map(
            half1VolumePath: IterationFile(i, "half1_class001_unfiltered.mrc"),
            half2VolumePath: IterationFile(i, "half2_class001_unfiltered.mrc"),
            averageVolumePath: IterationFile(i, "class001.mrc"),
            maskVolumePath: GetMask(i)?.MaskVolumePath,
            fscStarPath: IterationFile(i, "model.star"), isAbsoluteScale: true)], IterationFile(i, "model.star"));
    }

    private Mask GetMask(int iteration) =>
        new(IterationFile(iteration < 0 ? VisAvailableIteration : iteration, "mask_refinement.mrc"));

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        foreach (var source in ParticleInputs.Sources(PortsIn["Particles"]))
            if (source.Resource is { } particles)
            {
                if (!particles.HasData || !particles.HasSingleStar || particles.DataDimensionality != ParticleType.Image)
                    AddPortValidationError(errors, "Particles", "Beam requires extracted 2D particles in a single STAR file.");
                if (Alignment == BeamAlignment.Local && (!particles.HasAngles || !particles.HasShifts))
                    AddPortValidationError(errors, "Particles", "Local refinement requires existing orientations and shifts.");
                if ((RefineDefocus || RefineAberrations || FitMagnification || Ewald != BeamEwald.Off) && !particles.HasCtf)
                    AddPortValidationError(errors, "Particles", "CTF refinement and Ewald correction require particle CTF metadata.");
            }
        if (Alignment == BeamAlignment.Local && PortsIn["Mask"].Count == 0)
            AddPortValidationError(errors, "Mask", "Local refinement requires a mask.");
        var maps = PortsIn["Reference"].GetSingleResource<MapList>();
        if (maps != null && (maps.Maps.Count != 1 ||
            !(maps.Maps[0].HasHalf1Volume && maps.Maps[0].HasHalf2Volume) && !maps.Maps[0].HasAverageVolume))
            AddPortValidationError(errors, "Reference", "Connect one starting reference with paired half-maps or an average map.");
        return errors;
    }

    private int[] SelectedAberrationOrders()
    {
        var orders = new List<int>();
        if (AberrationOrder2 || AberrationOrder4) orders.Add(0);
        if (AberrationOrder1) orders.Add(1);
        if (AberrationOrder2) orders.Add(2);
        if (AberrationOrder3) orders.Add(3);
        if (AberrationOrder4) orders.Add(4);
        if (AberrationOrder5) orders.Add(5);
        return orders.ToArray();
    }

    private static readonly HashSet<string> ManagedArguments = new(StringComparer.Ordinal)
    { "i", "input", "o", "output", "project-root", "resume", "device", "j", "workers", "reference", "reference-half-1", "reference-half-2", "mask", "show-progress", "dont-show-progress" };

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        try
        {
            var custom = BeamWrapper.ParseArguments(AdditionalArguments);
            foreach (string name in custom.Keys.Where(ManagedArguments.Contains))
                errors[nameof(AdditionalArguments)] = $"--{name} is managed by Relay; use the job fields and connections.";
        }
        catch (ArgumentException e) { errors[nameof(AdditionalArguments)] = e.Message; }
        if (RefineAberrations && SelectedAberrationOrders().Length == 0)
            errors[nameof(RefineAberrations)] = "Select at least one aberration order.";
        if (ExtraIterations < -1)
            errors[nameof(ExtraIterations)] = "Extra iterations must be -1 (automatic) or nonnegative.";
        if (Workers < 1 || Memory < 1 || BatchSize < 1)
            errors[nameof(Workers)] = "Workers, batch size and host memory must be positive.";
        return errors;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var errors = ValidateInputs();
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values));
        var args = base.ComposeCommandArguments();
        args["regularization"] = Regularization.ToString().ToLowerInvariant();
        args["device"] = Device == BeamDevice.GPU ? "cuda:0" : "cpu";
        if (Ewald != BeamEwald.Off) args["ewald"] = Ewald == BeamEwald.Positive ? "p-positive" : "p-negative";
        if (Alignment == BeamAlignment.Local)
            args["local-pivot"] = LocalPivot switch
            {
                BeamLocalPivot.MaskCenter => "mass", BeamLocalPivot.BoundingBox => "bbox", _ => "box-center"
            };
        if (ExtraIterations >= 0) args["extra-iterations"] = ExtraIterations.ToString(CultureInfo.InvariantCulture);
        if (RefineAberrations)
            args["aberration-orders"] = string.Join(",", SelectedAberrationOrders());
        var particles = ParticleInputs.Describe(PortsIn["Particles"])
            ?? throw new InvalidOperationException("Connect input particles.");
        var map = PortsIn["Reference"].GetSingleResource<MapList>()?.Maps.Single()
            ?? throw new InvalidOperationException("Connect one starting reference.");
        args["input"] = particles.ParticlesSingleStarPath;
        if (map.HasHalf1Volume && map.HasHalf2Volume)
        {
            args["reference-half-1"] = map.Half1VolumePath;
            args["reference-half-2"] = map.Half2VolumePath;
        }
        else args["reference"] = map.AverageVolumePath;
        args["output"] = BeamDirectory;
        args["project-root"] = Space.RootDirectory;
        if (PortsIn["Mask"].Count > 0)
        {
            args["mask"] = PortsIn["Mask"].GetSingleResource<Mask>().MaskVolumePath;
        }
        foreach (var (key, value) in BeamWrapper.ParseArguments(AdditionalArguments)) args[key] = value;
        args["show-progress"] = "";
        return args;
    }

    internal int[] CommittedIterations() => !Directory.Exists(BeamDirectory) ? [] :
        Directory.EnumerateDirectories(BeamDirectory).Select(Path.GetFileName)
            .Where(name => Regex.IsMatch(name!, @"^it\d{3,}$"))
            .Select(name => int.Parse(name!.AsSpan(2), CultureInfo.InvariantCulture))
            .Where(i => File.Exists(IterationFile(i, "data.star")) && File.Exists(IterationFile(i, "model.star")) &&
                File.Exists(IterationFile(i, "class001.mrc")) && File.Exists(IterationFile(i, "half1_class001_unfiltered.mrc")) &&
                File.Exists(IterationFile(i, "half2_class001_unfiltered.mrc")))
            .Order().ToArray();

    public override Action TrackProgressLogs()
    {
        var stderrUpdate = base.TrackProgressLogs();
        if (!File.Exists(PathStdOut)) return stderrUpdate;
        // Force-read on final collection too: shared-filesystem length metadata
        // can lag behind the last progress update. Preserve one current bar line.
        string[] lines = JobTools.CleanProgressBarLines(
            File.ReadAllText(PathStdOut).Replace("\r\n", "\n").Split('\n'));
        var starts = new SortedDictionary<int, int> { [0] = 0 };
        for (int index = 0; index < lines.Length; index++)
        {
            var match = Regex.Match(lines[index].Trim(), @"^=== Iteration (\d+) ===$");
            if (match.Success) starts[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = index;
        }
        var sections = starts.ToArray();
        for (int section = 0; section < sections.Length; section++)
        {
            int end = section + 1 < sections.Length ? sections[section + 1].Value : lines.Length;
            JobTools.WriteLogFile(string.Join('\n', lines[sections[section].Value..end]), LogFilePath(sections[section].Key));
        }
        int latest = starts.Keys.Max();
        return () => { stderrUpdate?.Invoke(); LogsAvailableIteration = latest; };
    }

    public override Action TrackProgressResults()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        int latest = VisAvailableIteration;
        bool generated = false;
        foreach (int i in CommittedIterations())
        {
            if (File.Exists(VisCard(i)) && FigureFilesReady(VisFilteredSlices(i)) && FigureFilesReady(VisFsc(i)) &&
                FigureFilesReady(VisAngularDistribution(i)) && FigureFilesReady(VisFourierSampling(i)) && File.Exists(VisDiagnostics(i)))
            { latest = Math.Max(latest, i); continue; }
            // A committed directory contains complete products. Generate Relay-owned files once.
            // Existing PNG-only jobs acquire SVG siblings on their next collection/finalization.
            if (!FigureFilesReady(VisFilteredSlices(i)))
                BakeryWrapper.MapOrthosliceAtlas(IterationFile(i, "class001.mrc"), 1, VisFilteredSlices(i), svg: true);
            if (!FigureFilesReady(VisFsc(i)))
                BakeryWrapper.FSCFromModelStar(IterationFile(i, "model.star"), VisFsc(i), svg: true,
                    directionalFscFile: File.Exists(IterationFile(i, "class001_directional_fsc.csv"))
                        ? IterationFile(i, "class001_directional_fsc.csv") : null);
            if (!FigureFilesReady(VisAngularDistribution(i)) || !FigureFilesReady(VisFourierSampling(i)))
                BakeryWrapper.OrientationAndFourierSamplingHexBin(IterationFile(i, "data.star"),
                    VisAngularDistribution(i), VisFourierSampling(i), Symmetry == "C1" ? null : Symmetry, svg: true);
            if (!File.Exists(VisCard(i)))
                BakeryWrapper.Refine3DJobCard(IterationFile(i, "class001.mrc"), IterationFile(i, "model.star"), VisCard(i));
            if (!File.Exists(VisDiagnostics(i)))
                BakeryWrapper.BeamRefinementDiagnostics(BeamDirectory, i, RelayResultsDirectoryPath);
            generated = true;
            latest = i;
        }
        bool finished = File.Exists(PathSuccess) || File.Exists(Path.Combine(BeamDirectory, "RELION_JOB_EXIT_SUCCESS"));
        if (!generated && latest == VisAvailableIteration && finished == HasRunToCompletion) return null;
        return () => { VisAvailableIteration = latest; HasRunToCompletion = finished; };
    }

    internal static bool FigureFilesReady(string pngPath) =>
        new[] { pngPath, Path.ChangeExtension(pngPath, ".svg"),
            Path.ChangeExtension(pngPath, ".light.svg"), Path.ChangeExtension(pngPath, ".dark.svg") }.All(File.Exists);

    public override void FinalizeRun(Action<Job, Action<Job>> updateCallback)
    {
        if (CommittedIterations().Length == 0)
            throw new InvalidOperationException("Beam has no complete refinement iteration to finalize.");
        var logs = TrackProgressLogs();
        if (logs != null) updateCallback(this, _ => logs());
        // Manual finalization accepts the last complete iteration of an interrupted run.
        File.WriteAllText(PathSuccess, "");
        var results = TrackProgressResults();
        if (results != null) updateCallback(this, _ => results());
    }
}
