using Refund.DataModel;

namespace Refund.JobResources;

public class MSpecies : Resource
{
    public string PopulationDirectoryPath { get; set; }
    public string Name { get; set; }
    
    public string CanonicalPath =>  ToCanonicalPath(Name, PopulationDirectoryPath);
    
    public string CanonicalDirectoryPath => ToCanonicalDirectoryPath(Name, PopulationDirectoryPath);
    
    public static string ToCanonicalPath(string speciesName, string directoryPath)
    {
        return Path.Combine(ToCanonicalDirectoryPath(speciesName, directoryPath), $"{speciesName}.species");
    }
    
    public static string ToCanonicalDirectoryPath(string speciesName, string directoryPath)
    {
        return Path.Combine(directoryPath, "species", speciesName);
    }
    
    public string HalfMap1Path => Path.Combine(CanonicalDirectoryPath, $"{Name}_half1.mrc");
    
    public string HalfMap2Path => Path.Combine(CanonicalDirectoryPath, $"{Name}_half2.mrc");
    
    public string MaskPath => Path.Combine(CanonicalDirectoryPath, $"{Name}_mask.mrc");
    
    public string FilteredMapPath => Path.Combine(CanonicalDirectoryPath, $"{Name}_filt.mrc");
    
    public string SharpenedMapPath => Path.Combine(CanonicalDirectoryPath, $"{Name}_filtsharp.mrc");
    
    public string DenoisedMapPath => Path.Combine(CanonicalDirectoryPath, $"{Name}_denoised.mrc");
    
    public string FscStarPath => Path.Combine(CanonicalDirectoryPath, $"{Name}_fsc.star");
    
    public string ParticlesStarPath => Path.Combine(CanonicalDirectoryPath, $"{Name}_particles.star");
}