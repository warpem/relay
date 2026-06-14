namespace Refund.Jobs.Fs.Picking.BoxNetInference2D;

public class BoxNetInference2DFiles
{
    public string MaskDirectory = "mask";
    public string MatchingDirectory = "matching";

    public string LogDirectory = "logs";
    public string AverageDirectory = "average";
    public string ThumbnailsDirectory = "thumbnails";
    public string ProcessedItemsJsonFile = "processed_items.json";
    public string RunOutFile = "run.out";

    public string[] FileStems;
    public string[] XmlFiles;
    public string[] LogFiles;
    public string[] AverageFiles;
    public string[] ThumbnailFiles;
    public string[] MatchingStarFiles;
    public string[] MaskFiles;

    public int[] IndexMap;

    public Dictionary<string, string> GetOutputFilesForImage(int idx) =>
        new()
        {
            { nameof(XmlFiles), XmlFiles[IndexMap[idx]] },
            { nameof(LogFiles), LogFiles[IndexMap[idx]] },
            { nameof(AverageFiles), AverageFiles[IndexMap[idx]] },
            { nameof(ThumbnailFiles), ThumbnailFiles[IndexMap[idx]] },
            { nameof(MaskFiles), MaskFiles[IndexMap[idx]] },
            { nameof(MatchingStarFiles), MatchingStarFiles[IndexMap[idx]] }
        };
}