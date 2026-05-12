using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Ctf;

/// <summary>
/// Job that performs CTF estimation on tilt series.
/// This is based on the WarpTools CTFTiltseries command.
/// </summary>
[GenerateReadOnly]
public class Ctf : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "5b6053ff-f377-458c-99dc-bdf8e3b92699";
    public override string TypeCategory => "Tilt-series.CTF";

    public override string TypeName => "CTF estimation";

    public override string TypeNameShort => "CTF";

    public override string TypeDescription => "Estimates CTF parameters in tilt series using geometric constraints";

    public override Type ExpandedViewType => typeof(CtfExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    protected override int DefaultMemoryPerWorker => 16;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTiltSeriesSet = "TiltSeries";

    public const string PortOutTiltSeriesSet = "TiltSeries";
    
    #region Parameters

    /// <summary>
    /// Patch size for CTF estimation in binned pixels
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("CTF Estimation", 0)]
    [UiInt("window", "Patch size", min: 128, max: 1024, stepSize: 128,
           helpText: "Patch size for CTF estimation in binned pixels")]
    public int CtfWindow { get; set; } = 512;

    /// <summary>
    /// Lowest (worst) resolution in Angstrom to consider in fit
    /// </summary>
    [RelayProperty]
    [UiDecimal("range_low", "Minimum resolution", min: 1, max: 999, stepSize: 1, unit: "Å",
               helpText: "Lowest (worst) resolution in Angstrom to consider in fit")]
    public decimal RangeMin { get; set; } = 30;

    /// <summary>
    /// Highest (best) resolution in Angstrom to consider in fit
    /// </summary>
    [RelayProperty]
    [UiDecimal("range_high", "Maximum resolution", min: 1, max: 100, stepSize: 1, unit: "Å",
               helpText: "Highest (best) resolution in Angstrom to consider in fit")]
    public decimal RangeMax { get; set; } = 4;

    /// <summary>
    /// Minimum defocus value in µm to explore during fitting (positive = underfocus)
    /// </summary>
    [RelayProperty]
    [UiDecimal("defocus_min", "Minimum defocus", min: 0, max: 10, stepSize: 0.1, unit: "µm",
               helpText: "Minimum defocus value to explore during fitting (positive = underfocus)")]
    public decimal ZMin { get; set; } = 0.5M;

    /// <summary>
    /// Maximum defocus value in µm to explore during fitting (positive = underfocus)
    /// </summary>
    [RelayProperty]
    [UiDecimal("defocus_max", "Maximum defocus", min: 0, max: 20, stepSize: 0.1, unit: "µm",
               helpText: "Maximum defocus value to explore during fitting (positive = underfocus)")]
    public decimal ZMax { get; set; } = 5.0M;

    /// <summary>
    /// Acceleration voltage of the microscope in kV
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Microscope Parameters", 1)]
    [UiInt("voltage", "Acceleration voltage", min: 60, max: 5000, stepSize: 10, unit: "kV",
           helpText: "Acceleration voltage of the microscope in kV")]
    public int Voltage { get; set; } = 300;

    /// <summary>
    /// Spherical aberration of the microscope in mm
    /// </summary>
    [RelayProperty]
    [UiDecimal("cs", "Spherical aberration", min: 0.1, max: 10, stepSize: 0.01, unit: "mm",
               helpText: "Spherical aberration of the microscope")]
    public decimal Cs { get; set; } = 2.7M;

    /// <summary>
    /// Amplitude contrast of the sample, usually 0.07-0.10 for cryo
    /// </summary>
    [RelayProperty]
    [UiDecimal("amplitude", "Amplitude contrast", min: 0, max: 1, stepSize: 0.01,
               helpText: "Amplitude contrast of the sample, usually 0.07-0.10 for cryo")]
    public decimal Amplitude { get; set; } = 0.1M;

    /// <summary>
    /// Fit the phase shift of a phase plate
    /// </summary>
    [RelayProperty]
    [UiBool("fit_phase", "Fit phase shift",
            "Fit the phase shift of a phase plate")]
    public bool PhaseEnable { get; set; } = false;

    /// <summary>
    /// Run defocus handedness estimation based on this many tilt series (e.g. 10), then estimate CTF with the correct handedness
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Advanced Options", 2)]
    [UiIntNullable("auto_hand", "Use this many series to establish handedness", min: 0, max: 100, stepSize: 1,
                   helpText: "Run defocus handedness estimation based on this many tilt series (e.g. 10), then estimate " +
                             "CTF with the correct handedness. Leave empty to disable")]
    public int? AutoHand { get; set; } = null;
    
    #endregion

    public Ctf()
    {
        var portInTiltSeriesSet = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeriesSet, "Tilt-series", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTiltSeriesSet] = portInTiltSeriesSet
        });

        var portOutTiltSeriesSet = new PortOut(this, typeof(TiltSeriesSet), PortOutTiltSeriesSet, "Tilt-series with CTF data", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTiltSeriesSet] = portOutTiltSeriesSet
        });
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        if (!PortsIn[PortInTiltSeriesSet].IsConnected)
            return null;

        var resource = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();

        if (resource == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        resource.DataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");

        resource.HasMetadata = true;
        resource.LatestMetadataDirectory = DirectoryPath;

        resource.ProcessedItemsJson = ResProcessedItemsJson;
        resource.FailedItemsJson = ResFailedItemsJson;

        resource.PowerSpectrumDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.PowerSpectrumDirName);
        resource.ToPowerSpectrumPath = (name) => WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToPowerSpectrumPath(name));
        
        resource.HasCtf = true;

        return resource;
    }

    /// <summary>
    /// Gets the name of the Warp command used for CTF estimation in tilt series.
    /// </summary>
    public override string CommandName => "WarpTools ts_ctf";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));

        result["window"] = CtfWindow.ToString();
        result["range_low"] = RangeMin.ToString(CultureInfo.InvariantCulture);
        result["range_high"] = RangeMax.ToString(CultureInfo.InvariantCulture);
        result["defocus_min"] = ZMin.ToString(CultureInfo.InvariantCulture);
        result["defocus_max"] = ZMax.ToString(CultureInfo.InvariantCulture);
        result["voltage"] = Voltage.ToString();
        result["cs"] = Cs.ToString(CultureInfo.InvariantCulture);
        result["amplitude"] = Amplitude.ToString(CultureInfo.InvariantCulture);

        if (PhaseEnable)
            result["fit_phase"] = "";

        if (AutoHand is > 0)
            result["auto_hand"] = AutoHand.Value.ToString();

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        var tiltSeriesSet = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();

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
    
    public override Action TrackProgressResults()
    {
        var baseUpdate = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)) && NItemsProcessed > 1)
        {
            List<WarpTools.MiniJsonTsItem> processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            MicrographSet fsSet = GetTiltSeriesResource(0).DataSet.Micrographs;
            TiltSeries ts = new TiltSeries(Path.Combine(DirectoryPath, processedItems[0].Path));

            BakeryWrapper.TsCtfCardView(fsSet.ToThumbnailPath(processedItems[0].TiltMoviePaths[processedItems[0].TiltMoviePaths.Length / 2]),
                                        ts.XMLPath,
                                        VisCard(0));

            return () =>
            {
                baseUpdate?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return baseUpdate;
    }
}