using Refund.DataModel;
using Warp;

namespace Refund.JobResources;

/// <summary>
/// Represents a Frame Series dataset containing raw electron microscopy movie data.
/// This is typically the starting point for cryo-EM data processing, containing the
/// direct detector movie frames before motion correction and CTF estimation.
/// </summary>
public class DataSetFs : Resource
{
    #region Location
    
    /// <summary>
    /// Directory containing the raw movie files from the electron microscope.
    /// </summary>
    public string DataDirectory { get; set; } = "";
    
    /// <summary>
    /// Pattern to match movie files in the data directory. Default is "*.eer" for
    /// files recorded with a Falcon 4 detector in electron event representation format.
    /// </summary>
    public string FileSearchPattern { get; set; } = "*.eer";
    
    /// <summary>
    /// Whether to recursively search subdirectories for movie files.
    /// </summary>
    public bool DoRecursiveSearch { get; set; } = false;
    
    #endregion
    
    #region EER

    public int EerFrames { get; set; } = 40;
    
    #endregion

    #region Correction
    
    /// <summary>
    /// Path to a gain reference file used to correct for pixel-to-pixel sensitivity 
    /// variations in the detector.
    /// </summary>
    public string GainPath { get; set; } = "";
    
    /// <summary>
    /// Path to a defects file marking bad pixels or regions on the detector that
    /// should be excluded from processing.
    /// </summary>
    public string DefectsPath { get; set; } = "";
    
    /// <summary>
    /// Whether to flip the gain reference horizontally before applying it.
    /// </summary>
    public bool GainFlipX { get; set; } = false;
    
    /// <summary>
    /// Whether to flip the gain reference vertically before applying it.
    /// </summary>
    public bool GainFlipY { get; set; } = false;
    
    /// <summary>
    /// Whether to transpose the gain reference (swap X and Y axes) before applying it.
    /// </summary>
    public bool GainTranspose { get; set; } = false;
    
    #endregion

    #region Microscope parameters
    
    /// <summary>
    /// Physical size of one pixel in the recorded images, measured in Angstroms.
    /// </summary>
    public decimal PixelSize { get; set; } = 1.0m;
    
    /// <summary>
    /// Binning factor to apply to the images, reducing resolution but improving signal-to-noise ratio.
    /// </summary>
    public decimal BinFactor { get; set; } = 1.0m;
    
    /// <summary>
    /// Total electron dose applied to the specimen during data collection, measured in e⁻/Å².
    /// </summary>
    public decimal OverallExposure { get; set; } = 40m;
    
    /// <summary>
    /// Spherical aberration coefficient of the microscope objective lens, measured in mm.
    /// </summary>
    public decimal Cs { get; set; } = 2.7m;
    
    /// <summary>
    /// Acceleration voltage of the electron microscope, measured in kV.
    /// </summary>
    public decimal Voltage { get; set; } = 300m;
    
    /// <summary>
    /// Amplitude contrast component in image formation, typically 0.07-0.1 for cryo-EM.
    /// </summary>
    public decimal AmplitudeContrast { get; set; } = 0.1m;
    
    #endregion
    
    /// <summary>
    /// Converts this DataSetFs resource to Warp-specific options for processing.
    /// This enables integration with the Warp data processing library.
    /// </summary>
    /// <returns>Warp-compatible options object configured from this dataset's properties</returns>
    public OptionsWarp ToOptionsWarp()
    {
        var result = new OptionsWarp();
        
        result.Import.DataFolder = DataDirectory;
        result.Import.Extension = FileSearchPattern;
        result.Import.DoRecursiveSearch = DoRecursiveSearch;
        
        result.Import.PixelSize = PixelSize;
        result.Import.BinTimes = (decimal)Math.Log2((double)BinFactor);
        result.Import.DosePerAngstromFrame = -OverallExposure;

        result.Import.EERGroupFrames = -EerFrames;
        
        result.Import.CorrectGain = !string.IsNullOrWhiteSpace(GainPath);
        result.Import.GainPath = GainPath ?? "";
        result.Import.GainFlipX = GainFlipX;
        result.Import.GainFlipY = GainFlipY;
        result.Import.GainTranspose = GainTranspose;
        
        result.Import.CorrectDefects = !string.IsNullOrWhiteSpace(DefectsPath);
        result.Import.DefectsPath = DefectsPath ?? "";

        return result;
    }
}