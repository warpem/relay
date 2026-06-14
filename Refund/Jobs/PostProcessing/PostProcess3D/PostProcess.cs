using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Tools;

namespace Refund.Jobs.PostProcessing.PostProcess3D;

/// <summary>
/// A job that filters and sharpens a refined 3D map using RELION's post-processing tools.
/// This job applies B-factor sharpening, FSC-based filtering, and mask-based processing
/// to improve the visual and analytical quality of 3D reconstructions after refinement.
/// 
/// The PostProcess job is used to enhance map interpretability and is typically run after
/// 3D refinement is complete. It acts as a crucial final step in the reconstruction process
/// to optimize map visualization for interpretation and model building.
/// 
/// In usage, this job is instantiated programmatically as shown in JobDev testing:
/// <code>
/// PostProcess ppJob = new PostProcess();
/// ppJob.Space = Space;
/// ppJob.Id = 3;
/// ppJob.DirectoryName = "TestPostProcess";
/// </code>
/// 
/// The job integrates with the ClusterQueue system, which uses its CommandName and
/// ComposeCommandArguments methods to build the RELION command for execution.
/// </summary>
[GenerateReadOnly]
public class  PostProcess : RelionJob, IClusterJob
{
    /// <summary>
    /// Defines the size of the job card in the workflow view.
    /// </summary>
    public override int2 CardSquareCount { get; set; } = new(2, 1);

    public override string TypeGuid => "7c629810-0528-482f-90e0-d0be5c364f99";

    /// <summary>
    /// The category path for job type selection in the UI.
    /// </summary>
    public override string TypeCategory => "Refinement.Post-process.Post-process";
    
    /// <summary>
    /// The full display name of this job type.
    /// </summary>
    public override string TypeName => "Post-processing";
    
    /// <summary>
    /// The abbreviated name used in space-constrained UI elements.
    /// </summary>
    public override string TypeNameShort => "PostProcess";
    
    /// <summary>
    /// A brief description of the job's purpose.
    /// </summary>
    public override string TypeDescription => "Filter and sharpen a refined 3D map";
    
    /// <summary>
    /// Specifies that this job runs on CPU resources rather than GPU.
    /// Post-processing is typically CPU-bound and doesn't require GPU acceleration.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.CPU;
    
    /// <summary>
    /// This job doesn't have a custom expanded view - it uses the standard job view.
    /// </summary>
    public override Type ExpandedViewType => typeof(PostProcessExpandedView);

    /// <summary>
    /// Indicates that this job doesn't support multiple iterations.
    /// Post-processing is a one-time operation applied to a final map.
    /// </summary>
    public override bool IsIterative => false;
    
    #region Parameters
    
    #region Maps

    /// <summary>
    /// The calibrated pixel size (in Angstroms) for the final map.
    /// This may differ from the pixel size used during refinement if
    /// recalibration was performed based on a reference model.
    /// </summary>
    [UiFieldGroup("Maps", 0)]
    [UiDecimal("angpix", "Calibrated pixel size", 
               min: -1, max: 100000, stepSize: 0.001,
               helpText: "Provide the final, calibrated pixel size in Angstroms. This value may be different from the " +
                         "pixel-size used thus far, e.g. when you have recalibrated the pixel size using the fit to a " +
                         "PDB model. The X-axis of the output FSC plot will use this calibrated value.")]
    [RelayProperty]
    public decimal CalibratedPixelSize { get; set; } = -1;
    
    #endregion
    
    #region Sharpen
    
    /// <summary>
    /// Controls whether B-factor is estimated automatically using the
    /// Rosenthal and Henderson (2003) method, which analyzes the Guinier plot
    /// to determine appropriate sharpening parameters.
    /// </summary>
    [UiFieldGroup("Sharpen", 1)]
    [UiBool("auto_bfac", "Estimate B-factor automatically",
            helpText: "If set to Yes, then the program will use the automated procedure described by Rosenthal and " +
                      "Henderson (2003, JMB) to estimate an overall B-factor for your map, and sharpen it accordingly. " +
                      "Note that your map must extend well beyond the lowest resolution included in the procedure below, " +
                      "which should not be set to resolutions much lower than 10 Angstroms. ")]
    [RelayProperty]
    public bool EstimateBfactor { get; set; } = true;
    
