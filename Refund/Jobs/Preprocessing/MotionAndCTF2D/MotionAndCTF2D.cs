using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Preprocessing.MotionAndCTF2D;

/// <summary>
/// A preprocessing job that performs both motion correction and CTF estimation on 2D electron microscopy images.
/// This job corrects for beam-induced motion in movie frames and determines optical parameters of the microscope
/// (defocus, astigmatism, etc.), which are critical for high-quality structure determination.
/// </summary>
[GenerateReadOnly]
public class MotionAndCTF2D : WarpJobGpu, IClusterJob
{
    /// <summary>
    /// Defines the size of the job card in the workflow view.
    /// </summary>
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);

    public override string TypeGuid => "77cdcb73-1bd0-43e0-b206-3d93acecafa8";

    /// <summary>
    /// The category path for job type selection in the UI.
    /// </summary>
    public override string TypeCategory => "Frame-series.Motion & CTF.Motion and CTF";

    /// <summary>
    /// The full display name of this job type.
    /// </summary>
    public override string TypeName => "Motion & CTF";

    /// <summary>
    /// The abbreviated name used in space-constrained UI elements.
    /// </summary>
    public override string TypeNameShort => "MotionCTF";

    /// <summary>
    /// A brief description of the job's purpose.
    /// </summary>
    public override string TypeDescription => "Motion correction and CTF estimation on 2D images";

    /// <summary>
    /// Specifies that this job runs on GPU resources for faster processing.
    /// </summary>
    /// <summary>
    /// Indicates that this job doesn't support multiple iterations.
    /// Motion correction and CTF estimation are one-time preprocessing operations.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// The component type used for the expanded detailed view of this job.
    /// </summary>
    public override Type ExpandedViewType => typeof(MotionAndCTF2DExpandedView);

    public override int CoreCount => NGpus * PerDevice * 6;

    #region Parameters

    #region Motion Parameters

    /// <summary>
    /// The minimum resolution (in Angstroms) to consider during motion correction.
    /// Lower-resolution information is typically most reliable for tracking motion.
    /// </summary>
    [UiFieldGroup("Motion Processing", 0)]
    [UiDecimal("m_range_min", "Minimum resolution",
               helpText: "Minimum resolution in Angstrom to consider in motion fit",
               min: 1,
               max: 99999,
               unit: "Å")]
    [RelayProperty]
    public decimal MotionRangeMin { get; set; } = 500;

    /// <summary>
    /// The maximum resolution (in Angstroms) to consider during motion correction.
    /// Higher-resolution information can help with precise alignment but may be noisy.
    /// </summary>
    [UiDecimal("m_range_max", "Maximum resolution",
               helpText: "Maximum resolution in Angstrom to consider in motion fit",
               min: 1,
               unit: "Å")]
    [RelayProperty]
    public decimal MotionRangeMax { get; set; } = 10;

    /// <summary>
    /// The B-factor applied during motion correction to downweight high-resolution information.
    /// Negative values sharpen the data (enhance high frequencies) during alignment.
    /// </summary>
    [UiDecimal("m_bfac", "B-factor",
               helpText: "Downweight higher spatial frequencies using a B-factor",
               stepSize: 10,
               unit: "Å²")]
    [RelayProperty]
    public decimal MotionBfactor { get; set; } = -500;

    /// <summary>
    /// The resolution of the motion model grid in X, Y, and temporal dimensions.
    /// Higher values allow modeling more complex motion patterns but require more signal.
    /// </summary>
    [UiInt3("m_grid", "Model grid",
            helpText: "Resolution of the motion model grid in X, Y, and temporal dimensions, separated by 'x': e.g. 5x5x40; empty = auto")]
    [RelayProperty]
    public int3 MotionGridDims { get; set; } = new int3(1);

    #endregion

    #region CTF Parameters

    /// <summary>
    /// The patch size in binned pixels used for CTF estimation.
    /// Larger patches include more signal but may average over defocus variations.
    /// </summary>
    [UiFieldGroup("CTF Processing", 1)]
    [UiDecimal("c_window", "Patch size",
               helpText: "Patch size for CTF estimation in binned pixels",
               min: 256,
               max: 1536,
               stepSize: 256)]
    [RelayProperty]
    public decimal CTFWindow { get; set; } = 512;

    /// <summary>
    /// Controls whether to use the movie average spectrum instead of averaging individual frames' spectra.
    /// This can help with low-signal data or when imaging without an energy filter.
    /// </summary>
    [UiBool("c_use_sum", "Use movie average",
            "Use the movie average spectrum instead of the average of individual frames' spectra. Can help in the absence of an energy filter, or when signal is low.")]
    [RelayProperty]
    public bool CTFMovieSumEnable { get; set; }

    /// <summary>
    /// The resolution of the defocus model grid in X, Y, and temporal dimensions.
    /// Higher values allow modeling more complex defocus variations across the image.
    /// </summary>
    [UiInt3("c_grid", "Grid dimensions",
            helpText: "Resolution of the defocus model grid in X, Y, and temporal dimensions, separated by 'x': e.g. 5x5x40; empty = auto; Z > 1 is purely experimental")]
    [RelayProperty]
    public int3 CTFGridDims { get; set; } = new int3(1);

    /// <summary>
    /// The minimum resolution (in Angstroms) considered during CTF fitting.
    /// Typically set to exclude very low-resolution data that may be affected by background subtraction.
    /// </summary>
    [UiDecimal("c_range_min", "Minimum resolution",
               helpText: "Minimum resolution in Angstrom to consider in CTF fit",
               min: 1,
               max: 1000,
               stepSize: 1,
               unit: "Å")]
    [RelayProperty]
    public decimal CTFRangeMin { get; set; } = 30;

    /// <summary>
    /// The maximum resolution (in Angstroms) considered during CTF fitting.
    /// This should not exceed the Nyquist limit of the data.
    /// </summary>
    [UiDecimal("c_range_max", "Maximum resolution",
               helpText: "Maximum resolution in Angstrom to consider in CTF fit",
               min: 1,
               max: 1000,
               stepSize: 1,
               unit: "Å")]
    [RelayProperty]
    public decimal CTFRangeMax { get; set; } = 4.0M;

    /// <summary>
    /// The minimum defocus value (in micrometers) to explore during CTF fitting.
    /// Defines the lower bound of the defocus search range.
    /// </summary>
    [UiDecimal("c_defocus_min", "Minimum defocus",
               helpText: "Minimum defocus value to explore during fitting",
               min: -1000,
               max: 1000,
               stepSize: 0.1,
               unit: "µm")]
    [RelayProperty]
    public decimal CTFZMin { get; set; } = 0.5M;

    /// <summary>
    /// The maximum defocus value (in micrometers) to explore during CTF fitting.
    /// Defines the upper bound of the defocus search range.
    /// </summary>
    [UiDecimal("c_defocus_max", "Maximum defocus",
               helpText: "Maximum defocus value to explore during fitting",
               min: -1000,
               max: 1000,
               stepSize: 0.1,
               unit: "µm")]
    [RelayProperty]
    public decimal CTFZMax { get; set; } = 5.0M;

    /// <summary>
    /// The acceleration voltage of the microscope in kilovolts.
    /// This affects the electron wavelength and therefore the CTF calculation.
    /// </summary>
    [UiDecimal("c_voltage", "Acceleration voltage",
               helpText: "Acceleration voltage of the microscope",
               min: 10,
               max: 10000,
               stepSize: 10,
               unit: "kV")]
    [RelayProperty]
    public decimal CTFVoltage { get; set; } = 300;

    /// <summary>
    /// The spherical aberration of the microscope lens in millimeters.
    /// This is a fixed property of the objective lens and affects the CTF.
    /// </summary>
    [UiDecimal("c_cs", "Spherical aberration",
               helpText: "Spherical aberration of the microscope",
               min: 0.01,
               max: 1000,
               stepSize: 0.01,
               unit: "mm")]
    [RelayProperty]
    public decimal CTFCs { get; set; } = 2.7M;

    /// <summary>
    /// The amplitude contrast of the sample.
    /// Typically between 0.07-0.10 for cryo-EM samples.
    /// </summary>
    [UiDecimal("c_amplitude", "Amplitude contrast",
               helpText: "Amplitude contrast of the sample, usually 0.07-0.10 for cryo",
               min: 0.0,
               max: 1.0,
               stepSize: 0.01)]
    [RelayProperty]
    public decimal CTFAmplitude { get; set; } = 0.07M;

    /// <summary>
    /// Controls whether to fit the phase shift of a phase plate.
    /// Only needed when imaging with a phase plate.
    /// </summary>
    [UiBool("c_fit_phase", "Fit phase shift",
            "Fit the phase shift of a phase plate")]
    [RelayProperty]
    public bool CTFPhaseEnable { get; set; }

    #endregion

    #region Output controls

    /// <summary>
    /// Controls whether to export motion-corrected averages of the movie frames.
    /// These are typically used for downstream processing.
    /// </summary>
    [UiFieldGroup("Output", order: 2)]
    [UiBool("out_averages", "Export averages",
            helpText: "Export aligned averages")]
    [RelayProperty]
    public bool OutAverages { get; set; } = true;

    /// <summary>
    /// Controls whether to export separate averages for odd and even frames.
    /// This is useful for denoiser training or validation purposes.
    /// </summary>
    [UiBool("out_average_halves", "Export halves",
            helpText: "Export aligned averages of odd and even frames separately, e.g. for denoiser training")]
    [RelayProperty]
    public bool OutAverageHalves { get; set; }

    /// <summary>
    /// The number of frames to skip at the beginning of each movie when creating averages.
    /// Early frames often have higher motion and may reduce the quality of the average.
    /// </summary>
    [UiDecimal("out_skip_first", "Skip first N frames",
               helpText: "Skip first N frames when exporting averages",
               min: 0,
               stepSize: 1)]
    [RelayProperty]
    public decimal OutSkipFirst { get; set; } = 0;

    /// <summary>
    /// The number of frames to skip at the end of each movie when creating averages.
    /// Later frames may have accumulated radiation damage that reduces high-resolution information.
    /// </summary>
    [UiDecimal("out_skip_last", "Skip last N frames",
               helpText: "Skip last N frames when exporting averages",
               min: 0,
               stepSize: 1)]
    [RelayProperty]
    public decimal OutSkipLast { get; set; } = 0;

    #endregion

    #endregion

    #region Results paths

    /// <summary>
    /// Gets the path to a specific micrograph's motion-corrected average file.
    /// </summary>
    /// <param name="name">The name of the micrograph</param>
    /// <returns>The path to the average file</returns>
    public string ResAverageFile(string name) => Path.Combine(DirectoryPath,
                                                              Movie.ToAveragePath(name));

    /// <summary>
    /// Gets the path to a specific micrograph's motion tracks JSON file.
    /// This file contains the per-frame, per-tile motion vectors.
    /// </summary>
    /// <param name="name">The name of the micrograph</param>
    /// <returns>The path to the motion tracks file</returns>
    public string MotionTracksJsonFile(string name) => Path.Combine(DirectoryPath,
                                                                    Movie.ToMotionTracksPath(name));

    /// <summary>
    /// Gets the path to a specific micrograph's frame series XML file.
    /// This file contains CTF parameters and other metadata.
    /// </summary>
    /// <param name="name">The name of the micrograph</param>
    /// <returns>The path to the XML file</returns>
    public string FrameSeriesXmlFile(string name) => Path.Combine(DirectoryPath,
                                                                  Movie.ToXMLPath(name));

    #endregion

    #region Visualizations

    /// <summary>
    /// Gets the path to a specific micrograph's thumbnail image.
    /// These thumbnails are used for quick visualization in the UI.
    /// </summary>
    /// <param name="name">The name of the micrograph</param>
    /// <returns>The path to the thumbnail image</returns>
    public string VisThumbnail(string name) => Path.Combine(DirectoryPath,
                                                            Movie.ToThumbnailsPath(name));

    #endregion
    
    public const string PortInDataSet = "DataSet";
    public const string PortInMicrographs = "Micrographs";

    /// <summary>
    /// Initializes a new instance of the MotionAndCTF2D job.
    /// Sets up input ports for raw data and output ports for processed micrographs.
    /// </summary>
    public MotionAndCTF2D()
    {
        var portInDataSet = new PortIn(job: this,
                                       resourceType: typeof(DataSetFs),
                                       name: PortInDataSet,
                                       alias: "Data Set",
                                       minItems: 0,
                                       maxItems: 1);

        var portInMicrographs = new PortIn(job: this,
                                           resourceType: typeof(MicrographSet),
                                           name: PortInMicrographs,
                                           alias: "Micrographs",
                                           minItems: 0,
                                           maxItems: 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSet] = portInDataSet,
            [PortInMicrographs] = portInMicrographs,
        });

        var portOutMicrographSet = new PortOut(job: this,
                                               resourceType: typeof(MicrographSet),
                                               name: "Micrographs",
                                               alias: "Micrographs",
                                               resourceDelegate: GetMicrographsResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutMicrographSet.Name] = portOutMicrographSet,
        });
    }

    /// <summary>
    /// Gets the name of the Warp command used for motion correction and CTF estimation.
    /// </summary>
    public override string CommandName => "WarpTools fs_motion_and_ctf";

    /// <summary>
    /// Composes the command-line arguments for the motion correction and CTF estimation job.
    /// This prepares paths and parameter settings for the Warp software.
    /// </summary>
    /// <returns>A dictionary of command arguments to be passed to the Warp program.</returns>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));

        result["m_grid"] = $"{MotionGridDims.X}x{MotionGridDims.Y}x{MotionGridDims.Z}";
        result["c_grid"] = $"{CTFGridDims.X}x{CTFGridDims.Y}x{CTFGridDims.Z}";

        result["out_thumbnails"] = "256";

        return result;
    }

    /// <summary>
    /// Creates a MicrographSet resource from the processed output.
    /// This resource includes motion correction and CTF information.
    /// </summary>
    /// <param name="iter">The iteration number (always 0 for non-iterative jobs).</param>
    /// <returns>A MicrographSet resource containing processed micrographs.</returns>
    private Resource GetMicrographsResource(int iter)
    {
        MicrographSet result = null;

        if (PortsIn[PortInMicrographs].HasResource<MicrographSet>())
            result = PortsIn[PortInMicrographs].GetSingleResource<MicrographSet>();
        else if (PortsIn[PortInDataSet].HasResource<DataSetFs>())
            result = new MicrographSet { DataSetFs = PortsIn[PortInDataSet].GetSingleResource<DataSetFs>(), HasMovies = true };
        else
            return null;

        result.HasMotion = true;
        result.HasCtf = true;

        result.PowerspectrumDirectory = WarpHelper.PathCombine(DirectoryPath, Movie.PowerSpectrumDirName);
        result.ToPowerspectrumPath = (name) => WarpHelper.PathCombine(DirectoryPath, Movie.ToPowerSpectrumPath(name));

        if (OutAverages)
        {
            result.AverageDirectory = WarpHelper.PathCombine(DirectoryPath, Movie.AverageDirName);
            result.ToAveragePath = (name) => WarpHelper.PathCombine(DirectoryPath, Movie.ToAveragePath(name));
        }
        else
        {
            result.AverageDirectory = "";
        }

        if (OutAverageHalves)
        {
            result.AverageOddDirectory = WarpHelper.PathCombine(DirectoryPath, Movie.AverageOddDirName);
            result.AverageEvenDirectory = WarpHelper.PathCombine(DirectoryPath, Movie.AverageEvenDirName);
            
            result.ToAverageOddPath = (name) => WarpHelper.PathCombine(DirectoryPath, Movie.ToAverageOddPath(name));
            result.ToAverageEvenPath = (name) => WarpHelper.PathCombine(DirectoryPath, Movie.ToAverageEvenPath(name));
        }
        else
        {
            result.AverageOddDirectory = "";
            result.AverageEvenDirectory = "";
        }
        
        result.ThumbnailDirectory = WarpHelper.PathCombine(DirectoryPath, Movie.ThumbnailsDirName);
        result.ToThumbnailPath = (name) => WarpHelper.PathCombine(DirectoryPath, Movie.ToThumbnailsPath(name));

        result.ProcessedItemsJson = ResProcessedItemsJson;
        result.FailedItemsJson = ResFailedItemsJson;
        
        // Reset all MicrographSet parts that are invalidated by this job
        result.DenoiserModelPath = "";
        result.DenoiserTrainingDirectory = "";
        result.AverageDenoisedDirectory = "";
        result.MaskDirectory = "";

        return result;
    }

    /// <summary>
    /// Prepares the job for execution by setting up required files and directories.
    /// Converts input data into the format expected by Warp and creates the settings file.
    /// </summary>
    public override void Stage()
    {
        DataSetFs dataSetFs = null;

        if (PortsIn["DataSet"].Edges.Any())
            dataSetFs = PortsIn["DataSet"].Edges.First().Source.GetResource() as DataSetFs;
        else if (PortsIn["Micrographs"].Edges.Any())
            dataSetFs = (PortsIn["Micrographs"].Edges.First().Source.GetResource() as MicrographSet).DataSetFs;
        else
            throw new Exception("No input micrographs or dataset connected");

        OptionsWarp optionsWarp = dataSetFs.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;
        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }

    /// <summary>
    /// Generates visualizations of the processing results when available.
    /// Creates job cards showing motion tracks, CTF fits, and micrograph previews.
    /// </summary>
    /// <returns>An action to update visualization state, or null if no update is needed.</returns>
    public override Action TrackProgressResults()
    {
        var baseUpdate = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)) && NItemsProcessed > 0)
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonFsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            Movie m = new Movie(Path.Combine(DirectoryPath, processedItems[0].Path));

            BakeryWrapper.MotionAndCTF2DJobCard(m.MotionTracksPath, m.AveragePath, m.XMLPath, VisCard(0));

            return () =>
            {
                baseUpdate?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return baseUpdate;
    }
}