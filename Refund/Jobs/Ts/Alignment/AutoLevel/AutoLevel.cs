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

namespace Refund.Jobs.Ts.Alignment.AutoLevel;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class AutoLevel : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "79885498-1325-4aac-8ffe-2066f7cbdac0";

    public override string TypeCategory => "Tilt-series.Alignment.Auto-level";

    public override string TypeName => "Auto-level";

    public override string TypeNameShort => "Auto-level";

    public override string TypeDescription => "Estimates sample inclination around the X and Y axes to level out the tomogram.";

    public override Type ExpandedViewType => null;

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => IsPooled ? base.CoreCount : (NGpus * PerDevice) * 4;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInDataSetTs = "TiltSeries";
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
    [UiInt("patch_size", "Patch size", min: 80, max: Int32.MaxValue, stepSize: 10,
           unit: "Å",
           "Size of the patches the images will be divided into for processing")]
    public int PatchSize { get; set; } = 500;
    
    #endregion

    public AutoLevel()
    {
        var portInDataSetTs = new PortIn(this, typeof(TiltSeriesSet), PortInDataSetTs, "Aligned tilt-series", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = portInDataSetTs
        });

        var portOutDataSetTs = new PortOut(this, typeof(TiltSeriesSet), PortOutDataSetTs, "Leveled-out tilt-series", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutDataSetTs] = portOutDataSetTs
        });
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        
        if (!PortsIn[PortInDataSetTs].IsConnected)
            return null;

        var resource = PortsIn[PortInDataSetTs].GetSingleResource<TiltSeriesSet>();

        if (resource == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        resource.DataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");

        resource.HasMetadata = true;
        resource.LatestMetadataDirectory = DirectoryPath;

        resource.ProcessedItemsJson = ResProcessedItemsJson;
        resource.FailedItemsJson = ResFailedItemsJson;

        return resource;
    }

    /// <summary>
    /// Gets the name of the Warp command used for tilt series import.
    /// </summary>
    public override string CommandName => "WarpTools ts_autolevel";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        var tiltSeriesSet = PortsIn[PortInDataSetTs].GetSingleResource<TiltSeriesSet>();

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt-series input must have metadata.");

        Directory.CreateDirectory(DirectoryPath);
        
        foreach (var file in Directory.EnumerateFiles(tiltSeriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);

        var optionsWarp = tiltSeriesSet.DataSet.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;

        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }
}