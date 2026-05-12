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

namespace Refund.Jobs.Ts.Alignment.StackTilts;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class StackTilts : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "9907836e-9757-4c00-9c01-59b68de62ae0";

    public override string TypeCategory => "Tilt-series.Alignment.StackTilts";

    public override string TypeName => "Create tilt stack";

    public override string TypeNameShort => "StackTilts";

    public override string TypeDescription => "Creates tilt series stacks to use for alignment with external tools.";

    protected override int DefaultMemoryPerWorker => 8;

    public override Type ExpandedViewType => typeof(StackTiltsExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => (NGpus * PerDevice) * 8;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInDataSetTs = "DataSetTs";
    public const string PortOutDataSetTs = "TiltSeries";
    
    #region Parameters

    /// <summary>
    /// Rescale tilt images to this pixel size; normally 10–15 for cryo data
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Pre-processing", 0)]
    [UiDecimal("angpix", "Pixel size", min: 1, max: 100000, stepSize: 0.1, unit: "Å",
               "Rescale tilt images to this pixel size; normally 10–15 A for cryo data")]
    public decimal AngPix { get; set; } = 10;

    /// <summary>
    /// Apply mask to each image if available; masked areas will be filled with Gaussian noise
    /// </summary>
    [RelayProperty]
    [UiBool("mask", "Apply mask",
            "Apply mask to each image if available; masked areas will be filled with Gaussian noise")]
    public bool ApplyMask { get; set; } = true;
    
    #endregion

    public StackTilts()
    {
        var portInDataSetTs = new PortIn(this, typeof(DataSetTs), PortInDataSetTs, "Tilt-series data set", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = portInDataSetTs
        });

        var portOutDataSetTs = new PortOut(this, typeof(TiltSeriesSet), PortOutDataSetTs, "Misaligned tilt-series", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutDataSetTs] = portOutDataSetTs
        });
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        if (!PortsIn[PortInDataSetTs].IsConnected)
            return null;

        var previousDataSet = PortsIn[PortInDataSetTs].GetSingleResource<DataSetTs>();
        
        previousDataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");

        if (previousDataSet == null)
            throw new InvalidOperationException("Tilt-series data set input not found.");
        
        if (previousDataSet.Micrographs == null)
            throw new InvalidOperationException("Tilt-series data set must include micrographs.");

        return new TiltSeriesSet()
        {
            DataSet = previousDataSet,
            HasMetadata = true,
            LatestMetadataDirectory = DirectoryPath,
            TiltStackDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.TiltStackDirName),
            ToTiltStackPath = (name) => WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToTiltStackPath(name)),
            ToTiltStackThumbnailPath = (tsName, fsName) => Path.Combine(DirectoryPath, TiltSeries.ToTiltStackThumbnailPath(tsName, fsName))
        };
    }

    /// <summary>
    /// Gets the name of the Warp command used for tilt series import.
    /// </summary>
    public override string CommandName => "WarpTools ts_stack";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));

        result["thumbnails"] = "";

        return result;
    }

    public override void Stage()
    {
        base.Stage();
        
        var dataSet = PortsIn[PortInDataSetTs].GetSingleResource<DataSetTs>();
        if (dataSet == null)
            throw new InvalidOperationException("Tilt-series data set input not found.");
        
        if (dataSet.Micrographs == null)
            throw new InvalidOperationException("Tilt-series data set must include micrographs.");
        
        Directory.CreateDirectory(DirectoryPath);

        var optionsWarp = dataSet.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;
        
        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }
}