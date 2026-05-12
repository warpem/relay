namespace Refund.Jobs._3D.Class3D;

/// <summary>
/// Provides constants and helper methods for handling Class3D job output files.
/// This utility class centralizes the naming conventions and file paths for the various
/// files produced by RELION 3D classification jobs.
/// </summary>
/// <remarks>
/// Class3DJobFiles serves as a central registry for all file paths and naming conventions,
/// ensuring consistency between real job execution and simulation/testing. It is heavily 
/// 
/// This class provides a comprehensive mapping of all artifacts produced during 3D classification,
/// including data files, model files, class volumes, angular distributions, and auxiliary
/// reconstruction files. The naming conventions match exactly those used by RELION.
/// </remarks>
public class Class3DJobFiles
{
    /// <summary>
    /// RELION job configuration file containing metadata and parameters.
    /// </summary>
    public static string JobStarFile = "job.star";
    
    /// <summary>
    /// Text file containing job description entered by the user.
    /// </summary>
    public static string NoteTxtFile = "note.txt";
    
    /// <summary>
    /// Standard output log file containing the complete job execution log.
    /// with timing delays to mimic actual execution.
    /// </summary>
    public static string RunOutFile = "run.out";
    
    /// <summary>
    /// Standard error log file. This file captures any error messages from RELION.
    /// </summary>
    public static string RunErrFile = "run.err";
    
    /// <summary>
    /// File indicating successful job completion. This marker file is created by RELION
    /// when a job finishes successfully and is used by TrackProgressLogs to confirm completion.
    /// </summary>
    public static string JobExitSuccessFile = "SUCCESS";
    
    /// <summary>
    /// Gets the filename for the particle data STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The formatted filename</returns>
    public static string GetDataStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_data.star";
    }
    
    /// <summary>
    /// Gets the filename for a 3D class volume MRC file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetMrcFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}.mrc";
    }
    
    /// <summary>
    /// Gets the filename for an angular distribution BILD file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetBildFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_angdist.bild";
    }
    
    /// <summary>
    /// Gets the filename for the model metadata STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The formatted filename</returns>
    public static string GetModelStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_model.star";
    }
    
    /// <summary>
    /// Gets the filename for the optimizer parameters STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The formatted filename</returns>
    public static string GetOptimiserStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_optimiser.star";
    }
    
    /// <summary>
    /// Gets the filename for the sampling parameters STAR file for a specific iteration
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>The formatted filename</returns>
    public static string GetSamplingStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_sampling.star";
    }
    
    /// <summary>
    /// Gets the filename for the external reconstruction log file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetExternalReconstructLogFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_external_reconstruct.log";
    }
    
    /// <summary>
    /// Gets the filename for the external reconstruction MRC file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetExternalReconstructMrcFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_external_reconstruct.mrc";
    }
    
    /// <summary>
    /// Gets the filename for the external reconstruction metadata STAR file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetExternalReconstructStarFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_external_reconstruct.star";
    }
    
    /// <summary>
    /// Gets the filename for the external reconstruction imaginary part data MRC file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetExternalReconstructDataImagMrcFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_external_reconstruct_data_imag.mrc";
    }
    
    /// <summary>
    /// Gets the filename for the external reconstruction real part data MRC file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetExternalReconstructDataRealMrcFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_external_reconstruct_data_real.mrc";
    }
    
    /// <summary>
    /// Gets the filename for the external reconstruction weight MRC file for a specific iteration and class
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <param name="classNumber">The class number</param>
    /// <returns>The formatted filename</returns>
    public static string GetExternalReconstructWeightMrcFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_external_reconstruct_weight_real.mrc";
    }

    /// <summary>
    /// Gets an array of all output filenames that should be present for a specific iteration.
    /// This includes data STAR files, model STAR files, reconstruction MRC files, and all
    /// associated auxiliary files for each class.
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>An array of filenames for the specified iteration</returns>
    /// <remarks>
    /// This method is a critical component of the Class3D simulation framework, particularly
    /// for an iteration are properly copied during simulation.
    /// 
    /// The method returns a comprehensive list of all files that RELION would generate
    /// during an actual 3D classification job iteration, ensuring that the simulated job
    /// structure exactly matches what would be produced in a real execution. This enables
    /// accurate testing of file-dependent features like progress tracking and visualization.
    /// 
    /// Currently, the method assumes a 4-class classification job, which is the default
    /// configuration. The files are grouped into categories:
    /// 1. Core metadata files (data, model, optimizer, sampling)
    /// 2. Class volume files (one per class)
    /// 3. Angular distribution files (one per class)
    /// 4. External reconstruction files (multiple per class)
    /// </remarks>
    public static string[] GetOutputFilesForIteration(int iteration)
    {
        return new string[]
        {
            // Main metadata files
            GetDataStarFileForIteration(iteration),
            GetModelStarFileForIteration(iteration),
            GetOptimiserStarFileForIteration(iteration),
            GetSamplingStarFileForIteration(iteration),
            
            // Class volume files
            GetMrcFileForIterationAndClass(iteration, 1),
            GetMrcFileForIterationAndClass(iteration, 2),
            GetMrcFileForIterationAndClass(iteration, 3),
            GetMrcFileForIterationAndClass(iteration, 4),
            
            // Angular distribution files
            GetBildFileForIterationAndClass(iteration, 1),
            GetBildFileForIterationAndClass(iteration, 2),
            GetBildFileForIterationAndClass(iteration, 3),
            GetBildFileForIterationAndClass(iteration, 4),
            
            // External reconstruction files for each class
            GetExternalReconstructLogFileForIterationAndClass(iteration, 1),
            GetExternalReconstructLogFileForIterationAndClass(iteration, 2),
            GetExternalReconstructLogFileForIterationAndClass(iteration, 3),
            GetExternalReconstructLogFileForIterationAndClass(iteration, 4),
            GetExternalReconstructMrcFileForIterationAndClass(iteration, 1),
            GetExternalReconstructMrcFileForIterationAndClass(iteration, 2),
            GetExternalReconstructMrcFileForIterationAndClass(iteration, 3),
            GetExternalReconstructMrcFileForIterationAndClass(iteration, 4),
            GetExternalReconstructStarFileForIterationAndClass(iteration, 1),
            GetExternalReconstructStarFileForIterationAndClass(iteration, 2),
            GetExternalReconstructStarFileForIterationAndClass(iteration, 3),
            GetExternalReconstructStarFileForIterationAndClass(iteration, 4),
            GetExternalReconstructDataImagMrcFileForIterationAndClass(iteration, 1),
            GetExternalReconstructDataImagMrcFileForIterationAndClass(iteration, 2),
            GetExternalReconstructDataImagMrcFileForIterationAndClass(iteration, 3),
            GetExternalReconstructDataImagMrcFileForIterationAndClass(iteration, 4),
            GetExternalReconstructDataRealMrcFileForIterationAndClass(iteration, 1),
            GetExternalReconstructDataRealMrcFileForIterationAndClass(iteration, 2),
            GetExternalReconstructDataRealMrcFileForIterationAndClass(iteration, 3),
            GetExternalReconstructDataRealMrcFileForIterationAndClass(iteration, 4),
            GetExternalReconstructWeightMrcFileForIterationAndClass(iteration, 1),
            GetExternalReconstructWeightMrcFileForIterationAndClass(iteration, 2),
            GetExternalReconstructWeightMrcFileForIterationAndClass(iteration, 3),
            GetExternalReconstructWeightMrcFileForIterationAndClass(iteration, 4),
        };
    }
}