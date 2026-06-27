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

namespace Refund.Jobs.Ts.Alignment.AlignEtomo;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class AlignEtomo : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "21478107-44d8-4a21-b9b7-acead263bed3";

    public override string TypeCategory => "Tilt-series.Alignment.Etomo patch tracking";

    public override string TypeName => "Etomo alignment";

    public override string TypeNameShort => "Etomo alignment";

    public override string TypeDescription => "Creates tilt series stacks and runs Etomo patch or fiducial tracking to obtain tilt series alignments";

    public override Type ExpandedViewType => typeof(AlignEtomoExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => IsPooled ? base.CoreCount : (NGpus * PerDevice) * 8;

    public override string[] SupportedModules => base.SupportedModules.Concat(["imod"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["imod"]).ToArray();

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

    /// <summary>
    /// Override initial tilt axis angle with this value
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Alignment", 1)]
    [UiDecimalNullable("initial_axis", "Override initial axis angle", -360, 360, 0.001, "°",
                       "Override initial tilt axis angle with this value")]
    public decimal? InitialAxisAngle { get; set; } = null;

    /// <summary>
    /// Fit a new tilt axis angle for the whole dataset
    /// </summary>
    [RelayProperty]
    [UiBool("do_axis_search", "Search for tilt axis angle",
            "Fit a new tilt axis angle for the whole dataset")]
    public bool DoAxisAngleSearch { get; set; } = false;
    
    [RelayProperty]
    [UiEnum("", "What to track", typeof(EtomoMode),
            helpText: "Select whether to use patch tracking or fiducial marker tracking")]
    public EtomoMode Mode { get; set; } = EtomoMode.Patches;

    /// <summary>
    /// Patch size for patch tracking in Angstroms
    /// </summary>
    [RelayProperty]
    [UiInt("patch_size", "Patch size", 20, 1000000, 10,
           "Patch size for patch tracking in Angstroms", Unit = "Å",
           ConditionalOnField = nameof(Mode), ConditionalOnValue = EtomoMode.Patches)]
    public int PatchSizeAngstroms { get; set; } = 500;

    [RelayProperty]
    [UiInt("fiducial_size", "Fiducial diameter", 1, 1000000, 1,
           "Fiducial diameter in nanometers", Unit = "nm",
           ConditionalOnField = nameof(Mode), ConditionalOnValue = EtomoMode.Fiducials)]
    public int FiducialSizeAngstroms { get; set; } = 10;

    [RelayProperty]
    [UiInt("n_beads_target", "Number of fiducials", 1, 1000000, 1,
           "Target number of fiducials for etomo to find per field of view",
           ConditionalOnField = nameof(Mode), ConditionalOnValue = EtomoMode.Fiducials)]
    public int BeadsTarget { get; set; } = 20;

    /// <summary>
    /// Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Post-processing", 2)]
    [UiDecimal("min_fov", "Minimum FOV fraction", 0, 1, 0.01,
               helpText: "Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts")]
    public decimal MinFov { get; set; } = 0;

    /// <summary>
    /// Delete tilt series stacks generated for Etomo
    /// </summary>
    [RelayProperty]
    [UiBool("delete_intermediate", "Delete intermediate files",
            "Delete tilt series stacks generated for Etomo afterwards")]
    public bool DeleteIntermediate { get; set; } = false;
    
    #endregion

    public AlignEtomo()
    {
        var portInDataSetTs = new PortIn(this, typeof(DataSetTs), PortInDataSetTs, "Tilt-series data set", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = portInDataSetTs
        });

        var portOutDataSetTs = new PortOut(this, typeof(TiltSeriesSet), PortOutDataSetTs, "Aligned tilt-series", GetTiltSeriesResource);

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
    public override string CommandName => $"WarpTools {(Mode == EtomoMode.Patches ? "ts_etomo_patches" : "ts_etomo_fiducials")}";

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

public enum EtomoMode
{
    Patches,
    Fiducials
}