using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Utils;
using Serilog;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.SelectTomograms;

[GenerateReadOnly]
public class SelectTomograms : WarpJob, ILocalJob
{
    public override string TypeGuid => "a3f8c1d2-7e5b-4a9f-b6c3-2d1e0f9a8b7c";

    public override string TypeCategory => "Tilt-series.Selection.Select tomograms";

    public override string TypeName => "Tomogram selection";

    public override string TypeNameShort => "Select Tomograms";

    public override string TypeDescription => "Visually select or deselect tomograms from a reconstruction";

    public override JobQueueType QueueType => JobQueueType.Local;

    public override Type ExpandedViewType => typeof(SelectTomogramsExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override bool IsInteractive => true;

    public const string PortInTomogramSet = "Tomograms";
    public const string PortOutTomogramSet = "Tomograms";
    public const string PortOutTiltSeriesSet = "TiltSeries";

    /// <summary>
    /// Names of tomograms that have been deselected by the user.
    /// Empty means all are selected.
    /// </summary>
    [RelayProperty]
    [Clearable]
    public HashSet<string> DeselectedTomograms { get; set; } = new();

    private bool _isFinalized;

    public SelectTomograms()
    {
        var portInTomogramSet = new PortIn(this, typeof(TomogramSet), PortInTomogramSet, "Tomograms", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTomogramSet] = portInTomogramSet
        });

