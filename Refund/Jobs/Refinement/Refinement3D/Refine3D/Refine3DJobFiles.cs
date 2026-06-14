namespace Refund.Jobs.Refinement.Refinement3D.Refine3D;

/// <summary>
/// Provides a centralized reference for file paths and naming conventions used in Refine3D jobs.
/// making it easier to manage the complex file structure produced by RELION refinement jobs.
/// </summary>
public static class Refine3DJobFiles
{
    /// <summary>
    /// Standard job files that are common to most RELION jobs
    /// </summary>
    public static string NoteTxtFile = "note.txt";
    
    /// <summary>
    /// Standard output file for the RELION job, contains progress information
    /// and is parsed by TrackProgressLogs to update job status
    /// </summary>
    public static string RunOutFile = "run.out";
    
    /// <summary>
    /// File indicating successful job completion, created by RELION
    /// when the job finishes normally
    /// </summary>
    public static string JobExitSuccessFile = "SUCCESS";

    /// <summary>
    /// Final output files produced by RELION at job completion
    /// These files follow RELION's naming convention without iteration numbers
    /// </summary>
    
    /// <summary>
    /// Final filtered 3D reconstruction output map
    /// </summary>
    public static string FinalMapFile = "run_class001.mrc";
    
    /// <summary>
    /// Particle metadata STAR file with alignment parameters
    /// </summary>
    public static string FinalDataStarFile = "run_data.star";
    
    /// <summary>
    /// First half-set unfiltered reconstruction for FSC calculation
    /// </summary>
    public static string FinalHalf1MapFile = "run_half1_class001_unfil.mrc";
    
    /// <summary>
    /// Second half-set unfiltered reconstruction for FSC calculation
    /// </summary>
    public static string FinalHalf2MapFile = "run_half2_class001_unfil.mrc";
    
    /// <summary>
    /// Model parameters including FSC curve and resolution estimates
    /// </summary>
    public static string FinalModelStarFile = "run_model.star";
    
    /// <summary>
    /// Optimization parameters from the final iteration
    /// </summary>
    public static string FinalOptimiserStarFile = "run_optimiser.star";
    
    /// <summary>
    /// Angular and translational sampling parameters from the final iteration
    /// </summary>
    public static string FinalSamplingStarFile = "run_sampling.star";

    /// <summary>
    /// Methods for generating per-iteration output file names following RELION conventions
    /// Each method formats filenames with the iteration number in the standard format
    /// </summary>
    
    /// <summary>
    /// Gets the STAR file containing particle metadata for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the data.star file of the specified iteration</returns>
    public static string GetDataStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_data.star";
    }

    /// <summary>
    /// Gets the 3D reconstruction file for the first half-set at a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the first half-map of the specified iteration</returns>
    public static string GetHalf1MapFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_half1_class001.mrc";
    }

    /// <summary>
    /// Gets the 3D reconstruction file for the second half-set at a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the second half-map of the specified iteration</returns>
    public static string GetHalf2MapFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_half2_class001.mrc";
    }

    /// <summary>
    /// Gets the model parameters file for the first half-set at a specific iteration
    /// Contains FSC curve and resolution estimates
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the first half-set model.star file</returns>
    public static string GetHalf1ModelStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_half1_model.star";
    }

    /// <summary>
    /// Gets the model parameters file for the second half-set at a specific iteration
    /// Contains FSC curve and resolution estimates
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the second half-set model.star file</returns>
    public static string GetHalf2ModelStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_half2_model.star";
    }

    /// <summary>
    /// Gets the optimizer parameters file for a specific iteration
    /// Contains optimization statistics and convergence information
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the optimizer.star file of the specified iteration</returns>
    public static string GetOptimiserStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_optimiser.star";
    }

    /// <summary>
    /// Gets the sampling parameters file for a specific iteration
    /// Contains angular and translational sampling information
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Filename for the sampling.star file of the specified iteration</returns>
    public static string GetSamplingStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_sampling.star";
    }

    /// <summary>
    /// Gets all output files for a specific iteration, used for copying files
    /// files to copy for each simulated iteration.
    /// </summary>
    /// <param name="iteration">The iteration number (0-based)</param>
    /// <returns>Array of filenames for all output files of the specified iteration</returns>
    public static string[] GetOutputFilesForIteration(int iteration)
    {
        return new string[]
        {
            GetDataStarFileForIteration(iteration),
            GetHalf1MapFileForIteration(iteration),
            GetHalf2MapFileForIteration(iteration),
            GetHalf1ModelStarFileForIteration(iteration),
            GetHalf2ModelStarFileForIteration(iteration),
            GetOptimiserStarFileForIteration(iteration),
            GetSamplingStarFileForIteration(iteration)
        };
    }
}