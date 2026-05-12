using Refund.DataModel;

namespace Refund.JobResources;

/// <summary>
/// Represents a collection of processed electron micrographs, typically motion-corrected
/// and CTF-estimated. MicrographSet is the result of initial preprocessing steps and serves
/// as input for particle picking and extraction procedures.
/// </summary>
public class MicrographSet : Resource
{
    /// <summary>
    /// The source frame series dataset from which these micrographs were derived.
    /// </summary>
    public DataSetFs DataSetFs { get; set; }

    public bool HasMetadata { get; set; } = false;
    
    public string ProcessedItemsJson { get; set; }
    public string FailedItemsJson { get; set; }

    /// <summary>
    /// Directory containing averaged micrographs (motion-corrected and aligned frames).
    /// </summary>
    public string AverageDirectory { get; set; } = "";
    public bool HasAverage => !string.IsNullOrEmpty(AverageDirectory);
    public Func<string, string> ToAveragePath;
    
    /// <summary>
    /// Directory containing odd-frame averages, used for denoiser training.
    /// </summary>
    public string AverageOddDirectory { get; set; } = "";
    public bool HasAverageOdd => !string.IsNullOrEmpty(AverageOddDirectory);
    public Func<string, string> ToAverageOddPath;
    
    /// <summary>
    /// Directory containing even-frame averages, used for denoiser training.
    /// </summary>
    public string AverageEvenDirectory { get; set; } = "";
    public bool HasAverageEven => !string.IsNullOrEmpty(AverageEvenDirectory);
    public Func<string, string> ToAverageEvenPath;
    
    /// <summary>
    /// Directory containing powerspectra for each micrograph.
    /// </summary>
    public string PowerspectrumDirectory { get; set; } = "";
    public bool HasPowerspectrum => !string.IsNullOrEmpty(PowerspectrumDirectory);
    public Func<string, string> ToPowerspectrumPath;
    
    /// <summary>
    /// Directory containing denoised averages for each micrograph.
    /// </summary>
    public string AverageDenoisedDirectory { get; set; } = "";
    public bool HasAverageDenoised => !string.IsNullOrEmpty(AverageDenoisedDirectory);
    public Func<string, string> ToAverageDenoisedPath;
    
    /// <summary>
    /// Directory containing denoiser training data (odd and even frames).
    /// </summary>
    public string DenoiserTrainingDirectory { get; set; } = "";
    public bool HasDenoiserTraining => !string.IsNullOrEmpty(DenoiserTrainingDirectory);
    public string DenoiserModelPath { get; set; } = "";
    public bool HasDenoiserModel => !string.IsNullOrEmpty(DenoiserModelPath);
    
    /// <summary>
    /// Directory containing binary masks for each micrograph.
    /// </summary>
    public string MaskDirectory { get; set; } = "";
    public bool HasMask => !string.IsNullOrEmpty(MaskDirectory);
    public Func<string, string> ToMaskPath;
    
    /// <summary>
    /// Directory containing segmentation results for each micrograph.
    /// </summary>
    public string SegmentationDirectory { get; set; } = "";
    public bool HasSegmentation => !string.IsNullOrEmpty(SegmentationDirectory);
    public Func<string, string> ToSegmentationPath;
    
    /// <summary>
    /// Directory containing membrane models for each micrograph.
    /// </summary>
    public string MembraneDirectory { get; set; } = "";
    public bool HasMembranes => !string.IsNullOrEmpty(MembraneDirectory);
    public Func<string, string> ToMembranePath;
    
    /// <summary>
    /// Directory containing micrograph thumbnails.
    /// </summary>
    public string ThumbnailDirectory { get; set; } = "";
    public bool HasThumbnails => !string.IsNullOrEmpty(ThumbnailDirectory);
    public Func<string, string> ToThumbnailPath;
    
    /// <summary>
    /// Indicates whether the original movies are available for these micrographs.
    public bool HasMovies { get; set; }
    
    /// <summary>
    /// Indicates whether motion correction parameters are available for these micrographs.
    /// </summary>
    public bool HasMotion { get; set; }
    
    /// <summary>
    /// Indicates whether CTF parameters are available for these micrographs.
    /// </summary>
    public bool HasCtf { get; set; }
}