        var portOutTomogramSet = new PortOut(this, typeof(TomogramSet), PortOutTomogramSet, "Tomograms", GetTomogramSetResource);
        var portOutTiltSeriesSet = new PortOut(this, typeof(TiltSeriesSet), PortOutTiltSeriesSet, "Tilt series", GetTiltSeriesSetResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTomogramSet] = portOutTomogramSet,
            [PortOutTiltSeriesSet] = portOutTiltSeriesSet
        });
    }

    public override void Stage()
    {
        base.Stage();

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");

        Directory.CreateDirectory(DirectoryPath);

        File.Copy(tomogramSet.ProcessedItemsJson, ResProcessedItemsJson);
        if (File.Exists(tomogramSet.FailedItemsJson))
            File.Copy(tomogramSet.FailedItemsJson, ResFailedItemsJson);
    }

    public void RunLocal(CancellationToken token)
    {
        while (!IsInteractiveFinished && !token.IsCancellationRequested)
            Thread.Sleep(100);

        if (token.IsCancellationRequested)
            return;

        try
        {
            FinalizeSelection();
            _isFinalized = true;
        }
        catch (Exception ex)
        {
            Log.ForContext<SelectTomograms>().Error(ex, "Error finalizing tomogram selection");
            throw;
        }
    }

    private void WriteLog(string message)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        File.AppendAllText(LogFilePath(0), message + Environment.NewLine);
    }

    private void FinalizeSelection()
    {
        var inputTomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        var inputTiltSeriesSet = inputTomogramSet.TiltSeriesSet;

        var json = File.ReadAllText(inputTomogramSet.ProcessedItemsJson);
        var allItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(json);

        var selectedItems = allItems.Where(item => !DeselectedTomograms.Contains(WarpHelper.PathToName(item.Path))).ToList();

        WriteLog($"Selected {selectedItems.Count} out of {allItems.Count} tomograms");

        // Create output subdirectories
        string reconstructionDir = Path.Combine(DirectoryPath, TiltSeries.ReconstructionDirName);
        Directory.CreateDirectory(reconstructionDir);

        if (inputTomogramSet.HasDeconvTomograms)
            Directory.CreateDirectory(Path.Combine(DirectoryPath, TiltSeries.ReconstructionDeconvDirName));

        if (inputTomogramSet.HasHalfMap1Tomograms)
            Directory.CreateDirectory(Path.Combine(DirectoryPath, TiltSeries.ReconstructionOddDirName));

        if (inputTomogramSet.HasHalfMap2Tomograms)
            Directory.CreateDirectory(Path.Combine(DirectoryPath, TiltSeries.ReconstructionEvenDirName));

        if (inputTomogramSet.HasDenoisedTomograms)
            Directory.CreateDirectory(Path.Combine(DirectoryPath, TiltSeries.ReconstructionDenoisedDirName));

        var processedItems = new List<TiltSeries>();

        var pixelSize = inputTomogramSet.PixelSize;

        foreach (var item in selectedItems)
        {
            string name = item.Path;

            // Symlink reconstruction tomogram
            CreateSymlinkIfExists(
                inputTomogramSet.ToTomogramPath(name),
                WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToReconstructionTomogramPath(name, pixelSize)));

            // Symlink deconvolved tomogram
            if (inputTomogramSet.HasDeconvTomograms)
                CreateSymlinkIfExists(
                    inputTomogramSet.ToTomogramDeconvPath(name),
                    WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToReconstructionDeconvPath(name, pixelSize)));

            // Symlink half-map 1 (odd)
            if (inputTomogramSet.HasHalfMap1Tomograms)
                CreateSymlinkIfExists(
                    inputTomogramSet.ToTomogramHalfMap1Path(name),
                    WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToReconstructionOddPath(name, pixelSize)));

            // Symlink half-map 2 (even)
            if (inputTomogramSet.HasHalfMap2Tomograms)
                CreateSymlinkIfExists(
                    inputTomogramSet.ToTomogramHalfMap2Path(name),
                    WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToReconstructionEvenPath(name, pixelSize)));

            // Symlink denoised tomogram
            if (inputTomogramSet.HasDenoisedTomograms)
                CreateSymlinkIfExists(
                    inputTomogramSet.ToTomogramDenoisedPath(name),
                    WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToReconstructionDenoisedTomogramPath(name, pixelSize)));

            // Copy .tomostar file to job root (tomostar files contain relative paths that assume this)
            string baseName = WarpHelper.PathToName(name);
            string tomostarSource = Path.Combine(inputTiltSeriesSet.DataSet.DataDirectory, WarpHelper.PathToNameWithExtension(name));
            string tomostarDest = Path.Combine(DirectoryPath, WarpHelper.PathToNameWithExtension(name));
            if (File.Exists(tomostarSource))
            {
                File.Copy(tomostarSource, tomostarDest, true);
                WriteLog($"  {baseName}: copied .tomostar, symlinked tomogram");
            }
            else
            {
                WriteLog($"  {baseName}: .tomostar not found at {tomostarSource}");
            }

            // Copy .xml metadata file from the latest metadata directory
            string xmlSource = Path.Combine(inputTiltSeriesSet.LatestMetadataDirectory, baseName + ".xml");
            string xmlDest = Path.Combine(DirectoryPath, baseName + ".xml");
            if (File.Exists(xmlSource))
                File.Copy(xmlSource, xmlDest, true);

            // Build processed items JSON incrementally
            processedItems.Add(new TiltSeries(tomostarDest));
            JsonArray itemsJson = new JsonArray(processedItems.Select(series => series.ToMiniJson("")).ToArray());
            File.WriteAllText(ResProcessedItemsJson,
                              itemsJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        WriteLog($"Finalized: {selectedItems.Count} tomograms selected, {DeselectedTomograms.Count} deselected");
    }

    private static void CreateSymlinkIfExists(string sourcePath, string linkPath)
    {
        if (File.Exists(sourcePath) && !File.Exists(linkPath))
            File.CreateSymbolicLink(linkPath, Path.GetFullPath(sourcePath));
    }

    private TomogramSet GetTomogramSetResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");

        // Point the boxed TiltSeriesSet to our filtered tomostar folder
        tomogramSet.TiltSeriesSet = GetTiltSeriesSetResource(iter);

        // Update processed items to our filtered list
        tomogramSet.ProcessedItemsJson = ResProcessedItemsJson;
        tomogramSet.FailedItemsJson = ResFailedItemsJson;

        var pixelSize = tomogramSet.PixelSize;

        // Redirect tomogram directories to our symlink folders using standard naming
        tomogramSet.TomogramDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionDirName);
        tomogramSet.ToTomogramPath = name => WarpHelper.PathCombine(DirectoryPath,
            TiltSeries.ToReconstructionTomogramPath(name, pixelSize));

        if (tomogramSet.HasDeconvTomograms)
        {
            tomogramSet.TomogramDeconvDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionDeconvDirName);
            tomogramSet.ToTomogramDeconvPath = name => WarpHelper.PathCombine(DirectoryPath,
                TiltSeries.ToReconstructionDeconvPath(name, pixelSize));
        }

        if (tomogramSet.HasHalfMap1Tomograms)
        {
            tomogramSet.TomogramHalfMap1Directory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionOddDirName);
            tomogramSet.ToTomogramHalfMap1Path = name => WarpHelper.PathCombine(DirectoryPath,
                TiltSeries.ToReconstructionOddPath(name, pixelSize));
        }

        if (tomogramSet.HasHalfMap2Tomograms)
        {
            tomogramSet.TomogramHalfMap2Directory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionEvenDirName);
            tomogramSet.ToTomogramHalfMap2Path = name => WarpHelper.PathCombine(DirectoryPath,
                TiltSeries.ToReconstructionEvenPath(name, pixelSize));
        }

        if (tomogramSet.HasDenoisedTomograms)
        {
            tomogramSet.TomogramDenoisedDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionDenoisedDirName);
            tomogramSet.ToTomogramDenoisedPath = name => WarpHelper.PathCombine(DirectoryPath,
                TiltSeries.ToReconstructionDenoisedTomogramPath(name, pixelSize));
        }

        // Thumbnails stay where they are (not re-created)

        return tomogramSet;
    }

    private TiltSeriesSet GetTiltSeriesSetResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");

        var tiltSeriesSet = tomogramSet.TiltSeriesSet;

        // Point DataDirectory and metadata to our job root (tomostar + xml files are copied there)
        tiltSeriesSet.DataSet.DataDirectory = DirectoryPath;
        tiltSeriesSet.LatestMetadataDirectory = DirectoryPath;

        // Update processed items to our filtered list
        tiltSeriesSet.ProcessedItemsJson = ResProcessedItemsJson;
        tiltSeriesSet.FailedItemsJson = ResFailedItemsJson;

        return tiltSeriesSet;
    }

    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        if (LogsAvailableIteration < 0 && File.Exists(LogFilePath(0)))
            return () =>
            {
                baseResult?.Invoke();
                LogsAvailableIteration = 0;
            };

        return baseResult;
    }

    public override Action TrackProgressResults()
    {
        if (!_isFinalized)
            return null;

        var baseUpdate = base.TrackProgressResults();

        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)))
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            // Use input TomogramSet's thumbnail paths (they aren't re-created)
            var inputTomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

            if (processedItems.Count >= 2)
            {
                BakeryWrapper.TsReconstructJobCard(
                    inputTomogramSet.ToTomogramThumbnailPath(processedItems[0].Path),
                    inputTomogramSet.ToTomogramThumbnailPath(processedItems[1].Path),
                    VisCard(0));
            }
            else
            {
                // Only 1 selected tomogram — use same thumbnail for both slots
                BakeryWrapper.TsReconstructJobCard(
                    inputTomogramSet.ToTomogramThumbnailPath(processedItems[0].Path),
                    inputTomogramSet.ToTomogramThumbnailPath(processedItems[0].Path),
                    VisCard(0));
            }

            return () =>
            {
                baseUpdate?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return baseUpdate;
    }
}
