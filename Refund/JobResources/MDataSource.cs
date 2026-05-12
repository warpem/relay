using Refund.DataModel;

namespace Refund.JobResources;

public class MDataSource : Resource
{
    public string Name;
    
    public string PopulationDirectoryPath { get; set; }

    public int NItems;
    
    public string CanonicalPath => ToCanonicalPath(Name, PopulationDirectoryPath);
    
    public static string ToCanonicalPath(string sourceName, string directoryPath)
    {
        return Path.Combine(directoryPath, "sources", sourceName, $"{sourceName}.source");
    }
}