    /// <summary>
    /// The lowest resolution (in Angstroms) included in the B-factor estimation.
    /// This parameter is typically set around 10Å and used in the linear fitting
    /// of the Guinier plot during automatic B-factor estimation.
    /// </summary>
    [UiDecimal("autob_lowres", "Lowest resolution for fit", min: 1, max: 10000, stepSize: 0.1,
               helpText: "This is the lowest frequency (in Angstroms) that will be included in the linear fit of the " +
                         "Guinier plot as described in Rosenthal and Henderson (2003, JMB). Dont use values much lower or " +
                         "higher than 10 Angstroms. If your map does not extend beyond 10 Angstroms, then instead of the " +
                         "automated procedure use your own B-factor.",
               Unit = "Å", 
               ConditionalOnField = nameof(EstimateBfactor), 
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal LowestResolutionForFit { get; set; } = 10;
    
    /// <summary>
    /// A manually specified B-factor to apply when automatic estimation is disabled.
    /// Negative values sharpen the map (enhance high-resolution features),
    /// while positive values blur the map (enhance low-resolution features).
    /// </summary>
    [UiInt("adhoc_bfac", "Manual B-factor", min: -100000, max: 100000, stepSize: 10,
           helpText: "Use negative values for sharpening. Be careful: if you over-sharpen your map, you may end up " +
                     "interpreting noise for signal!",
           ConditionalOnField = nameof(EstimateBfactor),
           ConditionalOnValue = false)]
    [RelayProperty]
    public int ManualBfactor { get; set; } = 0;
    
    /// <summary>
    /// Controls whether to skip FSC-based filtering, which normally applies
    /// an optimal filter based on the resolution-dependent signal-to-noise ratio.
    /// When disabled, the map is filtered using the gold-standard FSC curve.
    /// </summary>
    [UiBool("skip_fsc_weighting", "Skip FSC weighting",
            isAdvanced: true,
            helpText: "If set to No (the default), then the output map will be low-pass filtered according to the " +
                      "mask-corrected, gold-standard FSC-curve. Sometimes, it is also useful to provide an ad-hoc " +
                      "low-pass filter (option below), as due to local resolution variations some parts of the map may " +
                      "be better and other parts may be worse than the overall resolution as measured by the FSC. In such " +
                      "cases, set this option to Yes and provide an ad-hoc filter as described below.")]
    [RelayProperty]
    public bool SkipFscWeighting { get; set; } = false;
    
    /// <summary>
    /// The resolution (in Angstroms) to use for manual low-pass filtering
    /// when FSC-based weighting is disabled. This allows for custom filtering
    /// that may be needed in cases of local resolution variation.
    /// </summary>
    [UiDecimal("low_pass", "Manual low-pass filter", 0, 100000, 0.1,
               isAdvanced: true,
               helpText: "This option allows one to low-pass filter the map at a user-provided frequency (in Angstroms). " +
                         "When using a resolution that is higher than the gold-standard FSC-reported resolution, take " +
                         "care not to interpret noise in the map for signal.",
               ConditionalOnField = nameof(SkipFscWeighting),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal ManualLowPassFilter { get; set; } = 0;
    
    /// <summary>
    /// Path to a file containing the modulation transfer function (MTF) of the detector.
    /// The MTF describes how the detector transfers different spatial frequencies
    /// and is used for more accurate map correction during post-processing.
    /// </summary>
    [UiPath("mtf", "Detector MTF", SelectionMode.SingleFile, ["*.star"],
            isAdvanced: true,
            helpText: "If you know the MTF of your detector, provide it here. Curves for some well-known detectors may " +
                      "be downloaded from the RELION Wiki. Also see there for the exact format. If you do not know the " +
                      "MTF of your detector and do not want to measure it, then by leaving this entry empty, you include " +
                      "the MTF of your detector in your overall estimated B-factor upon sharpening the map. Although that " +
                      "is probably slightly less accurate, the overall quality of your map will probably not suffer very much.")]
    [RelayProperty]
    public string DetectorMtf { get; set; } = "";
    
    /// <summary>
    /// The original pixel size (in Angstroms) of the raw micrographs from the detector.
    /// This value is used for proper scaling of the detector MTF correction.
    /// </summary>
    [UiDecimal("mtf_angpix", "Original detector pixel size", 0, 10000, 0.001,
               isAdvanced: true,
               helpText: "This is the original pixel size (in Angstroms) in the raw (non-super-resolution!) micrographs.",
               Unit = "Å",
               ConditionalOnField = nameof(DetectorMtf))]
    [RelayProperty]
    public decimal OriginalDetectorPixelSize { get; set; } = 1;
    
    #endregion
    
    #region Compute
    
    /// <summary>
    /// The number of CPU threads to use for computation.
    /// Post-processing can be parallelized to speed up processing.
    /// </summary>
    [UiFieldGroup("Compute", 2)]
    [UiDecimal("j", "Number of threads",
               1,
               99999,
               1,
               helpText: "Number of threads to parallelize the computation.")]
    [RelayProperty]
    public decimal NThreads { get; set; } = 1;

    /// <summary>
    /// Additional command-line arguments to pass to the post-processing program.
    /// This allows for advanced options not exposed in the standard UI.
    /// </summary>
    [UiString("", "Additional arguments",
              isAdvanced: true,
              helpText: "In this box command-line arguments may be provided that are not generated " +
                        "by the GUI. This may be useful for testing developmental options and/or " +
                        "expert use of the program. Specify as --option1 value1 --option2 value2")]
    [RelayProperty]
    public string AdditionalArguments { get; set; } = "";
    
    #endregion
    
    #endregion
    
    #region Results paths

    /// <summary>
    /// Path to the unmasked post-processed map file.
    /// </summary>
    private string ResMapFile => Path.Combine(DirectoryPath, "postprocess.mrc");
    
    /// <summary>
    /// Path to the masked post-processed map file.
    /// </summary>
    private string ResMapMaskedFile => Path.Combine(DirectoryPath, "postprocess_masked.mrc");
    
    /// <summary>
    /// Path to the STAR file containing post-processing metadata.
    /// </summary>
    private string ResStarFile => Path.Combine(DirectoryPath, "postprocess.star");
    
    /// <summary>
    /// Path to the XML file containing FSC data.
    /// </summary>
    private string ResXmlFile() => Path.Combine(DirectoryPath, "postprocess_fsc.xml");
    
    /// <summary>
    /// Path to the log plot file.
    /// </summary>
    private string ResPlotFile() => Path.Combine(DirectoryPath, "logfile.pdf");
    
    #endregion
    
    #region Visualization paths

    /// <summary>
    /// Path to the 2D orthogonal slices visualization of the post-processed map.
    /// </summary>
    public string VisFilteredSlices => Path.Combine(RelayResultsDirectoryPath,
                                                      "filtered_slices.png");
    
    /// <summary>
    /// Path to the mask isolines visualization.
    /// </summary>
    public string VisMaskIsolines => Path.Combine(RelayResultsDirectoryPath, 
                                                    "mask_isolines.png");

    /// <summary>
    /// Path to the FSC curve visualization.
    /// </summary>
    public string VisFsc => Path.Combine(RelayResultsDirectoryPath,
                                                       "fsc.png");

    /// <summary>
    /// Path to the Guinier plot visualization used for B-factor estimation.
    /// </summary>
    public string VisGuinier => Path.Combine(RelayResultsDirectoryPath,
                                                           "guinier.png");

    public string VisMap3d => ResMapFile;
    
    #endregion
    
    public const string PortInMap = "Map";
    public const string PortInMask = "Mask";
    public const string PortOutMapUnmasked = "MapUnmasked";
    public const string PortOutMapMasked = "MapMasked";

    /// <summary>
    /// Initializes a new instance of the PostProcess job.
    /// Sets up input ports for the refined map and mask, and output ports
    /// for the unmasked and masked post-processed maps.
    /// </summary>
    public PostProcess()
    {
        var portInMap = new PortIn(this, typeof(MapList), PortInMap, "Refined map", 1, 1);
        var portInMask = new PortIn(this, typeof(Mask), PortInMask, "Mask", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInMap.Name] = portInMap,
            [portInMask.Name] = portInMask
        });

        var portOutMapUnmasked = new PortOut(this, typeof(MapList), PortOutMapUnmasked, "Map", GetMapResource);
        var portOutMapMasked = new PortOut(this, typeof(MapList), PortOutMapMasked, "Masked map", GetMaskedMapResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutMapUnmasked.Name] = portOutMapUnmasked,
            [portOutMapMasked.Name] = portOutMapMasked
        });
    }
    
