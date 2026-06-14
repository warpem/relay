using System.Globalization;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Import.ImportDataSetTs;

[GenerateReadOnly]
public class ImportDataSetTs : WarpJob, IClusterJob
{
    public override string TypeGuid => "8275ebaf-ae30-432b-bacf-8fc4d4dab549";
    
    public override string TypeCategory => "Tilt-series.Import.Tilt series";

    public override string TypeName => "Tilt-series data set";

    public override string TypeNameShort => "DataSetTs";

    public override string TypeDescription => "Imports MDOC files containing tilt-series definitions";

    public override JobQueueType QueueType => JobQueueType.CPU;

    public override string[] RequiredModules => base.RequiredModules.Concat(["cpu"]).ToArray();

    public override int CoreCount => 16;
    public override int MemoryGb => 32;

    public override bool IsIterative => false;

    public override Type ExpandedViewType => typeof(ImportDataSetTsExpandedView);

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

    #region Parameters

    #region Data location

    [UiFieldGroup("Data location", 0)]
    [UiPath("", "MDOC directory",
            SelectionMode.SingleFolder,
            helpText: "The root directory containing (subdirectories with) MDOC files.")]
    [RelayProperty]
    public string MdocDirectory { set; get; }

    [UiString("pattern", "File search pattern",
              helpText: "The search pattern for files in the root directory (and its subdirectories).")]
    [RelayProperty]
    public string FileSearchPattern { get; set; } = "*.mdoc";

    #endregion

    #region Tomogram dimensions

    [UiFieldGroup("Tomogram", 1)]
    [UiInt3("", "Tomogram dimensions", 2, int.MaxValue, 2,
            helpText: "The dimensions of the tomogram in unbinned pixels.",
            Unit = "unbinned pixels")]
    [RelayProperty]
    public int3 TomogramDimensions { set; get; } = new int3(4096, 4096, 1000);

    #endregion

    #region Angles

    [UiFieldGroup("Conversion", 2)]
    [UiBool("dont_invert", "Invert tilt angles",
            helpText: "Whether to invert the tilt angles in the MDOC files. " +
                      "Inversion is usually needed to match IMOD's geometric handedness.",
            reverse: true)]
    [RelayProperty]
    public bool InvertTiltAngles { get; set; } = true;

    [UiDecimal("tilt_offset", "Tilt angle offset", -100, 100, 0.1,
               unit: "\u00b0",
               helpText: "Subtract this value from all tilt angle values to compensate pre-tilt.")]
    [RelayProperty]
    public decimal TiltOffset { get; set; } = 0;

    [UiBool("auto_zero", "Auto-zero overall tilt",
            helpText: "Adjust tilt angles so that the tilt with the highest average " +
                      "intensity becomes the 0-tilt")]
    [RelayProperty]
    public bool AutoZeroTilt { get; set; } = false;

    [UiDecimalNullable("override_axis", "Override tilt axis", -360, 360, 0.001, unit: "\u00b0",
                       helpText: "Override the tilt axis angle with this value.")]
    [RelayProperty]
    public decimal? OverrideTiltAxis { get; set; } = null;

    #endregion

    #region Tilt exclusion

    [UiFieldGroup("Tilt exclusion", 3)]
    [UiDecimal("max_tilt", "Maximum tilt angle", 0, 180, 0.1, unit: "\u00b0",
               helpText: "Exclude all tilts above this (absolute) tilt angle.")]
    [RelayProperty]
    public decimal MaxTilt { get; set; } = 90;

    [UiDecimal("min_intensity", "Minimum intensity", 0, 1, 0.01,
               helpText: "Exclude tilts if their average intensity is below MinIntensity * " +
                         "cos(angle) * 0-tilt intensity; set to 0 to not exclude anything.")]
    [RelayProperty]
    public decimal MinIntensity { get; set; } = 0;

    [UiInt("min_ntilts", "Minimum number of tilts", 1, int.MaxValue, 1,
           helpText: "Only import tilt series that have at least this many tilts after all " +
                     "the other filters have been applied.")]
    [RelayProperty]
    public int MinNTilts { get; set; } = 1;

    [UiDecimal("max_mask", "Maximum masked area", 0, 1, 0.01,
               helpText: "Exclude tilts if more than this fraction of their pixels is masked; " +
                         "needs frame series with BoxNet masking results.")]
    [RelayProperty]
    public decimal MaxMask { get; set; } = 1;

    #endregion

    #endregion
    
    public const string PortInMicrographs = "Micrographs";
    public const string PortOutDataSetTs = "DataSetTs";

    public ImportDataSetTs()
    {
        var portInMicrographs = new PortIn(this, typeof(MicrographSet), PortInMicrographs, "Aligned micrographs", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInMicrographs] = portInMicrographs
        });

        var portOutDataSetTs = new PortOut(this, typeof(DataSetTs), PortOutDataSetTs, "Tilt-series data set", GetDataSetResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutDataSetTs] = portOutDataSetTs
        });
    }

    private DataSetTs GetDataSetResource(int iter)
    {
        if (!PortsIn[PortInMicrographs].IsConnected)
            return null;

        var alignedMicrographs = PortsIn[PortInMicrographs].GetSingleResource<MicrographSet>();

        if (alignedMicrographs == null)
            throw new InvalidOperationException("Micrograph input not found.");
        
        if (!alignedMicrographs.HasAverage)
            throw new InvalidOperationException("Micrographs must include averages.");

        return new DataSetTs()
        {
            Micrographs = alignedMicrographs,
            DataDirectory = DirectoryPath,
            TomogramDimensions = TomogramDimensions
        };
    }

    /// <summary>
    /// Gets the name of the Warp command used for tilt series import.
    /// </summary>
    public override string CommandName => "WarpTools ts_import";

    /// <summary>
    /// Composes the command-line arguments for the motion correction and CTF estimation job.
    /// This prepares paths and parameter settings for the Warp software.
    /// </summary>
    /// <returns>A dictionary of command arguments to be passed to the Warp program.</returns>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var micrographSet = PortsIn["Micrographs"].Edges.First().Source.GetResource() as MicrographSet;

        var result = base.ComposeCommandArguments();

        result["mdocs"] = MdocDirectory;
        result["frameseries"] = PortsIn["Micrographs"].Edges.First().Source.Job.DirectoryPath;
        result["tilt_exposure"] = micrographSet.DataSetFs.OverallExposure.ToString(CultureInfo.InvariantCulture);
        result["output"] = DirectoryPath;

        return result;
    }
}