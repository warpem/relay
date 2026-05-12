using Refund.DataModel;

namespace Refund.JobResources;

/// <summary>
/// Represents a collection of extracted particles from electron micrographs or tomograms,
/// including metadata about their positions, orientations, and properties. Particle sets
/// are used as input for 2D classification, 3D classification, and 3D refinement procedures.
/// </summary>
public class ParticleSet : Resource, ICountableResource
{
    /// <summary>
    /// Indicates whether image or volume data exist for this particle set, i.e. if it's not just a set of positions.
    /// </summary>
    public bool HasData { get; set; } = false;
    
    /// <summary>
    /// Path to a STAR file containing particle metadata such as positions, orientations, and CTF parameters.
    /// </summary>
    public string ParticlesSingleStarPath { get; set; } = "";
    public bool HasSingleStar => !string.IsNullOrEmpty(ParticlesSingleStarPath);
    
    /// <summary>
    /// Directory containing multiple STAR files, each with coordinates for a single micrograph or tomogram.
    /// </summary>
    public string ParticlesMultiStarDirectory { get; set; } = "";
    public bool HasMultiStar => !string.IsNullOrEmpty(ParticlesMultiStarDirectory);

    /// <summary>
    /// Filename pattern for matching STAR files in the MultiStarDirectory.
    /// For example, "*.coords.star" would match all files ending with ".coords.star".
    /// </summary>
    public Func<string, string> ToMultiStarPath;
    
    /// <summary>
    /// Indicates whether this particle set is a single STAR file or multiple per-micrograph/tomogram STAR files
    /// </summary>
    public bool IsSingleStar => !string.IsNullOrEmpty(ParticlesSingleStarPath);
    
    /// <summary>
    /// Path to the tomograms STAR file containing metadata about the tomograms.
    /// </summary>
    public string TomogramsStarPath { get; set; }
    
    /// <summary>
    /// Path to the optimisation set STAR file that links particles and tomograms.
    /// </summary>
    public string OptimisationSetStarPath { get; set; }

    /// <summary>
    /// The dimensionality of the particle data (images, volumes, tilt series).
    /// </summary>
    public ParticleType DataDimensionality { get; set; } = ParticleType.Image;
    
    /// <summary>
    /// Indicates whether the particles come from micrographs (images), or tilt series (tomograms, tilt series)
    /// </summary>
    public bool IsTomo => DataDimensionality == ParticleType.Tomogram || DataDimensionality == ParticleType.Tiltseries;
    
    /// <summary>
    /// Indicates whether the coordinates have a Z component, needed for tomography workflows.
    /// </summary>
    public bool Has3dCoords { get; set; } = false;
    
    /// <summary>
    /// Indicates whether the coordinates are normalized to a range of [0, 1].
    /// </summary>
    public bool HasNormalizedCoords { get; set; } = false;
    
    /// <summary>
    /// If not normalized and not in physical units, this value indicates the size of a single pixel in Angstroms.
    /// </summary>
    public decimal CoordPixelSize { get; set; } = 1M;
    
    /// <summary>
    /// Indicates whether the particle coordinate origin is at the image/volume center, or in the corner.
    /// </summary>
    public bool HasCenteredCoords { get; set; } = false;
    
    /// <summary>
    /// Indicates whether this particle set includes class assignments (e.g., from 2D/3D classification).
    /// </summary>
    public bool HasClasses { get; set; }
    
    /// <summary>
    /// Indicates whether this particle set includes translational shift parameters.
    /// </summary>
    public bool HasShifts { get; set; }
    
    /// <summary>
    /// Indicates whether this particle set includes positional information (coordinates in micrographs).
    /// </summary>
    public bool HasPositions { get; set; }
    
    /// <summary>
    /// Indicates whether this particle set includes orientation angles (Euler angles).
    /// </summary>
    public bool HasAngles { get; set; }
    
    /// <summary>
    /// Indicates whether this particle set includes intensity scale factors.
    /// </summary>
    public bool HasScale { get; set; }
    
    /// <summary>
    /// Indicates whether this particle set includes CTF (Contrast Transfer Function) parameters.
    /// </summary>
    public bool HasCtf { get; set; }
    
    public int ParticleCount { get; set; }

    public int Count => ParticleCount;
    
    public int Diameter { get; set; } = 0;
    
    /// <summary>
    /// Set of tomograms in which these particles were picked.
    /// </summary>
    public TomogramSet PickedInTomograms { get; set; }
    
    /// <summary>
    /// Set of micrographs in which these particles were picked.
    /// </summary>
    public MicrographSet PickedInMicrographs { get; set; }
    
    /// <summary>
    /// Set of 3D maps corresponding to this particle set.
    /// Useful for displaying the particles in context, e.g. inside a tomogram
    /// </summary>
    public MapList CorrespondingMaps { get; set; }

    /// <summary>
    /// Returns a collection of downloadable resources associated with this particle set.
    /// </summary>
    /// <returns>Collection of resources that can be downloaded by the user</returns>
    public override IEnumerable<Downloadable> GetDownloadables()
    {
        List<Downloadable> result = new();
        
        if (!string.IsNullOrWhiteSpace(ParticlesSingleStarPath))
            result.Add(new Downloadable("Particles STAR file", "", ParticlesSingleStarPath));
            
        if (!string.IsNullOrWhiteSpace(TomogramsStarPath))
            result.Add(new Downloadable("Tomograms STAR file", "", TomogramsStarPath));
            
        if (!string.IsNullOrWhiteSpace(OptimisationSetStarPath))
            result.Add(new Downloadable("Optimisation Set STAR file", "", OptimisationSetStarPath));

        return result;
    }
}

public enum ParticleType
{
    /// <summary>
    /// Each particle is a 2D image (e.g., from micrographs).
    /// </summary>
    Image = 2,
    
    /// <summary>
    /// Each particle is a 3D volume (e.g., from tomograms).
    /// </summary>
    Tomogram = 3,
    
    /// <summary>
    /// Each particle is a tilt series (set of 2D images at different angles).
    /// </summary>
    Tiltseries = 4
}