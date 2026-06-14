namespace Refund.Jobs.Refinement.InitialModel.InitialReference3D;

/// <summary>
/// Manages file paths and naming conventions for Initial Reference 3D jobs.
/// This utility class provides standardized access to common file paths and patterns
/// used throughout the initial reference generation workflow.
/// </summary>
/// <remarks>
/// to locate and copy the required output files during simulated job execution. The class primarily
/// serves two purposes:
/// 
/// 1. Defines static constants for commonly accessed files:
///    - Log files (run.out, run.err)
///    - Job metadata files (job.star, note.txt)
///    - Success markers (RELION_JOB_EXIT_SUCCESS)
///    - Output model files (initial_model.mrc)
/// 
/// 2. Provides methods that generate standardized file paths following RELION naming conventions:
///    - Star files for data, model, optimizer, and sampling information
///    - MRC files for reconstructed volumes and moment calculations
///    - Visualization files like angular distribution BILDs
/// 
/// The GetOutputFilesForIteration method is particularly important for the simulation system,
/// files for a specific iteration at once:
/// 
/// ```csharp
/// public void CopyResultsForIteration(int iteration)
/// {
///     var files = InitialReferenceJobFiles.GetOutputFilesForIteration(iteration);
///     foreach (var file in files)
///         if (File.Exists(Path.Combine(_preCalculatedDataDirectory, file)))
///             CopyFile(file);
/// }
/// ```
/// </remarks>
public class InitialReferenceJobFiles
{
    // static refs to filenames
    public static string JobStarFile = "job.star";
    public static string NoteTxtFile = "note.txt";
    public static string RunOutFile = "run.out";
    public static string RunErrFile = "run.err";
    public static string JobExitSuccessFile = "SUCCESS";

    public static string InitialModelMrcFile = "initial_model.mrc";

    public static string GetDataStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_data.star";
    }

    public static string GetMrcFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}.mrc";
    }

    public static string GetMrcFileForIterationAndMoment(int iteration,
        int preMomentNumber, int momentNumber)
    {
        return $"run_it{iteration:D3}_{preMomentNumber}moment{momentNumber:D3}.mrc";
    }

    public static string GetBildFileForIterationAndClass(int iteration, int classNumber)
    {
        return $"run_it{iteration:D3}_class{classNumber:D3}_angdist.bild";
    }

    public static string GetModelStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_model.star";
    }

    public static string GetOptimiserStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_optimiser.star";
    }

    public static string GetSamplingStarFileForIteration(int iteration)
    {
        return $"run_it{iteration:D3}_sampling.star";
    }

    /// <summary>
    /// Returns an array of all expected output file paths for a specific iteration.
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>
    /// An array of file paths that should be present after the specified iteration completes,
    /// including data files, model files, and reconstructed volumes
    /// </returns>
    /// <remarks>
    /// This method plays a critical role in the simulation system, as it's used by 
    /// copied from the template directory for each iteration. By centralizing this logic,
    /// the simulation system can accurately reproduce the expected file structure without
    /// duplicating file path knowledge across multiple classes.
    /// 
    /// The returned array includes:
    /// - Data STAR file containing particle metadata
    /// - Model STAR file with class information and statistics
    /// - Optimizer and sampling STAR files with refinement parameters
    /// - MRC files for the 3D reconstruction and moment calculations
    /// </remarks>
    public static string[] GetOutputFilesForIteration(int iteration)
    {
        return new string[]
        {
            GetDataStarFileForIteration(iteration),
            GetModelStarFileForIteration(iteration),
            GetOptimiserStarFileForIteration(iteration),
            GetSamplingStarFileForIteration(iteration),
            GetMrcFileForIterationAndClass(iteration, 1),
            GetMrcFileForIterationAndMoment(iteration, 1, 1),
            GetMrcFileForIterationAndMoment(iteration, 1, 2),
            GetMrcFileForIterationAndMoment(iteration, 2, 1),
        };
    }
}