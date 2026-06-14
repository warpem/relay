using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Selection.DeselectTilts;

[GenerateReadOnly]
public class DeselectTilts : WarpJob, ILocalJob
{
    public override string TypeGuid => "97d2bfde-f719-47fe-96a4-525b3bf54249";

    public override string TypeCategory => "Tilt-series.Selection.Deselect tilts";

    public override string TypeName => "Deselect tilts";

    public override string TypeNameShort => "DeselectTilts";

    public override string TypeDescription => "Deselects individual tilts or entire tilt series";

    public override JobQueueType QueueType => JobQueueType.CPU;

    public override Type ExpandedViewType => typeof(DeselectTiltsExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    /// <summary>
    /// Collection of deselected tilt series by name.
    /// </summary>
    [RelayProperty]
    public HashSet<string> DeselectedTiltSeries { get; set; } = new();

    /// <summary>
    /// Collection of deselected individual tilts by name.
    /// </summary>
    [RelayProperty]
    public HashSet<string> DeselectedTilts { get; set; } = new();

    /// <summary>
    /// Keeping track of further deselected tilt series if the user wants to export them.
    /// </summary>
    [RelayProperty]
    public HashSet<string> FurtherDeselectedTiltSeries { get; set; } = new();

    /// <summary>
    /// Keeping track of further deselected tilts if the user wants to export them.
    /// </summary>
    [RelayProperty]
    public HashSet<string> FurtherDeselectedTilts { get; set; } = new();
    
    public const string PortInDataSetTs = "DataSetTs";
    public const string PortOutDataSetTs = "DataSetTs";

    public DeselectTilts()
    {
        var portInDataSetTs = new PortIn(this, typeof(DataSetTs), PortInDataSetTs, "Tilt-series data set", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = portInDataSetTs
        });

        var portOutDataSetTs = new PortOut(this, typeof(DataSetTs), PortOutDataSetTs, "Tilt-series data set", GetDataSetResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutDataSetTs] = portOutDataSetTs
        });
    }

    private DataSetTs GetDataSetResource(int iter)
    {
        if (!PortsIn[PortInDataSetTs].IsConnected)
            return null;

        var dataSet = PortsIn[PortInDataSetTs].GetSingleResource<DataSetTs>();

        if (dataSet == null)
            throw new InvalidOperationException("Tilt-series data set input not found.");
        
        if (dataSet.Micrographs == null)
            throw new InvalidOperationException("Tilt-series data set must include micrographs.");

        dataSet.DataDirectory = DirectoryPath;

        return dataSet;
    }

    public void RunLocal(CancellationToken ct)
    {
        var previousDataSet = PortsIn[PortInDataSetTs].GetSingleResource<DataSetTs>();

        if (previousDataSet == null)
            throw new InvalidOperationException("Tilt-series data set input not found.");
        
        if (previousDataSet.Micrographs == null)
            throw new InvalidOperationException("Tilt-series data set must include micrographs.");
        
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            (logger as StreamWriter).AutoFlush = true;

            var processedItems = new List<TiltSeries>();

            foreach (var pathIn in Directory.EnumerateFiles(previousDataSet.DataDirectory, "*.tomostar"))
            {
                var name = WarpHelper.PathToNameWithExtension(pathIn);
                var pathOut = WarpHelper.PathCombine(DirectoryPath, name);
                
                if (DeselectedTiltSeries.Contains(WarpHelper.PathToName(pathIn)))
                {
                    logger.WriteLine($"Deselecting entire tilt series: {name}");
                    continue;
                }
                
                List<int> deselectedRows = new();
                var tableIn = new Star(pathIn);
                var columnMovies = tableIn.GetColumn("wrpMovieName");
                
                for (int r = 0; r < tableIn.RowCount; r++)
                    if (DeselectedTilts.Contains(WarpHelper.PathToName(columnMovies[r])))
                        deselectedRows.Add(r);
                
                if (deselectedRows.Count > 0)
                {
                    logger.WriteLine($"Deselecting {deselectedRows.Count} tilts from {name}: {string.Join(", ", deselectedRows.Select(r => r + 1))}");
                    tableIn.RemoveRows(deselectedRows.ToArray());
                    tableIn.Save(pathOut);
                }
                else
                {
                    logger.WriteLine($"Leaving {name} without changes");
                    File.Copy(pathIn, pathOut);
                }

                processedItems.Add(new TiltSeries(pathOut));
                JsonArray itemsJson = new JsonArray(processedItems.Select(series => series.ToMiniJson(""))
                                                                  .ToArray());
                File.WriteAllText(ResProcessedItemsJson,
                                  itemsJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                if (ct.IsCancellationRequested)
                {
                    logger.WriteLine("Operation cancelled by user");
                    break;
                }
            }
        }
    }

    public override Action TrackProgressLogs()
    {
        if (LogsAvailableIteration < 0)
            return () => LogsAvailableIteration = 0;
        
        return null;
    }

    public override Action TrackProgressResults()
    {
        if (VisAvailableIteration < 0)
            return () => VisAvailableIteration = 0;
        
        return null;
    }
}