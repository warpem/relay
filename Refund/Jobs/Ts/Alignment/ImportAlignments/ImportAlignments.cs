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

namespace Refund.Jobs.Ts.Alignment.ImportAlignments;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class ImportAlignments : WarpJob, IClusterJob
{
    public override string TypeGuid => "fe02ae31-db05-4928-9196-87eacb6601b6";

    public override string TypeCategory => "Tilt-series.Alignment.ImportAlignments";

    public override string TypeName => "Import alignments";

    public override string TypeNameShort => "ImportAlignments";

    public override string TypeDescription => "Imports etomo-style alignment files from an external source.";

    public override JobQueueType QueueType => JobQueueType.CPU;
    
    public override Type ExpandedViewType => typeof(ImportAlignmentsExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => 8;

    public override int MemoryGb => 8;

    public override string[] SupportedModules => base.SupportedModules.Concat(["cpu"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["cpu"]).ToArray();

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTs = "TiltSeries";
    public const string PortOutTs = "TiltSeries";
    
    #region Parameters
    
    [RelayProperty]
    [UiFieldGroup("Alignments", 0)]
    [UiPath("alignments", "Alignments directory", SelectionMode.SingleFolder,
            helpText: "Path to a folder containing one sub-folder per tilt-series " +
                      "with etomo-style alignment results")]
    public string AlignmentsDir { get; set; }

    /// <summary>
    /// Rescale tilt images to this pixel size; normally 10–15 for cryo data
    /// </summary>
    [RelayProperty]
    [UiDecimal("alignment_angpix", "Pixel size", min: 1, max: 100000, stepSize: 0.001, unit: "Å",
               "Pixel size (in Angstrom) of the images used to create the alignments " +
               "(used to convert the alignment shifts from pixels to Angstrom)")]
    public decimal AngPix { get; set; } = 10;

    /// <summary>
    /// Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts
    /// </summary>
    [RelayProperty]
    [UiDecimal("min_fov", "Minimum FOV fraction", 0, 1, 0.01,
               helpText: "Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts")]
    public decimal MinFov { get; set; } = 0;
    
    #endregion

    public ImportAlignments()
    {
        var portInDataSetTs = new PortIn(this, typeof(TiltSeriesSet), PortInTs, "Misaligned tilt-series", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTs] = portInDataSetTs
        });

        var portOutDataSetTs = new PortOut(this, typeof(TiltSeriesSet), PortOutTs, "Aligned tilt-series", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTs] = portOutDataSetTs
        });
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        if (!PortsIn[PortInTs].IsConnected)
            return null;

        var result = PortsIn[PortInTs].GetSingleResource<TiltSeriesSet>();
        
        result.DataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");
        
        if (result == null)
            throw new InvalidOperationException("Tilt-series input not found.");

        result.HasMetadata = true;
        result.LatestMetadataDirectory = DirectoryPath;

        return result;
    }

    /// <summary>
    /// Gets the name and configuration of the Warp command used for tilt series import.
    /// </summary>
    public override string CommandName => "WarpTools ts_import_alignments";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));

        return result;
    }

    public override void Stage()
    {
        base.Stage();
        
        var tsSet = PortsIn[PortInTs].GetSingleResource<TiltSeriesSet>();
        if (tsSet == null)
            throw new InvalidOperationException("Tilt-series set input not found.");
        
        Directory.CreateDirectory(DirectoryPath);
        
        foreach (var file in Directory.EnumerateFiles(tsSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);

        var optionsWarp = tsSet.DataSet.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;
        
        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }
}