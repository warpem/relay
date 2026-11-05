using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Classes2D.BeamClass2D;

[GenerateReadOnly]
public class BeamClass2D : Job, IClusterJob
{
    public override string TypeGuid => "7bf23d0e-aeca-4cd5-86ad-ed6a11810d87";
    public override string TypeCategory => "💥 Beam.💥 2D classification";
    public override string TypeName => "💥 2D classification";
    public override string TypeNameShort => "💥 2D classification";
    public override string TypeDescription => "Classify particle images with likelihood alignment and PCA discovery.";
    public override Type ExpandedViewType => typeof(Class2D.Class2DExpandedView);
    public override int2 CardSquareCount { get; set; } = new(2, 1);
    public override bool IsIterative => true;
    public override bool CanBeFinalized => true;
    public override string CommandName => "beam class2d";
    public override string CommandSuffix => $" && touch {JobTools.QuoteArgumentValue(PathSuccess)}";
    public override JobQueueType QueueType => Device == BeamDevice.GPU ? JobQueueType.GPU : JobQueueType.CPU;
    public override string[] SupportedModules => ["beam", "gpu", "cpu"];
    public override string[] RequiredModules => ["beam", Device == BeamDevice.GPU ? "gpu" : "cpu"];
    public override int GpuCount => Device == BeamDevice.GPU ? 1 : 0;
    public override int ProcessCount => 1;
    public override int CoreCount => Workers;
    public override int MemoryGb => Memory;

    [UiFieldGroup("Classification", 0)]
    [UiInt("final-classes", "Number of classes", min: 1, max: 10000,
        helpText: "Final total, including classes representing unusual particles. Beam grows the class bank during training.")]
    [RelayProperty] public int NClasses { get; set; } = 64;
    [UiInt("iterations", "Training updates", min: 1, max: 10000)]
    [RelayProperty] public int Iterations { get; set; } = 60;
    [UiInt("particles-per-class", "Training particles per class", min: 1, max: 1000000,
        helpText: "Training particles per current class per update. Higher values give steadier updates.")]
    [RelayProperty] public int ParticlesPerClass { get; set; } = 100;
    [UiDecimal("learning-rate", "Learning rate", min: 0.001, max: 1, stepSize: 0.05,
        helpText: "Class reconstruction update rate in (0, 1].")]
    [RelayProperty] public decimal LearningRate { get; set; } = 0.2m;
    [UiBool("subclassify", "Subclassify final classes",
        helpText: "After final assignment, split each regular class into four fixed-pose subclasses. Completed subclasses appear grouped by parent, with separate subclass particle and template outputs.")]
    [RelayProperty] public bool Subclassify { get; set; }
    [UiInt("particle-diameter", "Particle diameter", min: 1, max: 10000, unit: "Å",
        helpText: "Enclose the entire particle. Class and PCA masks taper outside this diameter.")]
    [RelayProperty] public int ParticleDiameter { get; set; } = 200;
    [UiInt("working-box", "Working box size", min: 4, max: 4096, stepSize: 2, unit: "px",
        helpText: "Even Fourier-cropped box, no larger than the input box. Display reconstructions retain the original sampling.")]
    [RelayProperty] public int WorkingBox { get; set; } = 64;
    [UiEnum("", "Regularization", typeof(Beam2DRegularization))]
    [RelayProperty] public Beam2DRegularization Regularization { get; set; } = Beam2DRegularization.FRC;
    [UiInt("denoise-initial-steps", "Initial denoising steps", min: 1, max: 100000, isAdvanced: true,
        ConditionalOnField = nameof(Regularization), ConditionalOnValue = Beam2DRegularization.Denoise)]
    [RelayProperty] public int DenoiseInitialSteps { get; set; } = 10000;
    [UiInt("denoise-steps", "Denoising steps per update", min: 1, max: 100000, isAdvanced: true,
        ConditionalOnField = nameof(Regularization), ConditionalOnValue = Beam2DRegularization.Denoise)]
    [RelayProperty] public int DenoiseSteps { get; set; } = 1000;
    [UiInt("initial-classes", "Initial classes", min: 1, max: 10000, isAdvanced: true)]
    [RelayProperty] public int InitialClasses { get; set; } = 4;
    [UiInt("final-iterations", "Final full-data passes", min: 1, max: 100)]
    [RelayProperty] public int FinalIterations { get; set; } = 2;
    [UiDecimal("peripheral-class-ratio", "Peripheral classes per regular class", min: 0, max: 2, stepSize: 0.1, isAdvanced: true,
        helpText: "Reserve part of the class total for unusual particles. Zero disables peripheral classes.")]
    [RelayProperty] public decimal PeripheralClassRatio { get; set; } = 1;

