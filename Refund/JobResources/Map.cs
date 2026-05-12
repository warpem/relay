using System.Collections.ObjectModel;
using Refund.DataModel;

namespace Refund.JobResources;

/// <summary>
/// Represents a 3D volumetric map resource, typically resulting from 3D reconstruction or refinement.
/// A Map can contain multiple representations (half-maps, average, postprocessed) and associated
/// metadata such as FSC curves and visualization resources.
/// </summary>
public class Map : Resource
{
    /// <summary>
    /// Path to the first half-map volume file, used for resolution estimation via FSC.
    /// </summary>
    public readonly string Half1VolumePath;
    
    /// <summary>
    /// Path to the second half-map volume file, used for resolution estimation via FSC.
    /// </summary>
    public readonly string Half2VolumePath;
    
    /// <summary>
    /// Path to the averaged map volume file (combining both half-maps).
    /// </summary>
    public readonly string AverageVolumePath;
    
    /// <summary>
    /// Path to the post-processed map volume file, typically sharpened and filtered.
    /// </summary>
    public readonly string PostprocessedVolumePath;
    
    /// <summary>
    /// Path to a mask volume file used for focused refinement or post-processing.
    /// </summary>
    public readonly string MaskVolumePath;
    
    /// <summary>
    /// Path to a STAR file containing FSC (Fourier Shell Correlation) data for resolution estimation.
    /// </summary>
    public readonly string FSCStarPath;

    /// <summary>
    /// Indicates whether the map is on an absolute scale (e.g., for direct atomic modeling).
    /// </summary>
    public readonly bool IsAbsoluteScale;

    /// <summary>
    /// Collection of paths to various visualization resources for this map, such as orthoslice views,
    /// FSC plots, or angular distribution plots.
    /// </summary>
    public readonly ReadOnlyDictionary<VisTypes, string> VisualizationPaths;
    
    /// <summary>
    /// Indicates whether a first half-map volume is available.
    /// </summary>
    public bool HasHalf1Volume => !string.IsNullOrEmpty(Half1VolumePath);
    
    /// <summary>
    /// Indicates whether a second half-map volume is available.
    /// </summary>
    public bool HasHalf2Volume => !string.IsNullOrEmpty(Half2VolumePath);
    
    /// <summary>
    /// Indicates whether an average map volume is available.
    /// </summary>
    public bool HasAverageVolume => !string.IsNullOrEmpty(AverageVolumePath);
    
    /// <summary>
    /// Indicates whether a post-processed map volume is available.
    /// </summary>
    public bool HasPostprocessedVolume => !string.IsNullOrEmpty(PostprocessedVolumePath);
    
    /// <summary>
    /// Indicates whether a mask volume is available.
    /// </summary>
    public bool HasMaskVolume => !string.IsNullOrEmpty(MaskVolumePath);
    
    /// <summary>
    /// Indicates whether FSC data in STAR format is available.
    /// </summary>
    public bool HasFSCStar => !string.IsNullOrEmpty(FSCStarPath);

    /// <summary>
    /// Creates a new Map resource with paths to various map representations and visualization resources.
    /// </summary>
    /// <param name="half1VolumePath">Path to the first half-map volume</param>
    /// <param name="half2VolumePath">Path to the second half-map volume</param>
    /// <param name="averageVolumePath">Path to the averaged map volume</param>
    /// <param name="postprocessedVolumePath">Path to the post-processed map volume</param>
    /// <param name="maskVolumePath">Path to a mask volume</param>
    /// <param name="fscStarPath">Path to FSC data in STAR format</param>
    /// <param name="isAbsoluteScale">Whether the map is on an absolute scale</param>
    /// <param name="visualizationPaths">Dictionary of paths to visualization resources</param>
    public Map(string half1VolumePath = null, 
               string half2VolumePath = null, 
               string averageVolumePath = null, 
               string postprocessedVolumePath = null, 
               string maskVolumePath = null,
               string fscStarPath = null, 
               bool isAbsoluteScale = false,
               Dictionary<VisTypes, string> visualizationPaths = null)
    {
        Half1VolumePath = half1VolumePath;
        Half2VolumePath = half2VolumePath;
        AverageVolumePath = averageVolumePath;
        PostprocessedVolumePath = postprocessedVolumePath;
        MaskVolumePath = maskVolumePath;
        FSCStarPath = fscStarPath;
        
        IsAbsoluteScale = isAbsoluteScale;

        if (visualizationPaths != null)
            VisualizationPaths = visualizationPaths.AsReadOnly();
        else
            VisualizationPaths = new Dictionary<VisTypes, string>().AsReadOnly();
    }

    /// <summary>
    /// Types of visualization resources that can be associated with a map.
    /// </summary>
    public enum VisTypes
    {
        /// <summary>Orthogonal slice images through the 3D volume</summary>
        OrthoSlices,
        
        /// <summary>2D projections of the 3D volume</summary>
        Projections,
        
        /// <summary>Fourier Shell Correlation plots for resolution estimation</summary>
        Fsc,
        
        /// <summary>Particle angular distribution plots</summary>
        AngularDistribution,
        
        /// <summary>Fourier space sampling plots</summary>
        FourierSampling,
        
        /// <summary>General statistics about the 3D volume</summary>
        Statistics
    }

    /// <summary>
    /// Returns a collection of downloadable resources associated with this map.
    /// </summary>
    /// <returns>Collection of resources that can be downloaded by the user</returns>
    public override IEnumerable<Downloadable> GetDownloadables()
    {
        List<Downloadable> result = new();
        
        if (HasHalf1Volume)
            result.Add(new Downloadable("Half-map 1", "", Half1VolumePath));
        
        if (HasHalf2Volume)
            result.Add(new Downloadable("Half-map 2", "", Half2VolumePath));
        
        if (HasAverageVolume)
            result.Add(new Downloadable("Average map", "", AverageVolumePath));
        
        if (HasPostprocessedVolume)
            result.Add(new Downloadable("Post-processed map", "", PostprocessedVolumePath));
        
        if (HasMaskVolume)
            result.Add(new Downloadable("Mask", "", MaskVolumePath));
        
        if (HasFSCStar)
            result.Add(new Downloadable("FSC", "", FSCStarPath));
        
        return result;
    }

    public string GetAverageOrSimilar()
    {
        if (HasAverageVolume)
            return AverageVolumePath;
        if (HasPostprocessedVolume)
            return PostprocessedVolumePath;
        if (HasHalf1Volume)
            return Half1VolumePath;
        if (HasHalf2Volume)
            return Half2VolumePath;

        return null;
    }

    public string GetHalfMapOrSimilar()
    {
        if (HasHalf1Volume)
            return Half1VolumePath;
        if (HasHalf2Volume)
            return Half2VolumePath;

        return null;
    }
}
