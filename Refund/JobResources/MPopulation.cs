using System.Collections.ObjectModel;
using Refund.DataModel;

namespace Refund.JobResources;

public class MPopulation : Resource
{
    public string Name;
    
    public string DirectoryPath;
    
    public List<MDataSource> DataSources;
    
    public List<MSpecies> Species;
    
    public string CanonicalPath => ToCanonicalPath(Name, DirectoryPath);
    
    public static string ToCanonicalPath(string populationName, string directoryPath)
    {
        return Path.Combine(directoryPath, $"{populationName}.population");
    }
    
    public string SpeciesDirectoryPath => Path.Combine(DirectoryPath, "species");
    public string DataSourcesDirectoryPath => Path.Combine(DirectoryPath, "sources");

    public MPopulation(List<MDataSource> dataSources, List<MSpecies> species)
    {
        DataSources = (dataSources ?? []).ToList();
        Species = (species ?? []).ToList();
    }
    
    public void MoveTo(string newDirectoryPath)
    {
        DirectoryPath = newDirectoryPath;
        foreach (var source in DataSources)
            source.PopulationDirectoryPath = newDirectoryPath;
        foreach (var species in Species)
            species.PopulationDirectoryPath = newDirectoryPath;
    }

    public override IEnumerable<Downloadable> GetDownloadables()
    {
        var result = new List<Downloadable>();
        
        foreach (var species in Species)
        {
            result.Add(new Downloadable(
                name: $"{species.Name} – Denoised Map",
                description: $"Denoised map for species {species.Name}",
                serverPath: species.DenoisedMapPath));
            result.Add(new Downloadable(
                name: $"{species.Name} – Sharpened Map",
                description: $"Sharpened map for species {species.Name}",
                serverPath: species.SharpenedMapPath));
            result.Add(new Downloadable(
                name: $"{species.Name} – Filtered Map",
                description: $"Filtered map for species {species.Name}",
                serverPath: species.FilteredMapPath));
            result.Add(new Downloadable(
                name: $"{species.Name} – Half Map 1",
                description: $"Half map 1 for species {species.Name}",
                serverPath: species.HalfMap1Path));
            result.Add(new Downloadable(
                name: $"{species.Name} – Half Map 2",
                description: $"Half map 2 for species {species.Name}",
                serverPath: species.HalfMap2Path));
            result.Add(new Downloadable(
                name: $"{species.Name} – Mask",
                description: $"Mask for species {species.Name}",
                serverPath: species.MaskPath));
            result.Add(new Downloadable(
                name: $"{species.Name} – FSC",
                description: $"FSC STAR file for species {species.Name}",
                serverPath: species.FscStarPath));
            result.Add(new Downloadable(
                name: $"{species.Name} – Particles",
                description: $"Particles STAR file for species {species.Name}",
                serverPath: species.ParticlesStarPath));
        }

        return result;
    }
}