    [UiFieldGroup("Alignment", 1)]
    [UiDecimal("translation-range", "Translation range", min: 0, max: 1000, unit: "Å")]
    [RelayProperty] public decimal TranslationRange { get; set; } = 100;
    [UiDecimal("initial-angular-sampling", "Initial angular sampling", min: 0.1, max: 180, unit: "°", isAdvanced: true)]
    [RelayProperty] public decimal AngularSampling { get; set; } = 20;
    [UiBool("marginalize-poses", "Marginalize poses", isAdvanced: true)]
    [RelayProperty] public bool MarginalizePoses { get; set; }
    [UiBool("ctf-intact-first-peak", "CTF intact before first peak", isAdvanced: true)]
    [RelayProperty] public bool CtfIntactFirstPeak { get; set; }
    [UiEnum("", "Reconstruction assignments", typeof(Beam2DAssignments), isAdvanced: true)]
    [RelayProperty] public Beam2DAssignments ReconstructionAssignments { get; set; } = Beam2DAssignments.Soft;

    [UiFieldGroup("PCA discovery", 2)]
    [UiInt("pca-rank", "Principal components", min: 1, max: 256, isAdvanced: true)]
    [RelayProperty] public int PcaRank { get; set; } = 16;
    [UiInt("maximum-pca-particles", "Maximum PCA particles", min: 2, max: 10000000, isAdvanced: true)]
    [RelayProperty] public int MaximumPcaParticles { get; set; } = 50000;
    [UiInt("discovery-interval", "Updates between PCA rebuilds", min: 1, max: 10000, isAdvanced: true)]
    [RelayProperty] public int DiscoveryInterval { get; set; } = 10;

    [UiFieldGroup("Compute", 3)]
    [UiEnum("", "Compute device", typeof(BeamDevice))]
    [RelayProperty] public BeamDevice Device { get; set; } = BeamDevice.GPU;
    [UiInt("workers", "CPU workers", min: 1, max: 256)]
    [RelayProperty] public int Workers { get; set; } = 8;
    [UiInt("batch-size", "Particles per compute batch", min: 1, max: 4096, isAdvanced: true)]
    [RelayProperty] public int BatchSize { get; set; } = 128;
    [UiInt("", "Host memory", min: 1, max: 4096, unit: "GB")]
    [RelayProperty] public int Memory { get; set; } = 32;
    [UiPath("cache-dir", "Particle scratch directory", SelectionMode.SingleFolder,
        helpText: "Optional node-local particle cache. Queue variables such as $TMPDIR are supported.")]
    [RelayProperty] public string CacheDirectory { get; set; } = "";
    [UiString("", "Additional arguments", isAdvanced: true,
        helpText: "Additional Beam options. Input/output paths, device, workers, dry-run and quiet are managed by Relay.")]
    [RelayProperty] public string AdditionalArguments { get; set; } = "";

    [RelayProperty, Clearable] public int FinalAssignmentIteration { get; set; } = -1;
    public string BeamDirectory => Path.Combine(DirectoryPath, "beam");
    public string SnapshotDirectory(int i) => Path.Combine(BeamDirectory, "snapshots", $"it{i:D3}");
    public string ClassStack(int i) => Path.Combine(SnapshotDirectory(i), "classes.mrcs");
    public string PrincipalComponentsStack(int i) => Path.Combine(SnapshotDirectory(i), "principal_components.mrcs");
    public string SubclassStack(int parent) => Path.Combine(BeamDirectory, "subclasses", $"parent-{parent:D3}", "classes_display.mrcs");
    public string VisClassAtlas(int i) => Path.Combine(RelayResultsDirectoryPath, $"classes_it{i:D4}.png");
    public string VisClassStats(int i) => Path.Combine(RelayResultsDirectoryPath, $"stats_it{i:D4}.json");
    public string VisPcaAtlas(int i) => Path.Combine(RelayResultsDirectoryPath, $"pca_it{i:D4}.png");
    public string VisPcaInfo(int i) => Path.Combine(RelayResultsDirectoryPath, $"pca_info_it{i:D4}.json");
    public string VisEmbeddings(int i) => Path.Combine(RelayResultsDirectoryPath, $"embedding_it{i:D4}.svg");
    [RelayProperty, Clearable] public int SubclassGroupsAvailableCount { get; set; }
    [RelayProperty, Clearable] public bool SubclassesCollectedToCompletion { get; set; }
    [RelayProperty, Clearable] public bool SubclassSelectionAvailable { get; set; }
    public string SubclassResultsDirectory => Path.Combine(RelayResultsDirectoryPath, "subclasses");
    public string VisSubclassGroups => Path.Combine(SubclassResultsDirectory, "groups.json");
    public string VisSubclassParentAtlas(int parent) => Path.Combine(SubclassResultsDirectory, $"parent-{parent:D3}", "parent.png");
    public string VisSubclassAtlas(int parent) => Path.Combine(SubclassResultsDirectory, $"parent-{parent:D3}", "classes.png");

