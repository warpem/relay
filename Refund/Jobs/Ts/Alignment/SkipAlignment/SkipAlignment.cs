using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Ts.Alignment.SkipAlignment;

/// <summary>
/// Creates alignment metadata for pre-aligned or jitter-free tilt images.
/// </summary>
[GenerateReadOnly]
public class SkipAlignment : LocalJob, ILocalJob
{
    public override string TypeGuid => "629f8a8b-0138-4b3e-8c13-908b31b7d0fb";
    public override string TypeCategory => "Tilt-series.Alignment.Skip alignment";
    public override string TypeName => "Skip alignment";
    public override string TypeNameShort => "Skip alignment";
    public override string TypeDescription => "Creates XML metadata with zero shifts and a fixed tilt axis angle for pre-aligned or jitter-free tilt images.";

    public override JobQueueType QueueType => JobQueueType.Local;
    public override int GpuCount => 0;
    public override bool IsIterative => false;
    public override Type ExpandedViewType => null;
    public override Type CardViewType => null;
    public override int2 CardSquareCount { get; set; } = new(2, 1);

    public const string PortInDataSetTs = "DataSetTs";
    public const string PortOutTs = "TiltSeries";

    [RelayProperty]
    [UiFieldGroup("Alignment", 0)]
    [UiDecimal("axis", "Tilt axis angle", -360, 360, 0.001, "°",
               "Tilt axis angle to write for every tilt. All alignment shifts are set to zero.")]
    public decimal TiltAxisAngle { get; set; } = 0.0m;

    public string ResProcessedItemsJson => Path.Combine(DirectoryPath, "processed_items.json");

    public SkipAlignment()
    {
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = new(this, typeof(DataSetTs), PortInDataSetTs, "Tilt-series data set", 1, 1)
        });
        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTs] = new(this, typeof(TiltSeriesSet), PortOutTs, "Aligned tilt-series", GetTiltSeriesResource)
        });
    }

    private DataSetTs GetInputDataSet()
    {
        var dataSet = PortsIn[PortInDataSetTs].GetSingleResource<DataSetTs>();
        if (dataSet == null)
            throw new InvalidOperationException("Tilt-series data set input not found.");
        if (dataSet.Micrographs == null)
            throw new InvalidOperationException("Tilt-series data set must include micrographs.");
        return dataSet;
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        if (!PortsIn[PortInDataSetTs].IsConnected)
            return null;

        var dataSet = GetInputDataSet();
        dataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");

        return new TiltSeriesSet
        {
            ItemCount = dataSet.ItemCount,
            DataSet = dataSet,
            HasMetadata = true,
            LatestMetadataDirectory = DirectoryPath,
            ProcessedItemsJson = ResProcessedItemsJson
        };
    }

    public void RunLocal(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dataSet = GetInputDataSet();
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        var options = dataSet.ToOptionsWarp();
        options.Import.ProcessingFolder = DirectoryPath;
        options.Save(Path.Combine(DirectoryPath, "processing.settings"));

        using var logger = File.CreateText(LogFilePath(0));
        logger.AutoFlush = true;
        var processedItems = new JsonArray();

        foreach (var path in Directory.EnumerateFiles(dataSet.DataDirectory, "*.tomostar")
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var outputPath = Path.Combine(DirectoryPath, Path.GetFileName(path));

            // Start from the TOMOSTAR definition, including on reruns. Loading old XML
            // could retain local warps, tilt exclusions, or other alignment corrections.
            File.Delete(Path.ChangeExtension(outputPath, ".xml"));
            var series = new TiltSeries(outputPath, dataSet.DataDirectory);
            series.TiltAxisAngles = Enumerable.Repeat((float)TiltAxisAngle, series.NTilts).ToArray();
            series.TiltAxisOffsetX = new float[series.NTilts];
            series.TiltAxisOffsetY = new float[series.NTilts];
            series.FOVFraction = Enumerable.Repeat(1f, series.NTilts).ToArray();
            SaveMetadata(series);

            processedItems.Add(series.ToMiniJson());
            logger.WriteLine($"Created zero-shift metadata for {series.Name}");
        }

        token.ThrowIfCancellationRequested();
        File.WriteAllText(ResProcessedItemsJson,
            processedItems.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        logger.WriteLine($"Done: {processedItems.Count} tilt series");
    }

    private static void SaveMetadata(TiltSeries series)
    {
        // Warp's per-tilt XML values use the current culture when saving, but the
        // reader expects invariant numbers. Keep the generated XML portable.
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            series.SaveMeta();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
