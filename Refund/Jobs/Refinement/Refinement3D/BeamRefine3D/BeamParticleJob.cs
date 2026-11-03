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

/// <summary>Shared execution, staging and persisted logs for Beam particle jobs.</summary>
[GenerateReadOnly]
public abstract class BeamParticleJob : Job, IClusterJob
{
    public override bool IsIterative => true;
    public override bool CanBeFinalized => true;
    public override JobQueueType QueueType => Device == BeamDevice.GPU ? JobQueueType.GPU : JobQueueType.CPU;
    public override string[] SupportedModules => ["beam", "gpu", "cpu"];
    public override string[] RequiredModules => ["beam", Device == BeamDevice.GPU ? "gpu" : "cpu"];
    public override int GpuCount => Device == BeamDevice.GPU ? 1 : 0;
    public override int ProcessCount => 1;
    public override int CoreCount => Workers;
    public override int MemoryGb => Memory;
    public override string CommandSuffix => $" && touch {JobTools.QuoteArgumentValue(PathSuccess)}";

    public virtual bool SupportsAlignment => true;
    public bool UsesGlobalSearch => SupportsAlignment && Alignment == BeamAlignment.Global;
    public bool UsesLocalSearch => SupportsAlignment && Alignment == BeamAlignment.Local;
    public bool ShowParticleDiameter => !SupportsAlignment || UsesGlobalSearch;

    [UiFieldGroup("Reference", 0)]
    [UiEnum("", "Alignment", typeof(BeamAlignment), helpText: "Global searches all orientations. Local searches around existing particle poses and requires a mask.",
        ConditionalOnField = nameof(SupportsAlignment), ConditionalOnValue = true)]
    [RelayProperty] public BeamAlignment Alignment { get; set; } = BeamAlignment.Global;

    [UiSymmetry("symmetry", "Symmetry")]
    [RelayProperty] public string Symmetry { get; set; } = "C1";

    [UiDecimal("initial-resolution", "Initial low-pass", min: 1, max: 1000, unit: "Å",
        helpText: "Initial low-pass resolution.")]
    [RelayProperty] public decimal InitialResolution { get; set; } = 40;

    [UiInt("particle-diameter", "Particle diameter", min: 1, max: 10000, unit: "Å",
        ConditionalOnField = nameof(ShowParticleDiameter), ConditionalOnValue = true)]
    [RelayProperty] public int ParticleDiameter { get; set; } = 200;

    [UiBool("dont-align-symmetry", "Don't align symmetry",
        helpText: "Leave the starting reference frame unchanged. Otherwise Beam transforms both the reference and connected mask.",
        ConditionalOnField = nameof(UsesGlobalSearch), ConditionalOnValue = true)]
    [RelayProperty] public bool DontAlignSymmetry { get; set; }

    [UiFieldGroup("Alignment", 1)]
    [UiDecimal("pose-marginalization-temperature", "Pose marginalization temperature", min: 0, max: 100,
        stepSize: 0.1, isAdvanced: true, helpText: "Scales the pose posterior covariance; 0 uses only the best pose.")]
    [RelayProperty] public decimal PoseMarginalizationTemperature { get; set; } = 1;

    [UiFieldGroup("Alignment", 1)]
    [UiDecimal("local-angular-range", "Local angular range", min: 0.1, max: 60, stepSize: 0.1, unit: "°",
        ConditionalOnField = nameof(UsesLocalSearch), ConditionalOnValue = true)]
    [RelayProperty] public decimal LocalAngularRange { get; set; } = 5;

    [UiDecimal("translation-range", "Translation range", min: 0.1, max: 1000, unit: "Å",
        ConditionalOnField = nameof(UsesGlobalSearch), ConditionalOnValue = true)]
    [RelayProperty] public decimal TranslationRange { get; set; } = 40;

    [UiDecimal("translation-range", "Local translation range", min: 0.1, max: 1000, unit: "Å",
        ConditionalOnField = nameof(UsesLocalSearch), ConditionalOnValue = true)]
    [RelayProperty] public decimal LocalTranslationRange { get; set; } = 10;

    [UiBool("recenter-local-search", "Recenter local searches", isAdvanced: true,
        helpText: "Center each new search on the preceding iteration's best pose.",
        ConditionalOnField = nameof(UsesLocalSearch), ConditionalOnValue = true)]
    [RelayProperty] public bool RecenterLocalSearch { get; set; }

