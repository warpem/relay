using Refund.DataModel;

namespace Refund.JobResources;

public class TiltSeriesSet : Resource
{
    public DataSetTs DataSet { get; set; }

    public bool HasMetadata { get; set; } = false;
    public string LatestMetadataDirectory { get; set; }
    
    public string ProcessedItemsJson { get; set; }
    public string FailedItemsJson { get; set; }

    public bool HasReferenceFreeAlignments { get; set; } = false;
    public bool HasReferenceBasedRefinements { get; set; } = false;
    public bool HasCtf { get; set; } = false;

    public string TiltStackDirectory { get; set; } = "";
    public bool HasTiltStacks => !string.IsNullOrEmpty(TiltStackDirectory);
    public Func<string, string> ToTiltStackPath;

    public string TiltStackThumbnailsDirectory { get; set; } = "";
    public bool HasTiltStackThumbnails => !string.IsNullOrEmpty(TiltStackThumbnailsDirectory);
    public Func<string, string, string> ToTiltStackThumbnailPath;

    public Func<string, string> ToAngleFilePath;
    
    public string PowerSpectrumDirectory { get; set; } = "";
    public bool HasPowerSpectra => !string.IsNullOrEmpty(PowerSpectrumDirectory);
    public Func<string, string> ToPowerSpectrumPath;

    public string ReconstructionDirectory { get; set; } = "";
    public bool HasReconstructions => !string.IsNullOrEmpty(ReconstructionDirectory);
    public Func<string, decimal, string> ToReconstructionPath;
    
    public string ReconstructionDeconvDirectory { get; set; } = "";
    public bool HasDeconvReconstructions => !string.IsNullOrEmpty(ReconstructionDeconvDirectory);
    public Func<string, decimal, string> ToReconstructionDeconvPath;
    
    public string ReconstructionOddDirectory { get; set; } = "";
    public bool HasOddReconstructions => !string.IsNullOrEmpty(ReconstructionOddDirectory);
    public Func<string, decimal, string> ToReconstructionOddPath;
    
    public string ReconstructionEvenDirectory { get; set; } = "";
    public bool HasEvenReconstructions => !string.IsNullOrEmpty(ReconstructionEvenDirectory);
    public Func<string, decimal, string> ToReconstructionEvenPath;
    
    public string SubtomoDirectory { get; set; } = "";
    public bool HasSubtomos => !string.IsNullOrEmpty(SubtomoDirectory);
    public Func<string, decimal, string> ToSubtomoPath;
    
    public string ParticleSeriesDirectory { get; set; } = "";
    public bool HasParticleSeries => !string.IsNullOrEmpty(ParticleSeriesDirectory);
    public Func<string, decimal, string> ToParticleSeriesPath;
}