    /// <summary>
    /// Creates a Map resource from the unmasked post-processed map output.
    /// </summary>
    /// <param name="iter">The iteration number (always 0 for non-iterative jobs).</param>
    /// <returns>A Map resource containing the post-processed map and FSC data.</returns>
    private Resource GetMapResource(int iter)
    {
        return new MapList([new Map(averageVolumePath: ResMapFile, fscStarPath: ResStarFile)]);
    }

    /// <summary>
    /// Creates a Map resource from the masked post-processed map output.
    /// </summary>
    /// <param name="iter">The iteration number (always 0 for non-iterative jobs).</param>
    /// <returns>A Map resource containing the masked post-processed map and FSC data.</returns>
    private Resource GetMaskedMapResource(int iter)
    {
        if (PortsIn[PortInMask].IsConnected == false)
            return null;
        
        return new MapList([new Map(averageVolumePath: ResMapMaskedFile, fscStarPath: ResStarFile)]);
    }
    

    /// <summary>
    /// Gets the name of the RELION command used for post-processing.
    /// </summary>
    public override string CommandName => "relion_postprocess";

    /// <summary>
    /// Composes the command-line arguments for the post-processing job.
    /// This prepares input/output paths and parameter settings for RELION.
    /// </summary>
    /// <returns>A dictionary of command arguments to be passed to the RELION post-processing program.</returns>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();
        
        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        var map = (PortsIn[PortInMap].Edges.First().Source.GetResource() as MapList).Maps.First();
        Mask mask = null;
        if (PortsIn[PortInMask].IsConnected)
            mask = PortsIn[PortInMask].Edges.First().Source.GetResource() as Mask;
        
