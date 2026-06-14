namespace Refund.Jobs.Refinement.PostProcess.PostProcess3D;

/// <summary>
/// Provides a centralized collection of file names used by the Post-processing job.
/// This class defines the standard output file names for post-processing results,
/// ensuring consistent access across different components of the job implementation.
/// 
/// job execution by copying precalculated results files to the correct locations.
/// </summary>
public class PostProcessJobFiles
{
    /// <summary>
    /// The notes text file containing job status information.
    /// </summary>
    public static string NoteTxtFile = "note.txt";
    
    /// <summary>
    /// The main unmasked post-processed map file in MRC format.
    /// This file contains the B-factor sharpened, FSC-weighted map.
    /// </summary>
    public static string PostProcessMrcFile = "postprocess.mrc";
    
    /// <summary>
    /// The STAR file containing metadata about the post-processing.
    /// Includes FSC curves, applied B-factor values, and resolution estimates.
    /// </summary>
    public static string PostProcessStarFile = "postprocess.star";
    
    /// <summary>
    /// The masked version of the post-processed map file in MRC format.
    /// This is the final, production-ready map with mask applied.
    /// </summary>
    public static string PostProcessMaskedMrcFile = "postprocess_masked.mrc";
    
    /// <summary>
    /// The XML file containing FSC (Fourier Shell Correlation) data.
    /// This file stores resolution assessment metrics in an XML format.
    /// </summary>
    public static string PostProcessXmlFile = "postprocess_fsc.xml";
    
    /// <summary>
    /// The standard output log file from the RELION post-processing command.
    /// Contains detailed information about the post-processing operation.
    /// </summary>
    public static string RunOutFile = "run.out";
}