    public bool CanExport(int i) => FinalAssignmentIteration > 0 && i == FinalAssignmentIteration;

    public BeamClass2D()
    {
        PortsIn = new(new Dictionary<string, PortIn> { ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", 1, int.MaxValue) });
        PortsOut = new(new Dictionary<string, PortOut>
        {
            ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", GetParticles),
            ["Templates"] = new(this, typeof(TemplateSet), "Templates", "2D Classes", GetTemplates),
            ["SubclassParticles"] = new(this, typeof(ParticleSet), "SubclassParticles", "Subclass particles", GetSubclassParticles),
            ["SubclassTemplates"] = new(this, typeof(TemplateSet), "SubclassTemplates", "Subclasses", GetSubclassTemplates)
        });
    }

    private Resource GetParticles(int i)
    {
        if (i < 0) i = FinalAssignmentIteration;
        if (!CanExport(i)) return null;
        var result = ParticleInputs.Describe(PortsIn["Particles"])?.Copy();
        if (result == null) return null;
        result.ParticlesSingleStarPath = Path.Combine(BeamDirectory, "particles.star");
        result.ParticlesMultiStarDirectory = ""; result.ToMultiStarPath = null;
        result.ItemCount = null;
        result.HasClasses = result.HasShifts = result.HasScale = true;
        // Psi-only 2D alignment is not an estimated 3D orientation.
        result.HasAngles = false;
        return result;
    }

    private Resource GetTemplates(int i)
    {
        if (i < 0) i = VisAvailableIteration;
        if (!File.Exists(VisClassStats(i))) return null;
        using var stats = JsonDocument.Parse(File.ReadAllText(VisClassStats(i)));
        return new TemplateSet(Path.Combine(SnapshotDirectory(i), "model.star"),
            Path.Combine(SnapshotDirectory(i), "classes.mrcs"), VisClassStats(i), stats.RootElement.GetArrayLength());
    }

    private Resource GetSubclassParticles(int i)
    {
        if (!SubclassSelectionAvailable || (i >= 0 && !CanExport(i))) return null;
        var result = GetParticles(-1) as ParticleSet;
        if (result == null) return null;
        result.ParticlesSingleStarPath = Path.Combine(SubclassResultsDirectory, "particles.star");
        result.ItemCount = null;
        return result;
    }

    private Resource GetSubclassTemplates(int i)
    {
        if (!SubclassSelectionAvailable || (i >= 0 && !CanExport(i))) return null;
        string statsPath = Path.Combine(SubclassResultsDirectory, "stats.json");
        using var stats = JsonDocument.Parse(File.ReadAllText(statsPath));
        return new TemplateSet(Path.Combine(SubclassResultsDirectory, "model.star"),
            Path.Combine(SubclassResultsDirectory, "classes.mrcs"), statsPath, stats.RootElement.GetArrayLength());
    }

    internal Action CollectSubclassResults(bool finalize = false, Action<string, string, string, bool> render = null)
    {
        string path = Path.Combine(BeamDirectory, "subclasses", "summary.json");
        if (!File.Exists(path)) return null;
        JsonDocument summary;
        try { summary = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return null; } // Beam may currently be publishing its next parent.
        using (summary)
        {
            var records = summary.RootElement.GetProperty("parents").EnumerateArray().ToArray();
            bool complete = finalize || summary.RootElement.GetProperty("status").GetString() == "complete";
            if (records.Length == SubclassGroupsAvailableCount && File.Exists(VisSubclassGroups) &&
                (!complete || SubclassesCollectedToCompletion)) return null;
            int completedParents = 0;
            foreach (var record in records.Where(r => r.GetProperty("status").GetString() == "complete"))
            {
                int parent = record.GetProperty("parent_class").GetInt32();
                string directory = Path.Combine(BeamDirectory, "subclasses", $"parent-{parent:D3}");
                if (!new[] { "classes_display.mrcs", "particles.star", "summary.json" }.All(f => File.Exists(Path.Combine(directory, f))))
                    return null;
                completedParents++;
            }
            Directory.CreateDirectory(SubclassResultsDirectory);
            string source = Path.Combine(SubclassResultsDirectory, "source.json");
            File.WriteAllText(source, summary.RootElement.GetRawText());
            (render ?? BakeryWrapper.BeamClass2DSubclasses)(BeamDirectory, source, SubclassResultsDirectory, complete);
            bool available = complete && completedParents > 0;
            return () =>
            {
                SubclassGroupsAvailableCount = records.Length;
                SubclassesCollectedToCompletion = complete;
                SubclassSelectionAvailable = available;
            };
        }
    }

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        foreach (var source in ParticleInputs.Sources(PortsIn["Particles"]))
            if (source.Resource is { } particles && (!particles.HasData || !particles.HasSingleStar || particles.DataDimensionality != ParticleType.Image))
                AddPortValidationError(errors, "Particles", "Beam class2d requires extracted single-image particles in STAR format.");
        return errors;
    }

    private static readonly HashSet<string> Managed = new(StringComparer.Ordinal)
    { "i", "input", "o", "output", "project-root", "device", "j", "workers", "resume", "dry-run", "quiet", "help", "show-progress", "dont-show-progress" };
    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        try
        {
            foreach (string name in BeamWrapper.ParseArguments(AdditionalArguments).Keys.Where(Managed.Contains))
                errors[nameof(AdditionalArguments)] = $"--{name} is managed by Relay.";
        }
        catch (ArgumentException e) { errors[nameof(AdditionalArguments)] = e.Message; }
        foreach (var (name, value) in new Dictionary<string, int>
        {
            [nameof(NClasses)] = NClasses, [nameof(InitialClasses)] = InitialClasses,
            [nameof(Iterations)] = Iterations, [nameof(FinalIterations)] = FinalIterations,
            [nameof(ParticlesPerClass)] = ParticlesPerClass,
            [nameof(ParticleDiameter)] = ParticleDiameter, [nameof(Workers)] = Workers,
            [nameof(BatchSize)] = BatchSize, [nameof(Memory)] = Memory,
            [nameof(PcaRank)] = PcaRank, [nameof(DiscoveryInterval)] = DiscoveryInterval
        })
            if (value < 1) errors[name] = "Must be positive.";
        if (LearningRate <= 0 || LearningRate > 1) errors[nameof(LearningRate)] = "Learning rate must be in (0, 1].";
        if (TranslationRange < 0) errors[nameof(TranslationRange)] = "Translation range must be nonnegative.";
        if (AngularSampling <= 0 || AngularSampling > 180) errors[nameof(AngularSampling)] = "Angular sampling must be in (0, 180].";
        if (PeripheralClassRatio < 0 || PeripheralClassRatio > 2) errors[nameof(PeripheralClassRatio)] = "Peripheral class ratio must be in [0, 2].";
        if (WorkingBox < 4 || WorkingBox % 2 != 0) errors[nameof(WorkingBox)] = "Working box must be even and at least four.";
        if (InitialClasses > NClasses) errors[nameof(InitialClasses)] = "Initial classes cannot exceed the final total.";
        if (MaximumPcaParticles <= PcaRank || MaximumPcaParticles < NClasses)
            errors[nameof(MaximumPcaParticles)] = "Maximum PCA particles must exceed PCA rank and cover the requested classes.";
        if (PcaRank >= 2048) errors[nameof(PcaRank)] = "PCA rank must be smaller than the initial reservoir.";
        return errors;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var errors = ValidateInputs();
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values));
        var args = base.ComposeCommandArguments();
        args["device"] = Device == BeamDevice.GPU ? "cuda:0" : "cpu";
        args["input"] = ParticleInputs.Describe(PortsIn["Particles"])?.ParticlesSingleStarPath
            ?? throw new InvalidOperationException("Connect particles.");
        args["output"] = BeamDirectory; args["project-root"] = Space.RootDirectory;
        args["show-progress"] = "";
        args["reconstruction-assignments"] = ReconstructionAssignments.ToString().ToLowerInvariant();
        if (Regularization == Beam2DRegularization.Denoise) args["denoise"] = "";
        foreach (var (name, value) in BeamWrapper.ParseArguments(AdditionalArguments)) args[name] = value;
        return args;
    }

    internal sealed record Snapshot(int ClassCount, int PrincipalComponentCount, int EmbeddingParticleCount, bool Final);
    internal Snapshot ReadSnapshot(int i)
    {
        string path = Path.Combine(SnapshotDirectory(i), "snapshot.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        return new(r.GetProperty("class_count").GetInt32(), r.GetProperty("principal_component_count").GetInt32(),
            r.GetProperty("embedding_particle_count").GetInt32(), r.GetProperty("final").GetBoolean());
    }

    internal int[] CommittedIterations() => !Directory.Exists(Path.Combine(BeamDirectory, "snapshots")) ? [] :
        Directory.EnumerateDirectories(Path.Combine(BeamDirectory, "snapshots"))
            .Select(Path.GetFileName).Where(n => Regex.IsMatch(n!, @"^it\d{3,}$"))
            .Select(n => int.Parse(n!.AsSpan(2), CultureInfo.InvariantCulture))
            .Where(i => new[] { "snapshot.json", "classes.mrcs", "model.star" }.All(f => File.Exists(Path.Combine(SnapshotDirectory(i), f))))
            .Order().ToArray();

    public override Action TrackProgressLogs()
    {
        var stderr = base.TrackProgressLogs();
        if (!File.Exists(PathStdOut)) return stderr;
        string[] lines = JobTools.CleanProgressBarLines(File.ReadAllText(PathStdOut).Replace("\r\n", "\n").Split('\n'));
        var starts = new SortedDictionary<int, int> { [0] = 0 };
        for (int j = 0; j < lines.Length; j++)
        {
            var heading = Regex.Match(lines[j], @"^=== Snapshot (\d+) ===$");
            var published = Regex.Match(lines[j], @"^(?:class2d: snapshot |Publishing snapshot )(\d+):");
            if (heading.Success)
                starts[int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture)] = j;
            else if (published.Success)
                starts.TryAdd(int.Parse(published.Groups[1].Value, CultureInfo.InvariantCulture), j);
        }
        var sections = starts.ToArray();
        for (int j = 0; j < sections.Length; j++)
            JobTools.WriteLogFile(string.Join('\n', lines[sections[j].Value..(j + 1 < sections.Length ? sections[j + 1].Value : lines.Length)]), LogFilePath(sections[j].Key));
        return () => { stderr?.Invoke(); LogsAvailableIteration = starts.Keys.Max(); };
    }

    public override Action TrackProgressResults()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        int latest = VisAvailableIteration, final = FinalAssignmentIteration;
        foreach (int i in CommittedIterations())
        {
            var info = ReadSnapshot(i);
            if (info.Final && File.Exists(Path.Combine(BeamDirectory, "particles.star"))) final = i;
            bool hasEmbedding = File.Exists(Path.Combine(SnapshotDirectory(i), "embeddings.npz"));
            if (!File.Exists(VisClassStats(i)) || !File.Exists(VisPcaInfo(i)) ||
                (hasEmbedding && !File.Exists(VisEmbeddings(i))))
                BakeryWrapper.BeamClass2DFigures(SnapshotDirectory(i), RelayResultsDirectoryPath, i);
            if (!File.Exists(VisCard(i)))
                BakeryWrapper.Class2DJobCard(Path.Combine(SnapshotDirectory(i), "classes.mrcs"),
                    Enumerable.Range(0, info.ClassCount).ToArray(), Enumerable.Range(1, info.ClassCount).Select(c => c.ToString()).ToArray(), VisCard(i));
            latest = i;
        }
        var subclasses = CollectSubclassResults();
        return latest == VisAvailableIteration && final == FinalAssignmentIteration && subclasses == null ? null :
            () => { VisAvailableIteration = latest; FinalAssignmentIteration = final; subclasses?.Invoke(); };
    }

    public override void FinalizeRun(Action<Job, Action<Job>> updateCallback)
    {
        if (!CommittedIterations().Any()) throw new InvalidOperationException("Beam has no complete snapshot to finalize.");
        if (TrackProgressLogs() is { } logs) updateCallback(this, _ => logs());
        if (TrackProgressResults() is { } figures) updateCallback(this, _ => figures());
        if (CollectSubclassResults(finalize: true) is { } subclasses) updateCallback(this, _ => subclasses());
    }
}

public enum Beam2DRegularization { FRC, Denoise }
public enum Beam2DAssignments { Soft, Hard }

public sealed class BeamSubclassGroup
{
    public int ParentId { get; set; }
    public int FirstClass { get; set; }
    public Class2DModel[] Classes { get; set; } = [];
    public string Reason { get; set; }
}
