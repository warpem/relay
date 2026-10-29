using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Tools;

namespace Refund.Jobs.Common.Import.ImportParticles;

public enum ParticleMetadataFormat { Relion, CryoSparc }

/// <summary>Convert particle metadata with Beam on a CPU queue and collect Relay previews.</summary>
[GenerateReadOnly]
public class ImportParticles : Job, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new(3, 1);
    public override string TypeGuid => "847fe9cf-65bc-4618-8a8e-46ec2e74f38d";
    public override string TypeCategory => "Common.Import.Particles";
    public override string TypeName => "Import particles";
    public override string TypeNameShort => "Import particles";
    public override string TypeDescription => "Imports RELION or CryoSPARC particles using Beam";
    public override JobQueueType QueueType => JobQueueType.CPU;
    public override string[] SupportedModules => ["beam", "cpu"];
    public override string[] RequiredModules => ["beam", "cpu"];
    public override int GpuCount => 0;
    public override int CoreCount => 1;
    public override int ProcessCount => 1;
    public override int MemoryGb => Memory;
    public override bool IsIterative => false;
    public override Type ExpandedViewType => null;
    public override string CommandName => "beam import";
    public override string CommandPrefix => "OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 ";

    // The command dictionary represents one value per option. Keep the variable-length
    // --input list here so each path is passed as a separate, literal shell argument.
    public override string CommandSuffix => " --input " + string.Join(" ", InputMetadataPaths().Select(QuotePath)) +
        " && touch " + QuotePath(PathSuccess);

    [RelayProperty] public bool HasShifts { get; set; }
    [RelayProperty] public bool HasPositions { get; set; }
    [RelayProperty] public bool HasAngles { get; set; }
    [RelayProperty] public bool HasCtf { get; set; }
    [RelayProperty] public bool HasClasses { get; set; }
    [RelayProperty] public bool HasScale { get; set; }

    [UiFieldGroup("Parameters", 0)]
    [UiEnum("", "Metadata format", typeof(ParticleMetadataFormat))]
    [RelayProperty]
    public ParticleMetadataFormat MetadataFormat { get; set; } = ParticleMetadataFormat.Relion;

    // Retain the existing RELION property name for saved import jobs.
    [UiPath("", "RELION metadata file", SelectionMode.SingleFile, ["*.star"],
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.Relion)]
    [RelayProperty]
    public string FilePath { get; set; } = "";

    [UiPath("", "RELION project directory", SelectionMode.SingleFolder,
        helpText: "Directory containing the relative stack paths used in the STAR file. Leave empty to use the STAR file's directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.Relion)]
    [RelayProperty]
    public string RelionProjectDirectory { get; set; } = "";

    [UiPath("", "CryoSPARC metadata file", SelectionMode.SingleFile, ["*.cs"],
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string CryoSparcFilePath { get; set; } = "";

    [UiPath("", "CryoSPARC project directory", SelectionMode.SingleFolder,
        helpText: "Project directory containing the particle stacks referenced by the .cs metadata.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string CryoSparcProjectDirectory { get; set; } = "";

    [UiPath("", "Additional metadata 1", SelectionMode.SingleFile, ["*.cs"],
        helpText: "Optional CryoSPARC passthrough file. Relative paths are resolved from the CryoSPARC project directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string AdditionalMetadataFile1 { get; set; } = "";

    [UiPath("", "Additional metadata 2", SelectionMode.SingleFile, ["*.cs"],
        helpText: "Optional CryoSPARC passthrough file. Relative paths are resolved from the CryoSPARC project directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string AdditionalMetadataFile2 { get; set; } = "";

    [UiPath("", "Additional metadata 3", SelectionMode.SingleFile, ["*.cs"],
        helpText: "Optional CryoSPARC passthrough file. Relative paths are resolved from the CryoSPARC project directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string AdditionalMetadataFile3 { get; set; } = "";

    [UiPath("", "Additional metadata 4", SelectionMode.SingleFile, ["*.cs"],
        helpText: "Optional CryoSPARC passthrough file. Relative paths are resolved from the CryoSPARC project directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string AdditionalMetadataFile4 { get; set; } = "";

    [UiPath("", "Additional metadata 5", SelectionMode.SingleFile, ["*.cs"],
        helpText: "Optional CryoSPARC passthrough file. Relative paths are resolved from the CryoSPARC project directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string AdditionalMetadataFile5 { get; set; } = "";

    [UiFieldGroup("Compute", 1)]
    [UiInt("", "Host memory", min: 1, max: 4096, unit: "GB")]
    [RelayProperty]
    public int Memory { get; set; } = 8;

    private (string Name, string Path)[] AdditionalMetadataFiles =>
    [
        (nameof(AdditionalMetadataFile1), AdditionalMetadataFile1),
        (nameof(AdditionalMetadataFile2), AdditionalMetadataFile2),
        (nameof(AdditionalMetadataFile3), AdditionalMetadataFile3),
        (nameof(AdditionalMetadataFile4), AdditionalMetadataFile4),
        (nameof(AdditionalMetadataFile5), AdditionalMetadataFile5)
    ];

    public string[] CryoSparcMetadataPaths => new[] { CryoSparcFilePath }.Concat(
        AdditionalMetadataFiles.Where(file => !string.IsNullOrWhiteSpace(file.Path)).Select(file => file.Path)).ToArray();

    public string ImportedParticlesPath => Path.Combine(DirectoryPath, "particles.star");
    public string VisParticlesPath => Path.Combine(RelayResultsDirectoryPath, "visualization.png");

    public ImportParticles()
    {
        PortsIn = new(new Dictionary<string, PortIn>());
        PortsOut = new(new Dictionary<string, PortOut>
        {
            ["Particles"] = new(this, typeof(ParticleSet), "Particles", "Particles", GetParticles)
        });
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = new Dictionary<string, string>();
        if (MetadataFormat == ParticleMetadataFormat.Relion)
        {
            if (string.IsNullOrWhiteSpace(FilePath) || !FilePath.EndsWith(".star", StringComparison.OrdinalIgnoreCase))
                errors[nameof(FilePath)] = "Select a RELION .star particle metadata file.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(CryoSparcFilePath) || !CryoSparcFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                errors[nameof(CryoSparcFilePath)] = "Select a CryoSPARC .cs particle metadata file.";
            if (string.IsNullOrWhiteSpace(CryoSparcProjectDirectory))
                errors[nameof(CryoSparcProjectDirectory)] = "Specify the CryoSPARC project directory.";
            foreach (var file in AdditionalMetadataFiles)
                if (!string.IsNullOrWhiteSpace(file.Path) && !file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    errors[file.Name] = "Select a .cs metadata file or leave this field empty.";
        }
        if (Memory < 1) errors[nameof(Memory)] = "Host memory must be positive.";
        return errors;
    }

    private string[] InputMetadataPaths() => MetadataFormat == ParticleMetadataFormat.CryoSparc
        ? CryoSparcMetadataPaths : [Path.GetFullPath(FilePath, Space.RootDirectory)];

    private static string QuotePath(string path) => "'" + path.Replace("'", "'\"'\"'") + "'";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var errors = ValidateInputs();
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values));
        string root = MetadataFormat == ParticleMetadataFormat.CryoSparc ? CryoSparcProjectDirectory :
            !string.IsNullOrWhiteSpace(RelionProjectDirectory) ? RelionProjectDirectory :
            Path.GetDirectoryName(Path.GetFullPath(FilePath, Space.RootDirectory));
        return new()
        {
            ["project-root"] = Path.GetFullPath(root, Space.RootDirectory),
            ["output"] = ImportedParticlesPath,
            ["absolute-paths"] = "",
            ["check-stacks"] = ""
        };
    }

    internal void DetectMetadata(IEnumerable<string> columns)
    {
        var names = columns.ToHashSet();
        HasAngles = names.Contains("rlnAnglePsi");
        HasCtf = names.Contains("rlnDefocusU");
        HasClasses = names.Contains("rlnClassNumber");
        HasShifts = names.Contains("rlnOriginX") || names.Contains("rlnOriginXAngst");
        HasPositions = names.Contains("rlnCoordinateX") || names.Contains("rlnCoordinateXAngst");
        HasScale = names.Contains("rlnScale") || names.Contains("rlnNormCorrection");
    }

    private ParticleSet GetParticles(int iteration) => new()
    {
        ParticlesSingleStarPath = ImportedParticlesPath,
        HasData = true,
        HasShifts = HasShifts,
        HasPositions = HasPositions,
        HasAngles = HasAngles,
        HasCtf = HasCtf,
        HasClasses = HasClasses,
        HasScale = HasScale
    };

    private long _stdoutLength = -1;

    public override Action TrackProgressLogs()
    {
        var stderrUpdate = base.TrackProgressLogs();
        if (!File.Exists(PathStdOut)) return stderrUpdate;
        if (JobTools.HasLogFileChanged(PathStdOut, ref _stdoutLength) || !File.Exists(LogFilePath(0)))
        {
            JobTools.WriteLogFile(File.ReadAllText(PathStdOut), LogFilePath(0));
            return () => { stderrUpdate?.Invoke(); LogsAvailableIteration = 0; };
        }
        return stderrUpdate;
    }

    public override Action TrackProgressResults()
    {
        if (VisAvailableIteration >= 0 || !File.Exists(PathSuccess)) return null;
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        var imported = new Refund.Utils.Star.StreamingStar(ImportedParticlesPath, CancellationToken.None);
        var particles = imported.FindBlock("particles")
            ?? throw new InvalidDataException("Beam did not write a particle table.");
        if (particles.RowCount == 0 || !particles.Columns.Contains("rlnImageName"))
            throw new InvalidDataException("Beam did not write a non-empty particle image table.");
        string[] columns = particles.Columns.ToArray();
        if (!File.Exists(VisParticlesPath))
            BakeryWrapper.ParticleImageAtlas(Space.RootDirectory, [ImportedParticlesPath], 20, VisParticlesPath);
        if (!File.Exists(VisCard(0)))
            BakeryWrapper.ImportParticlesJobCard([ImportedParticlesPath], VisCard(0), Space.RootDirectory);
        return () => { DetectMetadata(columns); VisAvailableIteration = 0; };
    }
}
