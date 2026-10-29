using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Common.Import.ImportParticles;

public enum ParticleMetadataFormat { Relion, CryoSparc }

/// <summary>
/// Job for importing particle datasets into the system.
/// Accepts RELION STAR or CryoSPARC metadata referencing extracted particle stacks.
/// CryoSPARC conversion and stack validation are delegated to Beam.
/// </summary>
/// <remarks>
/// Particles are the individual extracted protein/complex images that are used in 3D reconstruction.
/// The job automatically detects what metadata is available (positions, angles, CTF, etc.) and
/// updates file paths to ensure the referenced image stacks can be found.
/// </remarks>
[GenerateReadOnly]
public class ImportParticles : Job, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import particles job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(3, 1);

    public override string TypeGuid => "847fe9cf-65bc-4618-8a8e-46ec2e74f38d";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "Common.Import.Particles";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Import particles";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Import particles";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Imports a set of particles";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>Runs locally on the CPU; requests no GPUs.</summary>
    public override int GpuCount => 0;

    /// <summary>
    /// Gets whether this job produces iterative results.
    /// Import jobs are non-iterative as they simply process existing files.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// Gets the type of the expanded view component for this job.
    /// Import particles jobs do not have a specialized expanded view.
    /// </summary>
    public override Type ExpandedViewType => null;
    
    /// <summary>
    /// Gets or sets whether the imported particles have shift (origin) information.
    /// This is automatically detected based on the presence of rlnOriginX/rlnOriginXAngst columns.
    /// </summary>
    [RelayProperty]
    public bool HasShifts { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have position information.
    /// This is automatically detected based on the presence of rlnCoordinateX/rlnCoordinateXAngst columns.
    /// </summary>
    [RelayProperty]
    public bool HasPositions { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have orientation angle information.
    /// This is automatically detected based on the presence of rlnAnglePsi column.
    /// </summary>
    [RelayProperty]
    public bool HasAngles { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have CTF parameter information.
    /// This is automatically detected based on the presence of rlnDefocusU column.
    /// </summary>
    [RelayProperty]
    public bool HasCtf { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have classification information.
    /// This is automatically detected based on the presence of rlnClassNumber column.
    /// </summary>
    [RelayProperty]
    public bool HasClasses { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have scale information.
    /// This is automatically detected based on the presence of rlnScale column.
    /// </summary>
    [RelayProperty]
    public bool HasScale { get; set; } = false;


    #region Parameters

    /// <summary>
    /// Gets or sets the primary particle metadata file (.star or .cs).
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiEnum("", "Metadata format", typeof(ParticleMetadataFormat))]
    [RelayProperty]
    public ParticleMetadataFormat MetadataFormat { get; set; } = ParticleMetadataFormat.Relion;

    [UiPath("", "File path",
            SelectionMode.SingleFile,
            ["*.star", "*.cs"],
            helpText: "Path to the particle metadata to be imported.")]
    [RelayProperty]
    public string FilePath { get; set; } = "";

    [UiPath("", "CryoSPARC project directory", SelectionMode.SingleFolder,
        helpText: "Project directory containing the particle stacks referenced by the .cs metadata.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string CryoSparcProjectDirectory { get; set; } = "";

    [UiText("", "Additional CryoSPARC metadata",
        helpText: "Optional passthrough .cs files, one path per line. Paths may be relative to the CryoSPARC project directory.",
        ConditionalOnField = nameof(MetadataFormat), ConditionalOnValue = ParticleMetadataFormat.CryoSparc)]
    [RelayProperty]
    public string AdditionalMetadataPaths { get; set; } = "";

    public string[] CryoSparcMetadataPaths => new[] { FilePath }.Concat(
        AdditionalMetadataPaths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
    
    
    #endregion

    /// <summary>
    /// Gets the path where the imported particle metadata will be stored within the job directory.
    /// </summary>
    public string ImportedParticlesPath => Path.Combine(DirectoryPath, "particles.star");

    /// <summary>
    /// Gets the path where the visualization of imported particles will be stored.
    /// </summary>
    public string VisParticlesPath => Path.Combine(RelayResultsDirectoryPath, "visualization.png");

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

    /// <summary>
    /// Initializes a new instance of the ImportParticles job.
    /// Configures the output port that will provide the imported particles to downstream jobs.
    /// </summary>
    public ImportParticles()
    {
        PortsIn = new(new Dictionary<string, PortIn>());

        var PortOutParticles = new PortOut(this, typeof(ParticleSet), "Particles", "Particles", GetParticles);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutParticles.Name] = PortOutParticles
        });
    }

    /// <summary>
    /// Validates the job parameters before execution.
    /// Checks if the specified star file path exists and has a valid format.
    /// </summary>
    /// <returns>A dictionary of validation errors, if any</returns>
    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = new Dictionary<string, string>();
        string extension = MetadataFormat == ParticleMetadataFormat.CryoSparc ? ".cs" : ".star";
        if (string.IsNullOrWhiteSpace(FilePath) || !FilePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            errors[nameof(FilePath)] = $"Select a {extension} particle metadata file.";
        if (MetadataFormat == ParticleMetadataFormat.CryoSparc)
        {
            if (string.IsNullOrWhiteSpace(CryoSparcProjectDirectory))
                errors[nameof(CryoSparcProjectDirectory)] = "Specify the CryoSPARC project directory.";
            if (CryoSparcMetadataPaths.Any(path => !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                errors[nameof(AdditionalMetadataPaths)] = "Each metadata path must name a .cs file.";
        }
        return errors;
    }

    /// <summary>
    /// Creates and returns a ParticleSet resource from the imported particle data.
    /// This method is called by the output port to provide data to downstream jobs.
    /// </summary>
    /// <param name="iter">The iteration number (not used as this job is non-iterative)</param>
    /// <returns>A ParticleSet resource pointing to the imported particles</returns>
    private ParticleSet GetParticles(int iter)
    {
        var result = new ParticleSet()
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

        return result;
    }

    /// <summary>
    /// Executes the particle import operation locally.
    /// Processes the STAR file, detects available metadata, resolves image stack paths,
    /// and generates visualizations of the particles.
    /// </summary>
    /// <param name="token">Cancellation token for aborting the operation</param>
    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            try
            {
                string stackForVis = null;
                
                logger.WriteLine($"Importing particles from {FilePath}");

                if (MetadataFormat == ParticleMetadataFormat.CryoSparc)
                {
                    BeamWrapper.ImportParticles(CryoSparcMetadataPaths, CryoSparcProjectDirectory,
                        ImportedParticlesPath, logger, ErrorFilePath, token);
                    // Beam already resolved and checked the stacks. Keep its complete STAR
                    // instead of rewriting it with the legacy whitespace-only Warp reader.
                    var imported = new Refund.Utils.Star.StreamingStar(ImportedParticlesPath, token);
                    var particles = imported.FindBlock("particles")
                        ?? throw new InvalidDataException("Beam did not write a particle table.");
                    if (particles.RowCount == 0)
                        throw new InvalidDataException("The particle table is empty.");
                    DetectMetadata(particles.Columns);
                }
                else
                {
                    string metadataPath = FilePath;
                    logger.Write("Figuring out image file location and saving the updated table... ");
                    {
                        Star tableIn = null;
                        Star tableOptics = null;

                        // Handle both old-style and new-style (multi-table) STAR files
                        if (Star.IsMultiTable(metadataPath))
                        {
                            tableIn = new(metadataPath, "particles");
                            tableOptics = new(metadataPath, "optics");
                        }
                        else
                            tableIn = new(metadataPath);

                        if (tableIn.RowCount == 0)
                            throw new Exception("The particle table is empty.");

                        if (!tableIn.HasColumn("rlnImageName"))
                            throw new Exception("The particle table does not contain the 'rlnImageName' column.");

                        DetectMetadata(tableIn.GetColumnNames());

                        string[] columnImageNames = tableIn.GetColumn("rlnImageName");
                        string originalFolder = Path.GetDirectoryName(metadataPath);
                        string pathCorrection = "";

                        // Try to find the image file in various locations - look up to 3 levels up
                        // from the STAR file location to find the referenced image stacks
                        {
                            string[] parts = columnImageNames[0].Split("@", StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

                            if (parts.Length != 2)
                                throw new Exception($"Expected particle addresses to be in the format 'path@filename', got {columnImageNames[0]} instead.");

                            string fileName = parts[1];

                            if (File.Exists(Path.Combine(originalFolder, fileName)))
                                pathCorrection = "";
                            else if (File.Exists(Path.Combine(originalFolder, "..", fileName)))
                                pathCorrection = "..";
                            else if (File.Exists(Path.Combine(originalFolder, "..", "..", fileName)))
                                pathCorrection = Path.Combine("..", "..");
                            else if (File.Exists(Path.Combine(originalFolder, "..", "..", "..", fileName)))
                                pathCorrection = Path.Combine("..", "..", "..");
                            else
                                throw new Exception($"Could not find the particle file {fileName} up to 3 directory levels above the table location.");
                        
                            originalFolder = Path.GetFullPath(Path.Combine(originalFolder, pathCorrection));
                        }
                    
                        // Update all image paths to use absolute paths
                        Dictionary<string, int> allStackPaths = new();
                    
                        for (int i = 0; i < columnImageNames.Length; i++)
                        {
                            string[] parts = columnImageNames[i].Split("@", StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

                            if (parts.Length != 2)
                                throw new Exception($"Expected particle addresses to be in the format 'id@filename', got {columnImageNames[i]} instead.");

                            string fileName = parts[1];
                            string newFileName = Path.GetFullPath(Path.Combine(originalFolder, fileName));

                            columnImageNames[i] = $"{parts[0]}@{newFileName}";

                            if (!allStackPaths.ContainsKey(newFileName))
                                allStackPaths.Add(newFileName, 0);

                            allStackPaths[newFileName]++;
                        }
                        tableIn.SetColumn("rlnImageName", columnImageNames);

                        // Find the stack with the most particles for visualization
                        int maxStackSize = allStackPaths.Select(kv => kv.Value).Max();
                    
                        foreach (var stack in allStackPaths)
                        {
                            if (!File.Exists(stack.Key))
                                throw new Exception($"Could not find the particle file {stack}.");
                        
                            if (stack.Value == maxStackSize)
                                stackForVis = stack.Key;
                        }

                        // Save the updated STAR file with absolute paths
                        if (tableOptics != null)
                            Star.SaveMultitable(ImportedParticlesPath,
                                                new() { { "optics", tableOptics }, { "particles", tableIn } });
                        else
                            Star.SaveMultitable(ImportedParticlesPath,
                                                new() { { "particles", tableIn } });
                    }
                    logger.WriteLine("Done.");
                }

                logger.Write("Preparing visualization... ");
                {
                    // Generate particle montage for expanded view
                    BakeryWrapper.ParticleImageAtlas(Space.RootDirectory, [ImportedParticlesPath], 20, VisParticlesPath);
                    // Generate job card visualization
                    BakeryWrapper.ImportParticlesJobCard([ImportedParticlesPath], VisCard(0), workingDirectory: Space.RootDirectory);
                    
                    VisAvailableIteration = 0;
                }
                logger.WriteLine("Done.");

                logger.WriteLine("Particles imported successfully");
            }
            catch (Exception exc)
            {
                logger.WriteLine($"An error occurred: {exc.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Tracks the progress of log generation for this job.
    /// Used to notify the UI when logs become available.
    /// </summary>
    /// <returns>An action to execute when logs become available, or null if no update is needed</returns>
    public override Action TrackProgressLogs()
    {
        if (LogsAvailableIteration < 0)
            return () =>
            {
                LogsAvailableIteration = 0;
            };
        
        return null;
    }
}
