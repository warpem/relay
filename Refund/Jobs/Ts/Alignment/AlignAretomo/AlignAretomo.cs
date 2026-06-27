using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Ts.Alignment.AlignEtomo;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Alignment.AlignAretomo;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class AlignAretomo : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "b189fd92-ec29-489c-b70a-01f43eb62a9c";

    public override string TypeCategory => "Tilt-series.Alignment.AreTomo";

    public override string TypeName => "AreTomo 2";

    public override string TypeNameShort => "AreTomo 2";

    public override string TypeDescription => "Creates tilt series stacks and runs AreTomo 2 to obtain tilt series alignments";

    public override Type ExpandedViewType => typeof(AlignEtomoExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    protected override int DefaultMemoryPerWorker => 16;

    public override int CoreCount => IsPooled ? base.CoreCount : (NGpus) * 4;

    public override string[] SupportedModules => base.SupportedModules.Concat(["aretomo2"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["aretomo2"]).ToArray();

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
    [UiEnum("", "AreTomo version", typeof(AretomoVersion),
            helpText: "Select which version of AreTomo to use")]
    public AretomoVersion Version { get; set; } = AretomoVersion.AreTomo2;
    
    [RelayProperty]
    [UiDecimalNullable("axis", "Override initial axis angle", -360, 360, 0.001, "°",
                       "Override initial tilt axis angle with this value")]
    public decimal? InitialAxisAngle { get; set; } = null;

    /// <summary>
    /// Expected sample thickness in Å for AreTomo's algorithm
    /// </summary>
    [RelayProperty]
    [UiInt("alignz", "Expected sample thickness", 10, int.MaxValue, 10,
           unit: "Å",
           helpText: "Expected sample thickness along the Z axis for AreTomo's algorithm",
           ConditionalOnField = nameof(Version), ConditionalOnValue = AretomoVersion.AreTomo2)]
    public int AlignZ { get; set; } = 1000;

    /// <summary>
    /// Expected sample thickness in Å for AreTomo's algorithm
    /// </summary>
    [RelayProperty]
    [UiIntNullable("alignz", "Expected sample thickness", 10, int.MaxValue, 10,
                   unit: "Å",
                   helpText: "Expected sample thickness along the Z axis for AreTomo's algorithm",
                   ConditionalOnField = nameof(Version), ConditionalOnValue = AretomoVersion.AreTomo3)]
    public int? AlignZ3 { get; set; } = null;

    /// <summary>
    /// Fit a new tilt axis angle for the whole dataset
    /// </summary>
    [RelayProperty]
    [UiIntNullable("axis_iter", "Axis search iterations", 0, 99, 1,
                   helpText: "Fit a new tilt axis angle for the whole dataset")]
    public int? DoAxisAngleSearch { get; set; } = null;

    /// <summary>
    /// Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Post-processing", 2)]
    [UiDecimal("min_fov", "Minimum FOV fraction", 0, 1, 0.01,
               helpText: "Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts")]
    public decimal MinFov { get; set; } = 0;

    /// <summary>
    /// Delete tilt series stacks generated for AreTomo
    /// </summary>
    [RelayProperty]
    [UiBool("delete_intermediate", "Delete intermediate files",
            "Delete tilt series stacks generated for AreTomo afterwards")]
    public bool DeleteIntermediate { get; set; } = false;
    
    #region GPU options

    public override int PerDevice { get; set; } = 1;

    #endregion
    
    #endregion

    public AlignAretomo()
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
    public override string CommandName => $"WarpTools {(Version == AretomoVersion.AreTomo2 ? "ts_aretomo" : "ts_aretomo3")}";

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

public enum AretomoVersion
{
    AreTomo2,
    AreTomo3
}