using Refund.DataModel;

namespace Refund.JobResources;

/// <summary>
/// Represents a collection of tomograms reconstructed from tilt series.
/// TomogramSet contains paths and metadata about tomographic reconstructions,
/// including standard, deconvolved, and half-reconstructions for validation.
/// </summary>
public class TomogramSet : Resource
{
    /// <summary>
    /// The source tilt series dataset from which these tomograms were reconstructed.
    /// </summary>
    public TiltSeriesSet TiltSeriesSet { get; set; }

    /// <summary>
    /// Metadata and processing information.
    /// </summary>
    public bool HasMetadata { get; set; } = false;
    public string LatestMetadataDirectory { get; set; }
    
    /// <summary>
    /// JSON files storing processing results.
    /// </summary>
    public string ProcessedItemsJson { get; set; }
    public string FailedItemsJson { get; set; }

    /// <summary>
    /// Pixel size of reconstructed tomograms in Angstroms.
    /// </summary>
    public decimal PixelSize { get; set; }

    /// <summary>
    /// Directory containing standard tomogram reconstructions.
    /// </summary>
    public string TomogramDirectory { get; set; } = "";
    public bool HasTomograms => !string.IsNullOrEmpty(TomogramDirectory);
    public Func<string, string> ToTomogramPath;

    /// <summary>
    /// Directory containing deconvolved tomogram reconstructions.
    /// </summary>
    public string TomogramDeconvDirectory { get; set; } = "";
    public bool HasDeconvTomograms => !string.IsNullOrEmpty(TomogramDeconvDirectory);
    public Func<string, string> ToTomogramDeconvPath;

    /// <summary>
    /// Directory containing first half of tomogram reconstructions (from odd frames or tilts).
    /// </summary>
    public string TomogramHalfMap1Directory { get; set; } = "";
    public bool HasHalfMap1Tomograms => !string.IsNullOrEmpty(TomogramHalfMap1Directory);
    public Func<string, string> ToTomogramHalfMap1Path;

    /// <summary>
    /// Directory containing second half of tomogram reconstructions (from even frames or tilts).
    /// </summary>
    public string TomogramHalfMap2Directory { get; set; } = "";
    public bool HasHalfMap2Tomograms => !string.IsNullOrEmpty(TomogramHalfMap2Directory);
    public Func<string, string> ToTomogramHalfMap2Path;

    /// <summary>
    /// Directory containing denoised tomograms.
    /// </summary>
    public string TomogramDenoisedDirectory { get; set; } = "";
    public bool HasDenoisedTomograms => !string.IsNullOrEmpty(TomogramDenoisedDirectory);
    public Func<string, string> ToTomogramDenoisedPath;

    /// <summary>
    /// Directory containing tomogram thumbnails and slices for visualization.
    /// </summary>
    public string TomogramThumbnailDirectory { get; set; } = "";
    public bool HasTomogramThumbnails => !string.IsNullOrEmpty(TomogramThumbnailDirectory);
    public Func<string, string> ToTomogramThumbnailPath;
    
    /// <summary>
    /// Directory containing correlation volumes for tomograms, if available.
    /// </summary>
    public string TomogramCorrVolumeDirectory { get; set; } = "";
    public bool HasTomogramCorrVolumes => !string.IsNullOrEmpty(TomogramCorrVolumeDirectory);
    public Func<string, string> ToTomogramCorrVolumePath;

    /// <summary>
    /// Indicates whether these tomograms were produced with a deconvolution filter.
    /// </summary>
    public bool HasDeconvolution { get; set; } = false;

    /// <summary>
    /// Indicates whether half-maps are available for denoiser training.
    /// </summary>
    public bool HasHalfMaps { get; set; } = false;
    
    public HalfMapType HalfMapType { get; set; }

    /// <summary>
    /// Indicates whether these tomograms had voxels not contained in some tilt images zeroed out.
    /// </summary>
    public bool OnlyFullVoxelsKept { get; set; } = true;
}

public enum HalfMapType
{
    Frames,
    Tilts
}