        if (string.IsNullOrWhiteSpace(map.Half1VolumePath) ||
            string.IsNullOrWhiteSpace(map.Half2VolumePath))
            throw new Exception("Map does not contain half-maps");
        
        result["i"] = Space.GetRelativePath(map.Half1VolumePath);
        result["i2"] = Space.GetRelativePath(map.Half2VolumePath);
        
        if (mask != null)
            result["mask"] = Space.GetRelativePath(mask.MaskVolumePath);
        
        result["o"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "postprocess"));

        return result;
    }
    /// <summary>
    /// Tracks the size of the log file to detect changes.
    /// </summary>
    private long LastLogSize = -1;

    /// <summary>
    /// Monitors and processes log output from the running job.
    /// This method reads the RELION output log, processes it, and makes it
    /// available for display in the UI.
    /// </summary>
    /// <returns>An action to update the log availability status, or null if no update is needed.</returns>
    public override Action TrackProgressLogs()
    {
        // Ensure results directory exists
        JobTools.EnsureResultsDirectory(RelayResultsDirectoryPath);
        
        int MaxLogsExist = -1;

        #region Track logs
        
        // Check if log file has changed since last check
        if (JobTools.HasLogFileChanged(PathStdOut, ref LastLogSize))
        {
            MaxLogsExist = 0;
            
            // Read and clean log lines
            string[] logLines = File.ReadAllText(PathStdOut).Split('\n');
            logLines = JobTools.CleanProgressBarLines(logLines);

            // Save processed logs
            JobTools.WriteLogFile(string.Join('\n', logLines), LogFilePath(0));
        }
        else if (File.Exists(PathStdOut))
        {
            // No change in log file, keep previous iteration value
            MaxLogsExist = Math.Max(MaxLogsExist, LogsAvailableIteration);
        }
        
        #endregion

        bool ReportUpdate = MaxLogsExist > LogsAvailableIteration;

        if (ReportUpdate)
            return () =>
            {
                LogsAvailableIteration = MaxLogsExist;
            };
        else
            return null;
    }
    
    /// <summary>
    /// Generates visualizations of the post-processing results when available.
    /// This method creates orthoslice views of the map, FSC curves, and Guinier plots
    /// to help users assess the quality of the post-processing.
    /// </summary>
    /// <returns>An action to update the visualization availability status, or null if no update is needed.</returns>
    public override Action TrackProgressResults()
    {
        // Ensure results directory exists
        JobTools.EnsureResultsDirectory(RelayResultsDirectoryPath);

        int MaxResultsExist = -1;

        if (!File.Exists(ResMapFile))
            return null;
        
        // If HasFinished == true, visualize all remaining results,
        // otherwise only the first iterations that doesn't have vis yet
        if (!File.Exists(VisFilteredSlices))
        {
            List<Task> VisTasks = new();

            // All iterations have the same name convention
            VisTasks.Add(Task.Run(() =>
            {
                if (File.Exists(ResMapFile))
                    BakeryWrapper.MapOrthosliceAtlas(ResMapFile,
                                                     1,
                                                     VisFilteredSlices);
            }));
            
            VisTasks.Add(Task.Run(() =>
            {
                if (File.Exists(ResStarFile))
                    BakeryWrapper.PostProcess3DFSCAndGuinier(ResStarFile, VisFsc, VisGuinier);
            }));
            
            VisTasks.Add(Task.Run(() =>
            {
                BakeryWrapper.PostProcess3DJobCard(ResMapFile,
                                                   ResStarFile,
                                                   VisCard(0));
            }));

            Task.WaitAll(VisTasks.ToArray());
        }

        MaxResultsExist = 0;

        bool ReportUpdate = MaxResultsExist > VisAvailableIteration;

        if (ReportUpdate)
            return () =>
            {
                VisAvailableIteration = MaxResultsExist;
            };
        else
            return null;
    }
}