namespace Refund.Jobs.Fs.Extraction.ExtractParticles2D;

public class ExtractParticles2DFiles
{
    public static string MatchingDirectory = "matching";
    public static string ParticlesDirectory = "particles";
    
    
    public static string LogDirectory = "logs";
    public static string AverageDirectory = "average";
    public static string ThumbnailsDirectory = "thumbnails";
    public static string ProcessedItemsJsonFile = "processed_items.json";
    public static string RunOutFile = "run.out";
    public static string ParticleStarFile = "particles.star";

    public static string[] FileStems;
    public static string[] XmlFiles;
    public static string[] LogFiles;
    public static string[] AverageFiles;
    public static string[] ThumbnailFiles;
    public static string[] MatchingStarFiles;
    public static string[] ParticleMrcsFiles;

    public static int[] IndexMap;
    
    public static string[] GetOutputFilesForImage(int idx) => new string[]
    {
        XmlFiles[IndexMap[idx]],
        LogFiles[IndexMap[idx]],
        AverageFiles[IndexMap[idx]],
        ThumbnailFiles[IndexMap[idx]],
        MatchingStarFiles[IndexMap[idx]],
        ParticleMrcsFiles[IndexMap[idx]],
    };
}