using Refund.DataModel;
using Warp;
using Warp.Tools;

namespace Refund.JobResources;

/// <summary>
/// Represents a Tilt Series dataset containing electron tomography data.
/// This is typically used for cryo-electron tomography (cryo-ET) processing,
/// containing images of the same specimen area recorded at different tilt angles.
/// </summary>
public class DataSetTs : Resource
{
    public string SettingsPath { get; set; } = "";
    
    /// <summary>
    /// Set of micrographs that have been aligned within the tilt series,
    /// corrected for beam-induced motion and potentially CTF-estimated.
    /// </summary>
    public MicrographSet Micrographs { get; set; }
    
    /// <summary>
    /// Directory containing tilt series data, typically with .tomostar files
    /// </summary>
    public string DataDirectory { get; set; } = "";
    
    /// <summary>
    /// Dimensions of the reconstructed tomogram in pixels (X, Y, Z).
    /// </summary>
    public int3 TomogramDimensions { get; set; }
    
    /// <summary>
    /// Converts this DataSetTs resource to Warp-specific options for tomography processing.
    /// This enables integration with the Warp data processing library for tomography.
    /// </summary>
    /// <returns>Warp-compatible options object configured for tomography processing</returns>
    public OptionsWarp ToOptionsWarp()
    {
        var optionsWarp = Micrographs.DataSetFs.ToOptionsWarp();
        
        optionsWarp.Import.DataFolder = DataDirectory;
        optionsWarp.Import.Extension = "*.tomostar";
        optionsWarp.Import.DoRecursiveSearch = false;
        
        optionsWarp.Tomo.DimensionsX = TomogramDimensions.X;
        optionsWarp.Tomo.DimensionsY = TomogramDimensions.Y;
        optionsWarp.Tomo.DimensionsZ = TomogramDimensions.Z;
        
        return optionsWarp;
    }
}