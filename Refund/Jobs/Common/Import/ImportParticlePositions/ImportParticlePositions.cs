using System.ComponentModel.DataAnnotations;
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
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Common.Import.ImportParticlePositions;

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
                       "Leave empty to detect it automatically from the directory contents. " +
                       "Note that template matching results carry the binned pixel size in their name, so the " +
                       "suffix includes it: 'TS_01_10.00Apx_ribosome.star' has the suffix '_10.00Apx_ribosome'.",
             ConditionalOnField = nameof(InputType),
             ConditionalOnValue = InputTypes.MultipleFiles)]
    [RelayProperty]
    public string MultipleFilesSuffix { get; set; } = "";

    /// <summary>
    /// The file name suffix the job actually imported with, either copied from
    /// <see cref="MultipleFilesSuffix"/> or detected from the directory contents when that was left
    /// empty. Resolved during the run and used from then on to locate per-series STAR files.
    /// Not user-editable.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public string ResolvedFilesSuffix { get; set; } = "";

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
            // Consumers address series by the tomostar file name from processed_items.json, while the
            // STAR files are named after the bare series name, the way WarpTools writes them.
            ToMultiStarPath = InputType == InputTypes.MultipleFiles ?
                                  (n) => Path.Combine(DirectoryPath, $"{WarpHelper.PathToName(n)}{ResolvedFilesSuffix}.star") :
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
                    ResolvedFilesSuffix = ResolveFilesSuffix(logger);

                    var files = MatchingFiles();

                    logger.WriteLine($"Found {files.Count} STAR files in {MultipleFilesDirectory} with suffix '{ResolvedFilesSuffix}'");

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
                        foreach (var file in MatchingFiles())
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

    #region Per-series file name suffix

    /// <summary>
    /// All STAR files in the input directory that carry <see cref="ResolvedFilesSuffix"/>.
    /// Matched in memory rather than through a glob so that a suffix containing wildcard
    /// characters can't widen the selection.
    /// </summary>
    private List<string> MatchingFiles()
    {
        return DirectoryStarFiles(MultipleFilesDirectory)
               .Where(n => Path.GetFileName(n).EndsWith($"{ResolvedFilesSuffix}.star", StringComparison.Ordinal))
               .ToList();
    }

    private static IEnumerable<string> DirectoryStarFiles(string directory)
    {
        return Directory.GetFiles(directory, "*.star")
                        .Where(n => Path.GetFileName(n)[0] != '.');
    }

    /// <summary>
    /// Establishes the suffix to import with: the one the user typed if there is one, otherwise
    /// whatever can be derived from the directory contents. Throws when neither yields files.
    /// </summary>
    private string ResolveFilesSuffix(TextWriter logger)
    {
        var fileNames = DirectoryStarFiles(MultipleFilesDirectory).Select(Path.GetFileName).ToList();

        if (!string.IsNullOrEmpty(MultipleFilesSuffix))
        {
            int matched = fileNames.Count(n => n.EndsWith($"{MultipleFilesSuffix}.star", StringComparison.Ordinal));

            if (matched == 0)
                throw new Exception($"None of the {fileNames.Count} STAR files in {MultipleFilesDirectory} end " +
                                    $"with '{MultipleFilesSuffix}.star'.");

            logger.WriteLine($"Using the specified file name suffix '{MultipleFilesSuffix}' ({matched} files)");

            return MultipleFilesSuffix;
        }

        var detection = DetectSuffix(fileNames);

        if (!detection.Succeeded)
            throw new Exception(DescribeFailedDetection(detection, fileNames.Count));

        logger.WriteLine($"Detected file name suffix '{detection.Suffix}' ({detection.MatchedCount} files)");

        foreach (var ignored in detection.Unmatched)
            logger.WriteLine($"  Ignoring {ignored}: no recognizable suffix");

        return detection.Suffix;
    }

    private static string DescribeFailedDetection(SuffixDetection detection, int fileCount)
    {
        if (detection.Candidates.Count == 0)
            return $"Could not derive a file name suffix from the {fileCount} STAR file(s) found. Template " +
                   "matching results are named '<series>_<pixel size>Apx_<template>.star'; if these aren't, " +
                   "specify the suffix manually.";

        string listed = string.Join(", ", detection.Candidates
                                                   .OrderByDescending(kv => kv.Value)
                                                   .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                                   .Select(kv => $"'{kv.Key}' ({kv.Value} files)"));

        return $"Found more than one candidate file name suffix: {listed}. This usually means the directory " +
               "holds results for several templates or binnings. Specify the one to import manually.";
    }

    /// <summary>
    /// Derives the per-series file name suffix from a listing of STAR file names.
    /// </summary>
    /// <remarks>
    /// Anchors on the binned pixel size that WarpTools stamps into every name it writes
    /// (see <c>TiltSeries.ToTomogramWithPixelSize</c>), taking the last occurrence so that a series
    /// named after an earlier matching run doesn't mislead it. The part before the anchor is the
    /// series name and is never parsed, so arbitrary acquisition-software naming survives intact.
    /// Detection only succeeds when every recognized file agrees on one suffix; several candidates
    /// mean genuine ambiguity and are reported rather than guessed between.
    /// </remarks>
    /// <param name="fileNames">STAR file names or paths to inspect.</param>
    internal static SuffixDetection DetectSuffix(IEnumerable<string> fileNames)
    {
        Dictionary<string, int> candidates = new(StringComparer.Ordinal);
        List<string> unmatched = new();

        foreach (var path in fileNames)
        {
            string fileName = Path.GetFileName(path);
            var match = SuffixPattern.Match(fileName);

            if (!match.Success)
            {
                unmatched.Add(fileName);
                continue;
            }

            string suffix = match.Groups["suffix"].Value;
            candidates[suffix] = candidates.GetValueOrDefault(suffix) + 1;
        }

        bool unambiguous = candidates.Count == 1;

        return new SuffixDetection
        {
            Suffix = unambiguous ? candidates.Keys.Single() : null,
            MatchedCount = unambiguous ? candidates.Values.Single() : 0,
            Candidates = candidates,
            Unmatched = unmatched
        };
    }

    /// <summary>
    /// Splits a WarpTools-written STAR file name into the series name and everything from the
    /// binned pixel size onwards. The greedy leading group makes the last pixel size win.
    /// The decimal separator varies because WarpTools formats it with the ambient culture.
    /// </summary>
    private static readonly Regex SuffixPattern = new(@"^(?<series>.*)(?<suffix>_\d+[.,]\d{2}Apx.*)\.star$",
                                                      RegexOptions.Compiled | RegexOptions.IgnoreCase);

    #endregion
}

/// <summary>
/// What <see cref="ImportParticlePositions.DetectSuffix"/> made of a directory listing.
/// </summary>
internal class SuffixDetection
{
    /// <summary>The single suffix all recognized files agreed on, or null if detection was inconclusive.</summary>
    public string Suffix { get; init; }

    /// <summary>Number of files carrying <see cref="Suffix"/>, or zero when detection failed.</summary>
    public int MatchedCount { get; init; }

    /// <summary>Every distinct suffix seen, and how many files carried it.</summary>
    public IReadOnlyDictionary<string, int> Candidates { get; init; } = new Dictionary<string, int>();

    /// <summary>Files with no recognizable suffix. Reported so silent omissions stay visible.</summary>
    public IReadOnlyList<string> Unmatched { get; init; } = [];

    public bool Succeeded => Suffix != null;
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