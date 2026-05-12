namespace Refund.Jobs.Preprocessing.MotionAndCTF2D;

public class MotionAndCTF2DFiles
{
    public string LogDirectory = "logs";
    public string PowerSpectrumDirectory = "powerspectrum";
    public string AverageDirectory = "average";
    public string ThumbnailsDirectory = "thumbnails";
    public string SettingsFile = "align_and_ctf_frameseries.settings";
    public string ProcessedItemsJsonFile = "processed_items.json";
    public string RunOutFile = "run.out";

    public string[] FileStems;
    public string[] XmlFiles;
    public string[] LogFiles;
    public string[] AverageFiles;
    public string[] MotionTrackFiles;
    public string[] PowerSpectrumFiles;
    public string[] ThumbnailFiles;

    public int[] IndexMap;

    public Dictionary<string, string> GetOutputFilesForImage(int idx) =>
        new()
        {
            { nameof(XmlFiles), XmlFiles[IndexMap[idx]] },
            { nameof(LogFiles), LogFiles[IndexMap[idx]] },
            { nameof(PowerSpectrumFiles), PowerSpectrumFiles[IndexMap[idx]] },
            { nameof(AverageFiles), AverageFiles[IndexMap[idx]] },
            { nameof(MotionTrackFiles), MotionTrackFiles[IndexMap[idx]] },
            { nameof(ThumbnailFiles), ThumbnailFiles[IndexMap[idx]] }
        };
}