    [UiBool("local-gaussian-priors", "Gaussian local priors", isAdvanced: true,
        ConditionalOnField = nameof(UsesLocalSearch), ConditionalOnValue = true)]
    [RelayProperty] public bool LocalGaussianPriors { get; set; }

    [UiInt("beams", "Alignment candidates", min: 1, max: 64, isAdvanced: true,
        ConditionalOnField = nameof(SupportsAlignment), ConditionalOnValue = true)]
    [RelayProperty] public int Beams { get; set; } = 20;

    [UiIntNullable("max-tilts", "Maximum tilts", min: 1, max: 1000,
        helpText: "Use at most this many visible tilts per particle, selected by lowest accumulated dose.",
        ConditionalOnField = nameof(IsTiltSeries), ConditionalOnValue = true)]
    [RelayProperty] public int? MaxTilts { get; set; }

    public bool IsTiltSeries => ParticleInputs.Sources(PortsIn["Particles"])
        .Any(s => s.Resource?.DataDimensionality == ParticleType.Tiltseries);

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
        helpText: "Additional Beam command options. Values may be quoted. Input/output paths, device, workers and resume are managed by Relay.")]
    [RelayProperty] public string AdditionalArguments { get; set; } = "";

    [RelayProperty, Clearable] public int LastCommittedIteration { get; set; } = -1;
    [RelayProperty, Clearable] public bool HasRunToCompletion { get; set; }

    protected int OutputIteration(int iteration) => iteration < 0 ? Math.Max(LastCommittedIteration, VisAvailableIteration) : iteration;
    public virtual string IterationMetadataDirectory(int iteration) => Path.Combine(BeamDirectory, $"it{OutputIteration(iteration):D3}", "warp_metadata");
    public string TiltProcessedItems(int iteration) => Path.Combine(RelayResultsDirectoryPath, $"tilt_series_it{OutputIteration(iteration):D4}.json");
    public string TiltFailedItems => Path.Combine(RelayResultsDirectoryPath, "tilt_series_failed.json");

    public string BeamDirectory => Path.Combine(DirectoryPath, "beam");
    public string IterationFile(int iteration, string suffix) => Path.Combine(BeamDirectory,
        $"it{iteration:D3}", $"run_it{iteration:D3}_{suffix}");
    protected BeamParticleJob()
    {
        PortsIn = new(new Dictionary<string, PortIn>
        {
            ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", 1, int.MaxValue),
            ["Reference"] = new(this, typeof(MapList), "Reference", "Reference", 1, 1),
            ["Mask"] = new(this, typeof(Mask), "Mask", "Mask", 0, 1),
            ["TiltSeries"] = new(this, typeof(TiltSeriesSet), "TiltSeries", "Tilt-series metadata", 0, 1)
        });
        PortsOut = new(new Dictionary<string, PortOut>
        {
            ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", GetParticles),
            ["Reference"] = new(this, typeof(MapList), "Reference", "Refined map", GetMaps),
            ["TiltSeries"] = new(this, typeof(TiltSeriesSet), "TiltSeries", "Refined tilt-series metadata", GetTiltSeries)
        });
    }

    protected virtual ParticleSet GetParticles(int iteration)
    {
        var result = ParticleInputs.Describe(PortsIn["Particles"])?.Copy();
        if (result == null) return null;
        result.ParticlesSingleStarPath = IterationFile(OutputIteration(iteration), "data.star");
        result.ParticlesMultiStarDirectory = "";
        result.ToMultiStarPath = null;
        result.TomogramsStarPath = result.OptimisationSetStarPath = null;
        result.ItemCount = null;
        result.HasAngles = result.HasShifts = result.HasScale = true;
        if (IsTiltSeries) result.HasCtf = true;
        return result;
    }

    protected abstract MapList GetMaps(int iteration);
    protected abstract void AddReferenceArguments(Dictionary<string, string> args);
    protected abstract void AddRunArguments(Dictionary<string, string> args);
    protected abstract IEnumerable<string> RequiredIterationFiles(int iteration);

    protected virtual TiltSeriesSet GetTiltSeries(int iteration)
    {
        if (!IsTiltSeries) return null;
        var result = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>()?.Copy();
        if (result == null) return null;
        result.LatestMetadataDirectory = IterationMetadataDirectory(iteration);
        result.HasMetadata = true;
        result.HasReferenceBasedRefinements = true;
        result.ProcessedItemsJson = TiltProcessedItems(iteration);
        result.FailedItemsJson = TiltFailedItems;
        result.ItemCount = null;
        return result;
    }

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        foreach (var source in ParticleInputs.Sources(PortsIn["Particles"]))
            if (source.Resource is { } particles)
            {
                if (!particles.HasData || !particles.HasSingleStar || particles.DataDimensionality is not (ParticleType.Image or ParticleType.Tiltseries))
                    AddPortValidationError(errors, "Particles", "Beam requires extracted single-tilt images or particle tilt stacks in a single STAR file.");
                if (UsesLocalSearch && (!particles.HasAngles || !particles.HasShifts))
                    AddPortValidationError(errors, "Particles", "Local alignment requires existing orientations and shifts.");
            }
        var tiltSeries = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>();
        if (IsTiltSeries && (tiltSeries == null || !tiltSeries.HasMetadata || string.IsNullOrEmpty(tiltSeries.LatestMetadataDirectory)))
            AddPortValidationError(errors, "TiltSeries", "Particle tilt stacks require tilt-series XML metadata.");
        if (IsTiltSeries && tiltSeries is { HasCtf: false })
            AddPortValidationError(errors, "TiltSeries", "Tilt-series metadata must contain CTF estimates.");
        if (!IsTiltSeries && PortsIn["TiltSeries"].Count > 0)
            AddPortValidationError(errors, "TiltSeries", "Connect tilt-series metadata only for particle tilt stacks.");
        if (UsesLocalSearch && PortsIn["Mask"].Count == 0)
            AddPortValidationError(errors, "Mask", "Local alignment requires a mask.");
        return errors;
    }

    private static readonly HashSet<string> ManagedArguments = new(StringComparer.Ordinal)
    { "i", "input", "o", "output", "project-root", "resume", "device", "j", "workers", "reference", "reference-half-1", "reference-half-2", "mask", "metadata-dir", "show-progress", "dont-show-progress" };

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
        if (MaxTilts is < 1) errors[nameof(MaxTilts)] = "Maximum tilts must be positive or empty.";
        if (Workers < 1 || Memory < 1 || BatchSize < 1)
            errors[nameof(Workers)] = "Workers, batch size and host memory must be positive.";
        return errors;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var errors = ValidateInputs();
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values));
        var args = base.ComposeCommandArguments();
        args["device"] = Device == BeamDevice.GPU ? "cuda:0" : "cpu";
        if (IsTiltSeries)
            args["metadata-dir"] = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>()?.LatestMetadataDirectory
                ?? throw new InvalidOperationException("Connect tilt-series metadata.");
        var particles = ParticleInputs.Describe(PortsIn["Particles"])
            ?? throw new InvalidOperationException("Connect input particles.");
        args["input"] = particles.ParticlesSingleStarPath;
        AddReferenceArguments(args);
        AddRunArguments(args);
        args["output"] = BeamDirectory;
        args["project-root"] = Space.RootDirectory;
        if (PortsIn.TryGetValue("Mask", out var maskPort) && maskPort.Count > 0)
        {
            args["mask"] = PortsIn["Mask"].GetSingleResource<Mask>().MaskVolumePath;
        }
        foreach (var (key, value) in BeamWrapper.ParseArguments(AdditionalArguments)) args[key] = value;
        args["show-progress"] = "";
        return args;
    }

    public override void Stage()
    {
        base.Stage();
        if (!IsTiltSeries) return;
        string metadata = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>().LatestMetadataDirectory;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in ParticleInputs.Sources(PortsIn["Particles"]))
        {
            var star = new StreamingStar(source.Resource.ParticlesSingleStarPath, CancellationToken.None);
            var optics = star.FindBlock("optics") ?? throw new InvalidDataException("Tilt particles require optics metadata.");
            int premultiplied = optics.Columns.IndexOf("rlnCtfDataAreCtfPremultiplied");
            if (premultiplied < 0) throw new InvalidDataException("Tilt stacks must declare whether their CTF is premultiplied.");
            star.ReadRows(optics, row =>
            {
                if (StreamingStar.Value(row[premultiplied]) != "0")
                    throw new InvalidDataException("Re-extract tilt stacks using the Beam export mode: raw, unpremultiplied images.");
            }, CancellationToken.None);
            var particles = star.FindBlock("particles") ?? star.FindBlock("")
                ?? throw new InvalidDataException("Particle STAR has no particle table.");
            int column = particles.Columns.IndexOf("rlnTomoName");
            if (column < 0) throw new InvalidDataException("Particle tilt stacks require rlnTomoName.");
            star.ReadRows(particles, row =>
            {
                string name = StreamingStar.Value(row[column]);
                string basename = TiltSeriesBasename(name);
                if (names.TryGetValue(basename, out string previous) && previous != name)
                    throw new InvalidDataException($"Tilt-series names '{previous}' and '{name}' resolve to the same XML file.");
                names[basename] = name;
            }, CancellationToken.None);
            star.EnsureUnchanged();
        }
        foreach (string name in names.Keys)
            if (!File.Exists(Path.Combine(metadata, name + ".xml")))
                throw new FileNotFoundException($"Missing tilt-series metadata for {name}.", Path.Combine(metadata, name + ".xml"));
    }

    private static string TiltSeriesBasename(string path) => Path.GetFileNameWithoutExtension(path?.Replace('\\', '/'));

    protected void CollectTiltSeriesItems(int iteration)
    {
        string metadata = IterationMetadataDirectory(iteration);
        var names = Directory.EnumerateFiles(metadata, "*.xml").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var input = PortsIn["TiltSeries"].GetSingleResource<TiltSeriesSet>();
        JsonArray items;
        if (!string.IsNullOrEmpty(input.ProcessedItemsJson) && File.Exists(input.ProcessedItemsJson))
        {
            var source = JsonNode.Parse(File.ReadAllText(input.ProcessedItemsJson)).AsArray();
            items = new JsonArray(source.Where(item => names.Contains(TiltSeriesBasename(item?["Path"]?.GetValue<string>())))
                .Select(item => item.DeepClone()).ToArray());
            if (items.Count != names.Count)
                throw new InvalidDataException("Published tilt-series metadata does not match the connected processed-series list.");
        }
        else
            items = new JsonArray(names.Order().Select(name => (JsonNode)new JsonObject
                { ["Path"] = Path.Combine(input.DataSet?.DataDirectory ?? "", name + ".tomostar") }).ToArray());
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        File.WriteAllText(TiltProcessedItems(iteration), items.ToJsonString());
        File.WriteAllText(TiltFailedItems, "[]");
    }

    internal virtual int[] CommittedIterations() => !Directory.Exists(BeamDirectory) ? [] :
        Directory.EnumerateDirectories(BeamDirectory).Select(Path.GetFileName)
            .Where(name => Regex.IsMatch(name!, @"^it\d{3,}$"))
            .Select(name => int.Parse(name!.AsSpan(2), CultureInfo.InvariantCulture))
            .Where(i => RequiredIterationFiles(i).All(File.Exists) &&
                (!IsTiltSeries || Directory.Exists(IterationMetadataDirectory(i))))
            .Order().ToArray();

    protected virtual int? LogIteration(string line)
    {
        var match = Regex.Match(line, @"^=== Iteration (\d+) ===$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    protected virtual bool HasPublishedTiltMetadata(int iteration) => Directory.Exists(IterationMetadataDirectory(iteration));

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
            int? iteration = LogIteration(lines[index].Trim());
            if (iteration.HasValue) starts[iteration.Value] = index;
        }
        var sections = starts.ToArray();
        for (int section = 0; section < sections.Length; section++)
        {
            int end = section + 1 < sections.Length ? sections[section + 1].Value : lines.Length;
            JobTools.WriteLogFile(string.Join('\n', lines[sections[section].Value..end]), LogFilePath(sections[section].Key));
        }
        int latest = starts.Keys.Max();
        int committed = CommittedIterations().DefaultIfEmpty(-1).Max();
        if (IsTiltSeries)
            foreach (int i in CommittedIterations())
                if (HasPublishedTiltMetadata(i) && !File.Exists(TiltProcessedItems(i))) CollectTiltSeriesItems(i);
        return () => { stderrUpdate?.Invoke(); LogsAvailableIteration = latest; LastCommittedIteration = committed; };
    }

    public override void FinalizeRun(Action<Job, Action<Job>> updateCallback)
    {
        if (CommittedIterations().Length == 0)
            throw new InvalidOperationException("Beam has no complete iteration to finalize.");
        var logs = TrackProgressLogs();
        if (logs != null) updateCallback(this, _ => logs());
        // Manual finalization accepts the last complete iteration of an interrupted run.
        File.WriteAllText(PathSuccess, "");
        var results = TrackProgressResults();
        if (results != null) updateCallback(this, _ => results());
    }
}
