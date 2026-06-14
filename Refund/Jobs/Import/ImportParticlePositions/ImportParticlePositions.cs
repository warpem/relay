using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Import.ImportParticlePositions;

[GenerateReadOnly]
public class ImportParticlePositions : LocalJob, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import particles job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "ce608a96-4342-4164-917b-3b4e0c75a811";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "Common.Import.Particle positions";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Import particle positions";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Import particle positions";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Imports a set of particle positions from one or multiple RELION-style star files, " +
                                              "including metadata such as shifts, positions, angles, CTF parameters, and classes.";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

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
    [Clearable]
    public bool HasShifts { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have position information.
    /// This is automatically detected based on the presence of rlnCoordinateX/rlnCoordinateXAngst columns.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public bool HasCoordinates { get; set; } = false;
    
    [RelayProperty]
    [Clearable]
    public bool HasNormalizedCoordinates { get; set; } = false;
    
    [RelayProperty]
    [Clearable]
    public decimal CoordinatePixelSize { get; set; } = 1M;
    
    [RelayProperty]
    [Clearable]
    public bool Has3dCoords { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have orientation angle information.
    /// This is automatically detected based on the presence of rlnAnglePsi column.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public bool HasAngles { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have CTF parameter information.
    /// This is automatically detected based on the presence of rlnDefocusU column.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public bool HasCtf { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have classification information.
    /// This is automatically detected based on the presence of rlnClassNumber column.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public bool HasClasses { get; set; } = false;
    
    /// <summary>
    /// Gets or sets whether the imported particles have scale information.
    /// This is automatically detected based on the presence of rlnScale column.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public bool HasScale { get; set; } = false;

    [RelayProperty]
    [Clearable]
    public bool HasImageData { get; set; } = false;

    #region Parameters

    /// <summary>
    /// Gets or sets the path to the particle star file to be imported.
    /// Must point to a valid RELION format star file containing particle metadata.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiEnum("", "Input type", typeof(InputTypes),
            helpText: "Select how to import particle positions. " +
                      "Single file imports a single RELION-style STAR file, " +
                      "while multiple files imports a STAR file for each micrograph or tilt series.")]
    [RelayProperty]
    public InputTypes InputType { get; set; } = InputTypes.SingleFile;
    
    [UiPath("", "Single file path",
            SelectionMode.SingleFile,
            ["*.star", ],
            helpText: "Path to the particle metadata to be imported.",
            ConditionalOnField = nameof(InputType),
            ConditionalOnValue = InputTypes.SingleFile)]
    [RelayProperty]
    public string SingleFilePath { get; set; } = "";

    [UiPath("", "Directory path",
            SelectionMode.SingleFolder,
            null,
            helpText: "Path to the directory containing multiple STAR files" +
                      "each corresponding to a single micrograph or tilt series.",
            ConditionalOnField = nameof(InputType),
            ConditionalOnValue = InputTypes.MultipleFiles)]
    [RelayProperty]
    public string MultipleFilesDirectory { get; set; } = "";
    
    [UiString("", "File name suffix",
             helpText: "Suffix to append to the micrograph or tilt series name to arrive at the STAR file name. " +
                       "For example, if the suffix is '_particles', the job will look for files like 'micrograph001_particles.star'.",
             ConditionalOnField = nameof(InputType),
             ConditionalOnValue = InputTypes.MultipleFiles)]
    [RelayProperty]
    public string MultipleFilesSuffix { get; set; } = "";

    [UiDecimalNullable("", "Pixel size",
                       min: 0.0001,
                       stepSize: 0.0001,
                       helpText: "Override or specify the pixel size for the particle coordinates.",
                       Unit = "Å")]
    [RelayProperty]
    public decimal? PixelSize { get; set; } = null;
    
    #endregion

    /// <summary>
    /// Gets the path where the imported particle metadata will be stored within the job directory.
    /// </summary>
    public string ImportedSinglePath => Path.Combine(DirectoryPath, "particles.star");
    
    public const string PortInParticles = "Particles";

    /// <summary>
    /// Initializes a new instance of the ImportParticles job.
    /// Configures the output port that will provide the imported particles to downstream jobs.
    /// </summary>
    public ImportParticlePositions()
    {
        PortsIn = new(new Dictionary<string, PortIn>());

        var PortOutParticles = new PortOut(this, typeof(ParticleSet), PortInParticles, "Particle positions", GetParticles);

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
        
        if (InputType == InputTypes.SingleFile)
        {
            if (string.IsNullOrWhiteSpace(SingleFilePath))
                errors[nameof(SingleFilePath)] = "Single file path must be specified.";
            else if (!File.Exists(SingleFilePath))
                errors[nameof(SingleFilePath)] = $"The specified file does not exist: {SingleFilePath}";
        }
        else if (InputType == InputTypes.MultipleFiles)
        {
            if (string.IsNullOrWhiteSpace(MultipleFilesDirectory))
                errors[nameof(MultipleFilesDirectory)] = "Directory path must be specified.";
            else if (!Directory.Exists(MultipleFilesDirectory))
                errors[nameof(MultipleFilesDirectory)] = $"The specified directory does not exist: {MultipleFilesDirectory}";
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
            ParticlesSingleStarPath = InputType == InputTypes.SingleFile ? 
                                          ImportedSinglePath : 
                                          null,
            ParticlesMultiStarDirectory = InputType == InputTypes.MultipleFiles ? 
                                              DirectoryPath : 
                                              null,
            ToMultiStarPath = InputType == InputTypes.MultipleFiles ? 
                                  (n) => Path.Combine(DirectoryPath, $"{n}{MultipleFilesSuffix}.star") : 
                                  null,
            HasShifts = HasShifts,
            HasPositions = HasCoordinates,
            HasNormalizedCoords = HasNormalizedCoordinates,
            CoordPixelSize = CoordinatePixelSize,
            HasData = HasImageData,
            Has3dCoords = Has3dCoords,
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
                logger.WriteLine($"Importing particles from {(InputType == InputTypes.SingleFile ? SingleFilePath : MultipleFilesDirectory)}");

                #region Figure out the file to check
                
                string fileToCheck = null;
                
                if (InputType == InputTypes.SingleFile)
                {
                    fileToCheck = SingleFilePath;
                }
                else
                {
                    var files = Directory.GetFiles(MultipleFilesDirectory, $"*{MultipleFilesSuffix}.star")
                                         .Where(n => Path.GetFileName(n)[0] != '.')
                                         .ToList();
                    
                    if (files.Count == 0)
                        throw new Exception($"No STAR files found in {MultipleFilesDirectory} with suffix '{MultipleFilesSuffix}'.");
                    
                    logger.WriteLine($"Found {files.Count} STAR files in {MultipleFilesDirectory} with suffix '{MultipleFilesSuffix}'");
                    
                    fileToCheck = files[0];
                }
                
                #endregion
                
                logger.WriteLine($"Checking columns in {fileToCheck}:");
                {
                    Star tableIn = null;
                    Star tableOptics = null;

                    // Handle both old-style and new-style (multi-table) STAR files
                    if (Star.IsMultiTable(fileToCheck))
                    {
                        tableIn = new(fileToCheck, "particles");
                        tableOptics = new(fileToCheck, "optics");
                    }
                    else
                        tableIn = new(fileToCheck);

                    if (tableIn.RowCount == 0)
                        throw new Exception("The particle table is empty.");

                    if (tableIn.HasColumn("rlnCoordinateX") || tableIn.HasColumn("rlnCoordinateXAngst"))
                    {
                        HasCoordinates = true;
                        logger.WriteLine("Found coordinates");

                        if (tableIn.HasColumn("rlnCoordinateX") && tableIn.GetFloat("rlnCoordinateX").All(v => v < 1.1f))
                        {
                            HasNormalizedCoordinates = true;
                            logger.WriteLine("Coordinates appear to be normalized to [0, 1]");
                        }
                    }
                    
                    if (tableIn.HasColumn("rlnCoordinateZ") || 
                        tableIn.HasColumn("rlnCoordinateZAngst") ||
                        tableIn.HasColumn("rlnOriginZ") ||
                        tableIn.HasColumn("rlnOriginZAngst"))
                    {
                        Has3dCoords = true;
                        logger.WriteLine("Coordinates have a Z component");
                    }

                    // Auto-detect what metadata is available in the STAR file
                    if (tableIn.HasColumn("rlnAnglePsi"))
                    {
                        HasAngles = true;
                        logger.WriteLine("Found orientation angles");
                    }

                    if (tableIn.HasColumn("rlnDefocusU"))
                    {
                        HasCtf = true;
                        logger.WriteLine("Found CTF parameters");
                    }

                    if (tableIn.HasColumn("rlnClassNumber"))
                    {
                        HasClasses = true;
                        logger.WriteLine("Found class numbers");
                    }
                    
                    if (tableOptics == null && PixelSize == null && 
                        (tableIn.HasColumn("rlnOriginX") || 
                         (tableIn.HasColumn("rlnCoordinateX") && !HasNormalizedCoordinates)))
                        throw new Exception("Pixel size must be specified or optics table must be present when using " +
                                            "coordinates or shifts without physical units.");

                    if (PixelSize != null)
                    {
                        CoordinatePixelSize = PixelSize.Value;
                        logger.WriteLine($"Using specified pixel size: {CoordinatePixelSize} Å");
                    }
                    else if (tableIn.HasColumn("rlnOriginX") || 
                             (tableIn.HasColumn("rlnCoordinateX") && !HasNormalizedCoordinates))
                    {
                        if ( tableOptics == null || !tableOptics.HasColumn("rlnImagePixelSize"))
                            throw new Exception("Optics table doesn't have rlnImagePixelSize column, but it's needed for " +
                                                "figuring out the pixel size of coordinates or shifts.");
                        
                        CoordinatePixelSize = decimal.Parse(tableOptics.GetColumn("rlnImagePixelSize")[0], CultureInfo.InvariantCulture);
                    }
                    
                    if (tableIn.HasColumn("rlnOriginX") || tableIn.HasColumn("rlnOriginXAngst"))
                    {
                        HasShifts = true;
                        logger.WriteLine("Found shifts");
                    }

                    if (tableIn.HasColumn("rlnScale"))
                    {
                        HasScale = true;
                        logger.WriteLine("Found signal intensity scale factors");
                    }
                }
                
                logger.Write("Copying files... ");
                {
                    if (InputType == InputTypes.SingleFile)
                    {
                        File.Copy(SingleFilePath, ImportedSinglePath, true);
                    }
                    else
                    {
                        var files = Directory.GetFiles(MultipleFilesDirectory, $"*{MultipleFilesSuffix}.star")
                                             .Where(n => Path.GetFileName(n)[0] != '.')
                                             .ToList();
                    
                        foreach (var file in files)
                            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);
                    }
                }
                logger.WriteLine("Done.");

                logger.WriteLine("Particle positions imported successfully");
            }
            catch (Exception exc)
            {
                logger.WriteLine($"An error occurred: {exc.Message}");
                throw;
            }
        }
    }
}
    
public enum InputTypes
{
    [Display(Name = "Single file",
             Description = "Import particle positions from a single RELION-style STAR file.")]
    SingleFile,
    
    [Display(Name = "Per-series files",
             Description = "Import particle positions from multiple STAR files, each belonging to a single micrograph or tilt series.")]
    MultipleFiles
}