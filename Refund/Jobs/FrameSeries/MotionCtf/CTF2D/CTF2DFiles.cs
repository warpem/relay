using System;
using System.IO;
using System.Linq;

namespace Refund.Jobs.FrameSeries.MotionCtf.CTF2D;

/// <summary>
/// Provides file management utilities for the CTF2D job, defining standard output file structures
/// and methods to access simulation data for development and testing. 
///
/// This class centralizes the file naming conventions and directory structure used by the CTF2D job,
/// ensuring consistent file organization across job instances.
/// </summary>
public class CTF2DFiles
{
    /// <summary>
    /// Directory containing power spectrum images generated from CTF estimation.
    /// These power spectra are essential for visual validation of CTF fitting quality.
    /// </summary>
    public static string PowerSpectrumDirectory = "average";

    /// <summary>
    /// Directory where processing log files are stored, containing detailed information
    /// about the CTF estimation process for each micrograph.
    /// </summary>
    public static string LogDirectory = "logs";

    /// <summary>
    /// The settings file containing parameters used for CTF estimation.
    /// </summary>
    public static string SettingsFile = "ctf_movies.settings";

    /// <summary>
    /// Collection of file stem names representing the test dataset for CTF estimation.
    /// These stems form the base for constructing the XML, power spectrum, and log files.
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
    /// XML output files containing CTF parameters determined by the estimation process.
    /// Each XML file corresponds to a micrograph and contains detailed CTF parameters.
    /// </summary>
    public static string[] XmlFiles = FileStems
        .Select(stem => $"{stem}.xml").ToArray();
    
    /// <summary>
    /// Power spectrum MRC files used for visual inspection of CTF estimation quality.
    /// These files show the radial average power spectrum with fitted CTF profiles.
    /// </summary>
    public static string[] PowerSpectrumFiles = FileStems
        .Select(stem => Path.Combine(PowerSpectrumDirectory, $"{stem}.mrc"))
        .ToArray();

    /// <summary>
    /// Log files containing detailed information about the CTF estimation process.
    /// Used for debugging and tracking the fitting procedure for each micrograph.
    /// </summary>
    public static string[] LogFiles = FileStems
        .Select(stem => Path.Combine(LogDirectory, $"{stem}.log"))
        .ToArray();
    
    /// <summary>
    /// Retrieves all output files associated with a specific micrograph's CTF estimation.
    /// 
    /// the appropriate files for each micrograph being processed.
    /// </summary>
    /// <param name="idx">The index of the micrograph in the FileStems array</param>
    /// <returns>An array of file paths representing all output files for the specified micrograph</returns>
    public static string[] GetOutputFilesForImage(int idx) => new string[]
    {
        XmlFiles[idx],
        PowerSpectrumFiles[idx],
        LogFiles[idx],
    };
}