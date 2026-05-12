using System;
using System.Runtime.Serialization;

namespace Refund;

/// <summary>
/// Defines the various types of 3D density maps used in cryo-electron microscopy processing.
/// Each type represents a specific stage or representation of the electron density data.
/// These types are used throughout the application to identify the purpose and handling of map resources.
/// </summary>
public enum MapTypes
{
    /// <summary>
    /// Standard reconstructed density map, typically the final averaged map after processing.
    /// </summary>
    [EnumMember(Value = "Map")]
    Map,

    /// <summary>
    /// Binary or continuous volume used to mask (include/exclude) regions of a density map.
    /// </summary>
    [EnumMember(Value = "Mask")]
    Mask,

    /// <summary>
    /// First half-map from independently processed datasets, used for FSC validation.
    /// </summary>
    [EnumMember(Value = "Half map 1")]
    HalfMap1,

    /// <summary>
    /// Second half-map from independently processed datasets, used for FSC validation.
    /// </summary>
    [EnumMember(Value = "Half map 2")]
    HalfMap2,

    /// <summary>
    /// Raw density map without any post-processing filters applied.
    /// </summary>
    [EnumMember(Value = "Unfiltered map")]
    MapUnfiltered,

    /// <summary>
    /// Map after applying noise reduction algorithms to improve signal-to-noise ratio.
    /// </summary>
    [EnumMember(Value = "Denoised map")]
    MapDenoised,

    /// <summary>
    /// Map after B-factor sharpening to enhance high-resolution features.
    /// </summary>
    [EnumMember(Value = "Sharpened map")]
    MapSharpened,

    /// <summary>
    /// Volume containing local resolution estimates at each voxel position.
    /// </summary>
    [EnumMember(Value = "Local resolution values")]
    LocalResolution
}