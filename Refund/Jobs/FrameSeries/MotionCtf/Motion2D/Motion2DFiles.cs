namespace Refund.Jobs.FrameSeries.MotionCtf.Motion2D;

/// <summary>
/// Defines the file structure and naming conventions for the Motion2D job.
/// This class serves as a central repository of file paths and naming patterns,
/// ensuring consistency across different components of the job implementation.
/// 
/// to the job's output directory.
/// </summary>
public class Motion2DFiles
{
    /// <summary>
    /// Directory name for storing motion-corrected averaged micrographs.
    /// Creates a dedicated subdirectory to organize output files.
    /// </summary>
    public static string AverageDirectory = "average";
    
    /// <summary>
    /// Directory name for storing processing log files.
    /// Creates a dedicated subdirectory to organize log output.
    /// </summary>
    public static string LogDirectory = "logs";
    
    /// <summary>
    /// Filename for the motion correction settings configuration.
    /// Contains parameters used by the alignment algorithm.
    /// </summary>
    public static string SettingsFile = "align_frameseries.settings";

    /// <summary>
    /// Base filenames (without extensions) for sample micrographs used in testing.
    /// 
    /// In a real production job, these would be dynamically generated based on the
    /// actual input micrographs being processed.
    /// </summary>
    public static string[] FileStems = new string[]
    {
        "20170629_00021_frameImage",
        "20170629_00022_frameImage",
        "20170629_00023_frameImage",
        "20170629_00024_frameImage",
        "20170629_00025_frameImage",
        "20170629_00026_frameImage",
        "20170629_00027_frameImage",
        "20170629_00028_frameImage",
        "20170629_00029_frameImage",
        "20170629_00030_frameImage",
        "20170629_00031_frameImage",
        "20170629_00035_frameImage",
        "20170629_00036_frameImage",
        "20170629_00037_frameImage",
        "20170629_00039_frameImage",
        "20170629_00040_frameImage",
        "20170629_00042_frameImage",
        "20170629_00043_frameImage",
        "20170629_00044_frameImage",
        "20170629_00045_frameImage",
        "20170629_00046_frameImage",
        "20170629_00047_frameImage",
        "20170629_00048_frameImage",
        "20170629_00049_frameImage"
    };

    /// <summary>
    /// Complete filenames for motion correction XML metadata files.
    /// These files contain shift vectors and other alignment information.
    /// </summary>
    public static string[] XmlFiles = FileStems
        .Select(stem => $"{stem}.xml").ToArray();
    
    /// <summary>
    /// Complete paths for motion-corrected averaged micrograph files.
    /// These are the primary output files of motion correction, stored in MRC format.
    /// </summary>
    public static string[] AverageFiles = FileStems
        .Select(stem => Path.Combine(AverageDirectory, $"{stem}.mrc"))
        .ToArray();

    /// <summary>
    /// Complete paths for log files containing processing details.
    /// These files contain information about the motion correction process for each micrograph.
    /// </summary>
    public static string[] LogFiles = FileStems
        .Select(stem => Path.Combine(LogDirectory, $"{stem}.log"))
        .ToArray();
    
    /// <summary>
    /// Gets all output files associated with a specific micrograph by index.
    /// 
    /// copy all files related to a single micrograph in one operation. It's 
    /// particularly useful for simulating incremental processing of multiple
    /// micrographs.
    /// </summary>
    /// <param name="idx">The index of the micrograph in the FileStems array</param>
    /// <returns>An array of file paths for all outputs related to the specified micrograph</returns>
    public static string[] GetOutputFilesForImage(int idx) => new string[]
    {
        XmlFiles[idx],
        AverageFiles[idx],
        LogFiles[idx],
    };
}
