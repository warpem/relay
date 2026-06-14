namespace Refund.Jobs.Refinement.Classes2D.Class2D;

/// <summary>
/// Provides standardized file naming and access for RELION 2D classification job files
/// </summary>
/// <remarks>
/// This class centralizes the file naming conventions for RELION 2D classification jobs,
/// making it easier to locate and manage the various output files produced during job execution.
/// It provides methods to generate iteration-specific filenames and lists of expected output files.
/// </remarks>
public class Class2DJobFiles
{
    /// <summary>
    /// The STAR file containing job metadata
    /// </summary>
    public static string JobStarFile = "job.star";
    
    /// <summary>
    /// The text file containing user notes about the job
    /// </summary>
    public static string NoteTxtFile = "note.txt";
    
    /// <summary>
    /// The standard output log file
    /// </summary>
    public static string RunOutFile = "run.out";
    
    /// <summary>
    /// The standard error log file
    /// </summary>
    public static string RunErrFile = "run.err";
    
    /// <summary>
    /// The file indicating successful job completion
    /// </summary>
    public static string JobExitSuccessFile = "SUCCESS";
    
    /// <summary>
    /// Gets the filename for the particle data STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The filename for the data STAR file</returns>
    /// <remarks>
    /// This file contains particle metadata including assigned classes and orientations
    /// </remarks>
    public static string GetDataStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_data.star";
    }
    
    /// <summary>
    /// Gets the filename for the class averages MRC stack for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The filename for the class averages MRC file</returns>
    /// <remarks>
    /// This file contains the 2D class average images as a stack of 2D slices
    /// </remarks>
    public static string GetClassesFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_classes.mrcs";
    }
    
    /// <summary>
    /// Gets the filename for the model parameters STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The filename for the model STAR file</returns>
    /// <remarks>
    /// This file contains class statistics and parameters, including particle distribution
    /// and estimated resolution
    /// </remarks>
    public static string GetModelStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_model.star";
    }
    
    /// <summary>
    /// Gets the filename for the optimizer parameters STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The filename for the optimizer STAR file</returns>
    /// <remarks>
    /// This file contains parameters used by the optimization algorithm, including
    /// sampling and convergence information
    /// </remarks>
    public static string GetOptimiserStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_optimiser.star";
    }
    
    /// <summary>
    /// Gets the filename for the first moment data file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The filename for the first moment MRC file</returns>
    /// <remarks>
    /// Used by the VDAM algorithm to store gradient moment information
    /// </remarks>
    public static string Get1MomentMrcsFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_1moment.mrcs";
    }
    
    /// <summary>
    /// Gets the filename for the second moment data file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The filename for the second moment MRC file</returns>
    /// <remarks>
    /// Used by the VDAM algorithm to store gradient moment information
    /// </remarks>
    public static string Get2MomentMrcsFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_2moment.mrcs";
    }
    
    /// <summary>
    /// Gets an array of all output filenames for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>An array of filenames for all expected output files for the iteration</returns>
    /// <remarks>
    /// This method provides a comprehensive list of all files that should be produced
    /// for each iteration of a 2D classification job. It's used for checking job progress
    /// </remarks>
    public static string[] GetOutputFilesForIteration(int iteration)
    {
        return new string[]
        {
            GetDataStarFileForIteration(iteration),
            GetClassesFileForIteration(iteration),
            GetModelStarFileForIteration(iteration),
            Get1MomentMrcsFileForIteration(iteration),
            Get2MomentMrcsFileForIteration(iteration),
            GetOptimiserStarFileForIteration(iteration),
        };
    }
}